using System.Collections.Concurrent;
using System.Threading;

namespace AITool.Web.Services;

/// <summary>
/// 并发打满时的处理策略。
/// </summary>
public enum ConcurrencyAcquireMode
{
    /// <summary>
    /// 跳到下一顺位模型。
    /// </summary>
    SkipOnFull = 0,
    /// <summary>
    /// 排队等待直到释放或超时。
    /// </summary>
    WaitForSlot = 1
}

/// <summary>
/// 并发获取结果，表示是否成功拿到许可，以及用于释放的句柄。
/// </summary>
public sealed class ConcurrencyAcquireResult : IDisposable
{
    /// <summary>
    /// 是否成功获取到并发许可。
    /// </summary>
    public bool Acquired { get; }

    private readonly Action? _releaseAction;
    private int _disposed;

    private ConcurrencyAcquireResult(bool acquired, Action? releaseAction)
    {
        Acquired = acquired;
        _releaseAction = releaseAction;
    }

    /// <summary>
    /// 未获取到许可的实例。
    /// </summary>
    public static ConcurrencyAcquireResult NotAcquired { get; } = new(false, null);

    /// <summary>
    /// 成功获取许可的实例。
    /// </summary>
    public static ConcurrencyAcquireResult AcquiredSlot(Action? releaseAction = null) => new(true, releaseAction);

    /// <summary>
    /// 释放并发许可，无论成功或异常都会正确释放。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _releaseAction?.Invoke();
    }
}

/// <summary>
/// 模型并发快照，用于调试页面展示最近出现过的站点模型及其实时并发数。
/// </summary>
public sealed class ActiveModelConcurrencyEntry
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; init; }
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; init; } = string.Empty;
    /// <summary>
    /// 当前活跃并发数。
    /// </summary>
    public int ActiveCount { get; init; }
    /// <summary>
    /// 最近一次进入或离开活跃态的时间。
    /// </summary>
    public DateTimeOffset LastSeenAt { get; init; }
}

/// <summary>
/// 按 SiteId + RemoteModelName 粒度控制最大并发数。
/// 限制为 0 表示不限制，请求直接通过；大于 0 时根据模式跳过或排队。
/// </summary>
public sealed class ModelConcurrencyLimiter
{
    /// <summary>
    /// 初始化模型并发限制器。
    /// </summary>
    public ModelConcurrencyLimiter(ProxyRequestMetadataCache metadataCache)
    {
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 每个 SiteId + RemoteModelName 组合对应的并发状态。
    /// </summary>
    private readonly ConcurrentDictionary<string, ModelConcurrencyState> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// 最近出现过的模型并发展示元数据，归零后仍会保留一段时间，避免调试页瞬间清空。
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveModelConcurrencyEntry> _activeEntries = new(StringComparer.Ordinal);

    /// <summary>
    /// 代理请求元数据缓存，用于统一复用内存中的并发配置。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 调试页默认保留最近 6 小时内出现过的模型并发记录。
    /// </summary>
    public static readonly TimeSpan RecentRetention = TimeSpan.FromHours(6);

    /// <summary>
    /// 按模式获取指定站点模型的并发许可。
    /// SkipOnFull：打满立即返回 NotAcquired；
    /// WaitForSlot：排队等待直到释放或超时。
    /// </summary>
    public async ValueTask<ConcurrencyAcquireResult> AcquireAsync(
        IServiceProvider serviceProvider,
        Guid siteId,
        string remoteModelName,
        ConcurrencyAcquireMode mode,
        TimeSpan queueTimeout,
        CancellationToken cancellationToken)
    {
        var limits = await _metadataCache.GetModelConcurrencyLimitsAsync(cancellationToken);
        var key = BuildKey(siteId, remoteModelName);
        var maxConcurrency = limits.TryGetValue(key, out var configuredLimit)
            ? configuredLimit
            : 0;
        var state = _states.GetOrAdd(key, _ => new ModelConcurrencyState());

        List<QueuedAcquireWaiter>? promotedWaiters = null;
        LinkedListNode<QueuedAcquireWaiter>? waiterNode = null;
        QueuedAcquireWaiter? waiter = null;
        var acquired = false;
        var activeCount = 0;

        lock (state.SyncRoot)
        {
            state.MaxConcurrency = maxConcurrency;
            promotedWaiters = PromoteQueuedWaitersLocked(state);

            if (CanAcquireImmediatelyLocked(state))
            {
                state.ActiveCount++;
                activeCount = state.ActiveCount;
                acquired = true;
            }
            else if (mode == ConcurrencyAcquireMode.WaitForSlot)
            {
                waiter = new QueuedAcquireWaiter();
                waiterNode = state.Waiters.AddLast(waiter);
            }
        }

        ReleaseQueuedWaiters(promotedWaiters, key, siteId, remoteModelName, state.ActiveCount);

        if (acquired)
        {
            UpdateActiveEntry(key, siteId, remoteModelName, activeCount);
            return CreateTrackedAcquireResult(key, siteId, remoteModelName);
        }

        if (mode == ConcurrencyAcquireMode.SkipOnFull)
        {
            return ConcurrencyAcquireResult.NotAcquired;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(queueTimeout);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        var completedTask = await Task.WhenAny(waiter!.Completion.Task, cancellationTask);

        if (completedTask == waiter.Completion.Task)
        {
            UpdateActiveEntry(key, siteId, remoteModelName, GetActiveCount(state));
            return CreateTrackedAcquireResult(key, siteId, remoteModelName);
        }

        var grantedDuringCancellation = false;
        lock (state.SyncRoot)
        {
            if (waiter.Granted)
            {
                grantedDuringCancellation = true;
            }
            else if (waiterNode?.List is not null)
            {
                state.Waiters.Remove(waiterNode);
            }
        }

        if (grantedDuringCancellation)
        {
            await waiter.Completion.Task;
            UpdateActiveEntry(key, siteId, remoteModelName, GetActiveCount(state));
            return CreateTrackedAcquireResult(key, siteId, remoteModelName);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return ConcurrencyAcquireResult.NotAcquired;
    }

    /// <summary>
    /// 配置变更后同步新的最大并发数，并尽快唤醒可立即放行的等待请求。
    /// </summary>
    public void UpdateLimit(Guid siteId, string remoteModelName, int maxConcurrency)
    {
        var key = BuildKey(siteId, remoteModelName);
        var state = _states.GetOrAdd(key, _ => new ModelConcurrencyState());
        List<QueuedAcquireWaiter>? promotedWaiters;
        int activeCount;

        lock (state.SyncRoot)
        {
            state.MaxConcurrency = Math.Max(0, maxConcurrency);
            promotedWaiters = PromoteQueuedWaitersLocked(state);
            activeCount = state.ActiveCount;
        }

        ReleaseQueuedWaiters(promotedWaiters, key, siteId, remoteModelName, activeCount);
    }

    /// <summary>
    /// 返回当前活跃的真实模型并发快照，只包含并发数大于 0 的项。
    /// </summary>
    public IReadOnlyList<ActiveModelConcurrencyEntry> ListActive()
    {
        return _activeEntries.Values
            .Where(x => x.ActiveCount > 0)
            .OrderByDescending(x => x.ActiveCount)
            .ThenBy(x => x.SiteModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SiteId)
            .ToList();
    }

    /// <summary>
    /// 返回最近保留窗口内出现过的模型并发快照，归零后的项也会保留显示。
    /// </summary>
    public IReadOnlyList<ActiveModelConcurrencyEntry> ListRecent(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;

        foreach (var pair in _activeEntries)
        {
            if (pair.Value.ActiveCount <= 0 && pair.Value.LastSeenAt < cutoff)
            {
                _activeEntries.TryRemove(pair.Key, out _);
            }
        }

        return _activeEntries.Values
            .Where(x => x.ActiveCount > 0 || x.LastSeenAt >= cutoff)
            .OrderByDescending(x => x.ActiveCount)
            .ThenByDescending(x => x.LastSeenAt)
            .ThenBy(x => x.SiteModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SiteId)
            .ToList();
    }

    /// <summary>
    /// 创建带真实活跃计数的并发句柄，只在请求真正开始占用模型时才记数。
    /// </summary>
    private ConcurrencyAcquireResult CreateTrackedAcquireResult(string key, Guid siteId, string remoteModelName)
    {
        return ConcurrencyAcquireResult.AcquiredSlot(() => ReleaseActiveCount(key, siteId, remoteModelName));
    }

    /// <summary>
    /// 当前配置允许时，优先按先进先出放行已排队请求。
    /// </summary>
    private static List<QueuedAcquireWaiter> PromoteQueuedWaitersLocked(ModelConcurrencyState state)
    {
        List<QueuedAcquireWaiter>? promoted = null;
        while (CanAcquireImmediatelyLocked(state) && TryDequeueNextWaiterLocked(state, out var waiter))
        {
            state.ActiveCount++;
            waiter.Granted = true;
            promoted ??= [];
            promoted.Add(waiter);
        }

        return promoted ?? [];
    }

    /// <summary>
    /// 释放等待中的请求，使其沿用已预留的并发槽位继续执行。
    /// </summary>
    private void ReleaseQueuedWaiters(
        IReadOnlyList<QueuedAcquireWaiter> waiters,
        string key,
        Guid siteId,
        string remoteModelName,
        int activeCount)
    {
        if (waiters.Count == 0)
        {
            return;
        }

        UpdateActiveEntry(key, siteId, remoteModelName, activeCount);
        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetResult(true);
        }
    }

    /// <summary>
    /// 请求结束后回收当前活跃计数，归零时保留最近记录，便于调试页在 6 小时窗口内显示 0 并发。
    /// </summary>
    private void ReleaseActiveCount(string key, Guid siteId, string remoteModelName)
    {
        var state = _states.GetOrAdd(key, _ => new ModelConcurrencyState());
        List<QueuedAcquireWaiter>? promotedWaiters;
        int activeCount;

        lock (state.SyncRoot)
        {
            if (state.ActiveCount > 0)
            {
                state.ActiveCount--;
            }

            promotedWaiters = PromoteQueuedWaitersLocked(state);
            activeCount = state.ActiveCount;
        }

        if (promotedWaiters.Count > 0)
        {
            ReleaseQueuedWaiters(promotedWaiters, key, siteId, remoteModelName, activeCount);
            return;
        }

        UpdateActiveEntry(key, siteId, remoteModelName, activeCount);
    }

    /// <summary>
    /// 更新最近活跃快照，归零时保留最近一次出现时间。
    /// </summary>
    private void UpdateActiveEntry(string key, Guid siteId, string remoteModelName, int activeCount)
    {
        _activeEntries[key] = new ActiveModelConcurrencyEntry
        {
            SiteId = siteId,
            SiteModelName = remoteModelName,
            ActiveCount = activeCount,
            LastSeenAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// 读取当前活跃并发数，用于等待中的请求被唤醒后刷新调试快照。
    /// </summary>
    private static int GetActiveCount(ModelConcurrencyState state)
    {
        lock (state.SyncRoot)
        {
            return state.ActiveCount;
        }
    }

    /// <summary>
    /// 当前状态下是否还能立刻拿到并发槽位。
    /// </summary>
    private static bool CanAcquireImmediatelyLocked(ModelConcurrencyState state)
    {
        return state.MaxConcurrency <= 0 || state.ActiveCount < state.MaxConcurrency;
    }

    /// <summary>
    /// 从等待队列中取出下一个仍有效的请求，跳过已取消的节点。
    /// </summary>
    private static bool TryDequeueNextWaiterLocked(ModelConcurrencyState state, out QueuedAcquireWaiter waiter)
    {
        while (state.Waiters.First is not null)
        {
            var node = state.Waiters.First;
            state.Waiters.RemoveFirst();
            if (!node.Value.Completion.Task.IsCompleted)
            {
                waiter = node.Value;
                return true;
            }
        }

        waiter = null!;
        return false;
    }

    /// <summary>
    /// 拼接 SiteId + RemoteModelName 为缓存键。
    /// </summary>
    private static string BuildKey(Guid siteId, string remoteModelName)
    {
        return $"{siteId:N}:{remoteModelName}";
    }

    /// <summary>
    /// 单个站点模型的运行时并发状态。
    /// </summary>
    private sealed class ModelConcurrencyState
    {
        /// <summary>
        /// 状态锁。
        /// </summary>
        public object SyncRoot { get; } = new();
        /// <summary>
        /// 当前活跃并发数。
        /// </summary>
        public int ActiveCount { get; set; }
        /// <summary>
        /// 当前生效的最大并发数，0 表示不限制。
        /// </summary>
        public int MaxConcurrency { get; set; }
        /// <summary>
        /// 等待中的请求队列。
        /// </summary>
        public LinkedList<QueuedAcquireWaiter> Waiters { get; } = [];
    }

    /// <summary>
    /// 排队中的获取请求。
    /// </summary>
    private sealed class QueuedAcquireWaiter
    {
        /// <summary>
        /// 请求被放行时完成。
        /// </summary>
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// 是否已经为该请求预留并发槽位。
        /// </summary>
        public bool Granted { get; set; }
    }
}
