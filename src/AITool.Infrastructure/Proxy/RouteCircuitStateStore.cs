namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 路由级熔断状态存储，单条路由连续失败达到阈值后被临时屏蔽
/// </summary>
public sealed class RouteCircuitStateStore
{
    /// <summary>
    /// 保护熔断参数读写的同步锁
    /// </summary>
    private readonly object _syncRoot = new();
    /// <summary>
    /// 触发熔断后路由被屏蔽的持续时间
    /// </summary>
    private long _blockDurationTicks;
    /// <summary>
    /// 连续失败达到该次数时触发熔断
    /// </summary>
    private int _failThreshold;
    /// <summary>
    /// 路由连续失败次数记录
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _failCounts = [];
    /// <summary>
    /// 被熔断的路由及其解除时间
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _blockedRoutes = [];

    /// <summary>
    /// 注入熔断屏蔽时长和连续失败阈值
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
            Volatile.Write(ref _blockDurationTicks, blockDuration.Ticks);
            Volatile.Write(ref _failThreshold, failThreshold);
        }
    }

    /// <summary>
    /// 记录一次失败，连续失败达到阈值时触发熔断
    /// </summary>
    public void Block(Guid routeId)
    {
        // 如果已经被熔断，不再重复计数
        if (IsBlocked(routeId)) return;

        var count = _failCounts.AddOrUpdate(routeId, 1, (_, current) => current + 1);
        var failThreshold = Volatile.Read(ref _failThreshold);
        var blockDuration = TimeSpan.FromTicks(Volatile.Read(ref _blockDurationTicks));

        if (count >= failThreshold)
        {
            _blockedRoutes[routeId] = DateTimeOffset.UtcNow.Add(blockDuration);
        }
    }

    /// <summary>
    /// 记录一次成功，清除该路由的连续失败计数
    /// </summary>
    public void Succeed(Guid routeId)
    {
        _failCounts.TryRemove(routeId, out _);
    }

    /// <summary>
    /// 判断路由当前是否仍处于熔断窗口内
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
    /// 返回所有当前被熔断的路由（routeId → 解除时间）和正在累计失败但尚未熔断的路由。
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
            result[pair.Key] = new CircuitRouteInfo(
                IsBlocked: true,
                FailureCount: failCount,
                BlockedUntil: pair.Value,
                RemainingTime: pair.Value > now ? pair.Value - now : TimeSpan.Zero);
        }

        // 收集正在累计失败但尚未熔断的路由
        var failThreshold = Volatile.Read(ref _failThreshold);
        foreach (var pair in _failCounts)
        {
            if (result.ContainsKey(pair.Key)) continue;
            result[pair.Key] = new CircuitRouteInfo(
                IsBlocked: false,
                FailureCount: pair.Value,
                BlockedUntil: null,
                RemainingTime: null);
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
/// 单条路由的熔断状态信息。
/// </summary>
public sealed record CircuitRouteInfo(
    bool IsBlocked,
    int FailureCount,
    DateTimeOffset? BlockedUntil,
    TimeSpan? RemainingTime);
