namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 熔断触发事件的回调参数。
/// <para>
/// 当某条路由因连续失败达到阈值被首次熔断时，<see cref="RouteCircuitStateStore"/>
/// 通过 <see cref="RouteCircuitStateStore.OnCircuitOpened"/> 事件向外通知。
/// </para>
/// </summary>
public sealed class CircuitOpenedEventArgs : EventArgs
{
    /// <summary>
    /// 被熔断的路由标识。
    /// </summary>
    public required Guid RouteId { get; init; }

    /// <summary>
    /// 触发熔断时的连续失败次数。
    /// </summary>
    public required int FailureCount { get; init; }

    /// <summary>
    /// 熔断阈值（触发熔断所需的最小连续失败次数）。
    /// </summary>
    public required int FailThreshold { get; init; }

    /// <summary>
    /// 熔断持续时长（路由被屏蔽的时间窗口）。
    /// </summary>
    public required TimeSpan BlockDuration { get; init; }

    /// <summary>
    /// 熔断预计解除时间（UTC）。
    /// </summary>
    public required DateTimeOffset RecoveryTime { get; init; }
}

/// <summary>
/// 路由级熔断状态存储，单条路由连续失败达到阈值后被临时屏蔽。
/// <para>
/// 当熔断首次触发时，通过 <see cref="OnCircuitOpened"/> 事件通知订阅者，
/// 外部可据此发布跨宿主事件到 Admin 侧，用于实时监控路由健康状态。
/// </para>
/// </summary>
public sealed class RouteCircuitStateStore
{
    /// <summary>
    /// 保护熔断参数读写的同步锁。
    /// </summary>
    private readonly object _syncRoot = new();

    /// <summary>
    /// 触发熔断后路由被屏蔽的持续时间（以 Ticks 存储，便于 Volatile.Read/Write 跨线程可见）。
    /// </summary>
    private long _blockDurationTicks;

    /// <summary>
    /// 连续失败达到该次数时触发熔断。
    /// </summary>
    private int _failThreshold;

    /// <summary>
    /// 路由连续失败次数记录。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _failCounts = [];

    /// <summary>
    /// 被熔断的路由及其解除时间。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _blockedRoutes = [];

    /// <summary>
    /// 当某条路由因连续失败达到阈值被首次熔断时触发。
    /// 订阅者可据此发布跨宿主事件，事件参数包含路由标识、失败计数、阈值和恢复时间。
    /// </summary>
    public event EventHandler<CircuitOpenedEventArgs>? OnCircuitOpened;

    /// <summary>
    /// 注入熔断屏蔽时长和连续失败阈值。
    /// </summary>
    public RouteCircuitStateStore(TimeSpan? blockDuration = null, int failThreshold = 5)
    {
        _blockDurationTicks = (blockDuration ?? TimeSpan.FromMinutes(2)).Ticks;
        _failThreshold = failThreshold;
    }

    /// <summary>
    /// 动态更新熔断参数，新配置应尽快影响后续请求。
    /// </summary>
    public void UpdateOptions(TimeSpan blockDuration, int failThreshold)
    {
        lock (_syncRoot)
        {
            // 使用 Volatile.Write 保证写入对其它线程立即可见（Block 在锁外读取这两个字段）。
            Volatile.Write(ref _blockDurationTicks, blockDuration.Ticks);
            Volatile.Write(ref _failThreshold, failThreshold);
        }
    }

    /// <summary>
    /// 记录一次失败，连续失败达到阈值时触发熔断。
    /// <para>
    /// 首次触发熔断时会引发 <see cref="OnCircuitOpened"/> 事件。
    /// 如果路由已被熔断，则不再重复计数。
    /// </para>
    /// </summary>
    public void Block(Guid routeId)
    {
        // 如果已经被熔断，不再重复计数
        if (IsBlocked(routeId)) return;

        var count = _failCounts.AddOrUpdate(routeId, 1, (_, current) => current + 1);
        // 使用 Volatile.Read 保证读到 UpdateOptions 最新写入的值（Block 在锁外读取）。
        var failThreshold = Volatile.Read(ref _failThreshold);
        var blockDuration = TimeSpan.FromTicks(Volatile.Read(ref _blockDurationTicks));

        if (count >= failThreshold)
        {
            var recoveryTime = DateTimeOffset.UtcNow.Add(blockDuration);
            _blockedRoutes[routeId] = recoveryTime;

            // 首次触发熔断时通知订阅者
            OnCircuitOpened?.Invoke(this, new CircuitOpenedEventArgs
            {
                RouteId = routeId,
                FailureCount = count,
                FailThreshold = failThreshold,
                BlockDuration = blockDuration,
                RecoveryTime = recoveryTime
            });
        }
    }

    /// <summary>
    /// 记录一次成功，清除该路由的连续失败计数。
    /// </summary>
    public void Succeed(Guid routeId)
    {
        _failCounts.TryRemove(routeId, out _);
    }

    /// <summary>
    /// 判断路由当前是否仍处于熔断窗口内。
    /// </summary>
    public bool IsBlocked(Guid routeId)
    {
        if (_blockedRoutes.TryGetValue(routeId, out var until))
        {
            if (until > DateTimeOffset.UtcNow) return true;
            _blockedRoutes.TryRemove(routeId, out _);
            _failCounts.TryRemove(routeId, out _);
        }
        return false;
    }

    /// <summary>
    /// 返回所有当前被熔断的路由和正在累计失败但尚未熔断的路由。
    /// 供熔断监控页展示全局状态。
    /// </summary>
    public IReadOnlyDictionary<Guid, CircuitRouteInfo> GetAllCircuitStates()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new Dictionary<Guid, CircuitRouteInfo>();
        // 先清理已过期的熔断
        foreach (var pair in _blockedRoutes)
        {
            if (pair.Value <= now)
            {
                _blockedRoutes.TryRemove(pair.Key, out _);
                _failCounts.TryRemove(pair.Key, out _);
            }
        }
        // 收集当前被熔断的路由
        foreach (var pair in _blockedRoutes)
        {
            var failCount = _failCounts.GetValueOrDefault(pair.Key, 0);
            result[pair.Key] = new CircuitRouteInfo(IsBlocked: true, FailureCount: failCount,
                BlockedUntil: pair.Value, RemainingTime: pair.Value > now ? pair.Value - now : TimeSpan.Zero);
        }
        // 收集正在累计失败但尚未熔断的路由
        var failThreshold = Volatile.Read(ref _failThreshold);
        foreach (var pair in _failCounts)
        {
            if (result.ContainsKey(pair.Key)) continue;
            result[pair.Key] = new CircuitRouteInfo(IsBlocked: false, FailureCount: pair.Value,
                BlockedUntil: null, RemainingTime: null);
        }
        return result;
    }

    /// <summary>
    /// 手动解除指定路由的熔断状态（同时清除失败计数）。
    /// </summary>
    public bool Reset(Guid routeId)
    {
        var removed = false;
        removed |= _blockedRoutes.TryRemove(routeId, out _);
        removed |= _failCounts.TryRemove(routeId, out _);
        return removed;
    }

    /// <summary>
    /// 解除所有路由的熔断状态。
    /// </summary>
    public int ResetAll()
    {
        var count = _blockedRoutes.Count;
        _blockedRoutes.Clear();
        _failCounts.Clear();
        return count;
    }
}

/// <summary>
/// 单条路由的熔断状态摘要，供监控页展示。
/// </summary>
public sealed record CircuitRouteInfo(
    bool IsBlocked,
    int FailureCount,
    DateTimeOffset? BlockedUntil,
    TimeSpan? RemainingTime);
