using System.Collections.Concurrent;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 流式响应可恢复缓冲存储：为每个活跃流式请求缓存 SSE 原始帧（切帧见 <see cref="SseFrameSplitter"/>），
/// 供断线方以 <c>X-Stream-Resume: {requestId}:{lastSeq}</c> 重入后重放，实现断点续传。
/// <para>
/// 内存护栏（与 Core 256MB 堆上限配套）：
/// - 单请求缓冲上限 2MB（超限标记不可恢复并停止追加）；
/// - 并发缓冲流上限 50（超限直接拒绝新缓冲，调用方按不可恢复处理）；
/// - 缓冲区在流结束/断开后保留 MaxRetainMinutes（默认 5 分钟）供迟到重连，随后惰性回收。
/// </para>
/// </summary>
public sealed class StreamResumeStore
{
    private sealed class BufferEntry
    {
        public required string RequestId { get; init; }
        public List<byte[]> Frames { get; } = [];
        public long TotalBytes { get; set; }
        public int LastSequence { get; set; }
        public bool Truncated { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
        public bool IsCompleted { get; set; }
    }

    public const int DefaultMaxFrameBytesPerRequest = 2 * 1024 * 1024;
    public const int DefaultMaxActiveStreams = 50;
    public static readonly TimeSpan DefaultRetainWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, BufferEntry> _entries = new();
    private readonly int _maxFrameBytesPerRequest;
    private readonly int _maxActiveStreams;
    private readonly TimeSpan _retainWindow;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);
    private readonly object _sweepGate = new();
    private readonly CancellationTokenSource _cts = new();

    public StreamResumeStore(
        int maxFrameBytesPerRequest = DefaultMaxFrameBytesPerRequest,
        int maxActiveStreams = DefaultMaxActiveStreams,
        TimeSpan? retainWindow = null)
    {
        _maxFrameBytesPerRequest = maxFrameBytesPerRequest;
        _maxActiveStreams = maxActiveStreams;
        _retainWindow = retainWindow ?? DefaultRetainWindow;
        _ = Task.Run(BackgroundSweepAsync);
    }

    /// <summary>
    /// 注册一个新流。并发缓冲满时返回 false（调用方按不可恢复处理，正常转发不受影响）。
    /// </summary>
    public bool TryBegin(string requestId)
    {
        if (!string.IsNullOrEmpty(requestId) && _entries.TryAdd(requestId, new BufferEntry { RequestId = requestId }))
        {
            return _entries.Count <= _maxActiveStreams;
        }

        return false;
    }

    /// <summary>
    /// 追加一帧（调用方保证为完整 SSE 帧的原始字节）。
    /// 缓冲超限后返回 false 并停止追加；帧之间加锁，序列号单调递增。
    /// </summary>
    public bool Append(string requestId, byte[] rawFrame)
    {
        if (!_entries.TryGetValue(requestId, out var entry) || entry.Truncated || entry.IsCompleted)
        {
            return false;
        }

        lock (entry)
        {
            if (entry.Truncated || entry.IsCompleted)
            {
                return false;
            }

            entry.TotalBytes += rawFrame.Length;
            if (entry.TotalBytes > _maxFrameBytesPerRequest)
            {
                entry.Truncated = true;
                entry.Frames.Clear();
                return false;
            }

            entry.Frames.Add(rawFrame);
            entry.LastSequence++;
            return true;
        }
    }

    /// <summary>
    /// 标记流结束：进入宽限保留窗口，供断线方在窗口内重放。
    /// </summary>
    public void MarkCompleted(string requestId)
    {
        if (_entries.TryGetValue(requestId, out var entry))
        {
            lock (entry)
            {
                entry.IsCompleted = true;
                entry.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// 尝试断点续传：返回（lastSeq+1）起的所有完整帧。
    /// <para>
    /// - 未知 requestId / 已过期收割 / 已截断 → null；
    /// - lastSeq 超前于缓冲（客户端比服务器还新，帧丢失）→ 空列表 + 标记状态，调用方需从头重来；
    /// - 正常 → 帧列表（原始字节）。
    /// </para>
    /// </summary>
    public IReadOnlyList<byte[]>? TryResume(string requestId, int lastSeq)
    {
        if (!_entries.TryGetValue(requestId, out var entry) || entry.Truncated)
        {
            return null;
        }

        lock (entry)
        {
            if (entry.Truncated)
            {
                return null;
            }

            if (lastSeq >= entry.LastSequence)
            {
                // 客户端已拥有全部已缓冲帧（或更多）：无帧可重放，但流仍可衔接。
                return [];
            }

            var fromIndex = Math.Max(0, lastSeq); // seq 从 1 起：lastSeq=0 → 从第 1 帧开始
            if (fromIndex > entry.Frames.Count)
            {
                return null;
            }

            return entry.Frames.Skip(fromIndex).Take(entry.Frames.Count - fromIndex).ToList();
        }
    }

    /// <summary>
    /// 查询是否存在可恢复缓冲（供响应侧决定是否发出 X-Stream-Request-Id）。
    /// </summary>
    public bool HasBuffer(string requestId) => _entries.ContainsKey(requestId);

    private void BackgroundSweepAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Task.Delay(_sweepInterval, _cts.Token).Wait(_cts.Token);
                SweepExpired();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
    }

    private void SweepExpired()
    {
        lock (_sweepGate)
        {
            foreach (var (requestId, entry) in _entries)
            {
                if (entry.IsCompleted && DateTimeOffset.UtcNow - entry.CompletedAt > _retainWindow)
                {
                    _entries.TryRemove(requestId, out _);
                }
            }
        }
    }
}