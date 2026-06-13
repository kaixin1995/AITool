using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧开发者追踪内存存储。
/// <para>
/// 消费 Core 发布的 developer-trace 事件后，将摘要信息缓存在内存中，
/// 供 Admin 的开发者调试页面查询展示。
/// </para>
/// <para>
/// 与 Core 侧的 <c>DeveloperInvocationTraceStore</c> 类似，但存储粒度不同：
/// Core 侧保存完整的追踪记录（含请求体、响应体、请求头等），
/// Admin 侧保存完整统一代理事件（CoreUnifiedProxyEvent），包含请求体、响应体和所有尝试明细。
/// </para>
/// <para>
/// 数据特性：最多保留 100 条，6 小时过期，线程安全。
/// </para>
/// </summary>
public sealed class AdminDeveloperTraceStore
{
    /// <summary>
    /// 最大保留记录数。
    /// </summary>
    private const int MaxEntryCount = 100;

    /// <summary>
    /// 记录保留时长。
    /// </summary>
    private static readonly TimeSpan EntryRetention = TimeSpan.FromHours(6);

    /// <summary>
    /// 并发访问锁对象。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 按完成时间倒序排列的追踪记录列表（最新在前）。
    /// </summary>
    private readonly LinkedList<CoreUnifiedProxyEvent> _entries = [];

    /// <summary>
    /// 以 TraceId 为主键的节点索引，用于去重和按 ID 查询。
    /// </summary>
    private readonly Dictionary<Guid, LinkedListNode<CoreUnifiedProxyEvent>> _nodes = [];

    /// <summary>
    /// 添加或更新一条统一代理事件。
    /// 如果相同 TraceId 已存在，则用新数据替换旧数据（支持事件重放场景）。
    /// </summary>
    public void Upsert(CoreUnifiedProxyEvent traceEvent)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);

        lock (_gate)
        {
            PurgeExpiredUnsafe();

            // 如果已存在同 TraceId 的记录，先移除旧节点再插入新数据
            if (_nodes.TryGetValue(traceEvent.TraceId, out var existingNode))
            {
                _entries.Remove(existingNode);
            }

            var node = _entries.AddFirst(traceEvent);
            _nodes[traceEvent.TraceId] = node;
            TrimUnsafe();
        }
    }

    /// <summary>
    /// 批量添加开发者追踪事件，自动按 TraceId 去重。
    /// </summary>
    public void UpsertRange(IEnumerable<CoreUnifiedProxyEvent> events)
    {
        foreach (var evt in events)
        {
            Upsert(evt);
        }
    }

    /// <summary>
    /// 获取所有追踪记录的快照（按完成时间倒序）。
    /// 返回的是深拷贝列表，调用方可以安全地在任意线程使用。
    /// </summary>
    public IReadOnlyList<CoreUnifiedProxyEvent> List()
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            return _entries.Select(Clone).ToList();
        }
    }

    /// <summary>
    /// 按 TraceId 获取单条追踪记录，返回深拷贝。
    /// </summary>
    public CoreUnifiedProxyEvent? Get(Guid traceId)
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            return _nodes.TryGetValue(traceId, out var node) ? Clone(node.Value) : null;
        }
    }

    /// <summary>
    /// 当前存储的记录总数。
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// 获取统计摘要信息，供 Admin 页面初始加载时使用。
    /// </summary>
    public (int TotalCount, int FailedCount, int PendingCount) GetSummary()
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            var totalCount = _entries.Count;
            var failedCount = _entries.Count(e =>
                string.Equals(e.Status, "error", StringComparison.OrdinalIgnoreCase));
            var pendingCount = _entries.Count(e =>
                string.Equals(e.Status, "pending", StringComparison.OrdinalIgnoreCase));
            return (totalCount, failedCount, pendingCount);
        }
    }

    /// <summary>
    /// 清理过期记录（在锁内部调用）。
    /// </summary>
    private void PurgeExpiredUnsafe()
    {
        var expireBefore = DateTimeOffset.UtcNow - EntryRetention;
        while (_entries.Last is { } last && last.Value.FinishedAt < expireBefore)
        {
            _nodes.Remove(last.Value.TraceId);
            _entries.RemoveLast();
        }
    }

    /// <summary>
    /// 裁剪超出上限的记录（在锁内部调用）。
    /// </summary>
    private void TrimUnsafe()
    {
        while (_entries.Count > MaxEntryCount)
        {
            var last = _entries.Last;
            if (last is null)
            {
                break;
            }

            _nodes.Remove(last.Value.TraceId);
            _entries.RemoveLast();
        }
    }

    /// <summary>
    /// 深拷贝一个 <see cref="CoreUnifiedProxyEvent"/>，包括所有尝试明细。
    /// </summary>
    private static CoreUnifiedProxyEvent Clone(CoreUnifiedProxyEvent source)
    {
        return new CoreUnifiedProxyEvent
        {
            // ─── 来自 CoreUsageLogEvent ───
            RequestId = source.RequestId,
            AccessKeyId = source.AccessKeyId,
            ProtocolType = source.ProtocolType,
            ForwardingMode = source.ForwardingMode,
            RequestModel = source.RequestModel,
            AttemptedModel = source.AttemptedModel,
            TargetSiteId = source.TargetSiteId,
            Status = source.Status,
            Source = source.Source,
            RetryCount = source.RetryCount,
            AttemptIndex = source.AttemptIndex,
            IsFinalResult = source.IsFinalResult,
            FallbackTriggered = source.FallbackTriggered,
            ErrorMessage = source.ErrorMessage,
            InputTokens = source.InputTokens,
            CachedTokens = source.CachedTokens,
            OutputTokens = source.OutputTokens,
            IsStreaming = source.IsStreaming,
            IsStreamInterrupted = source.IsStreamInterrupted,
            FirstTokenLatencyMs = source.FirstTokenLatencyMs,
            StreamDurationMs = source.StreamDurationMs,
            TotalDurationMs = source.TotalDurationMs,
            ReasoningEffort = source.ReasoningEffort,
            RequestedAt = source.RequestedAt,

            // ─── 来自 CoreDeveloperTraceEvent ───
            TraceId = source.TraceId,
            TargetSiteName = source.TargetSiteName,
            StartedAt = source.StartedAt,
            FinishedAt = source.FinishedAt,

            // ─── 完整请求/响应数据 ───
            RequestBody = source.RequestBody,
            ResponseBody = source.ResponseBody,
            RequestHeaders = new Dictionary<string, string>(source.RequestHeaders),
            ClientIp = source.ClientIp,
            UserAgent = source.UserAgent,
            RequestPath = source.RequestPath,
            StatusCode = source.StatusCode,
            ResponseContentType = source.ResponseContentType,

            // ─── 深拷贝尝试明细 ───
            Attempts = source.Attempts.Select(a => new CoreUnifiedAttemptDetail
            {
                AttemptId = a.AttemptId,
                AttemptIndex = a.AttemptIndex,
                AttemptedModel = a.AttemptedModel,
                UpstreamProtocolType = a.UpstreamProtocolType,
                ForwardingMode = a.ForwardingMode,
                TargetSiteId = a.TargetSiteId,
                TargetSiteName = a.TargetSiteName,
                Status = a.Status,
                StatusCode = a.StatusCode,
                ErrorMessage = a.ErrorMessage,
                ResponseBody = a.ResponseBody,
                ResponseContentType = a.ResponseContentType,
                IsStreaming = a.IsStreaming,
                IsStreamInterrupted = a.IsStreamInterrupted,
                InputTokens = a.InputTokens,
                CachedTokens = a.CachedTokens,
                OutputTokens = a.OutputTokens,
                TotalDurationMs = a.TotalDurationMs,
                FirstTokenLatencyMs = a.FirstTokenLatencyMs,
                StreamDurationMs = a.StreamDurationMs,
                StartedAt = a.StartedAt,
                FinishedAt = a.FinishedAt
            }).ToList()
        };
    }
}
