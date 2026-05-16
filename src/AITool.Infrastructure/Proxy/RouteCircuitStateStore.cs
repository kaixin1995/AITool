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
    private TimeSpan _blockDuration;
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
        _blockDuration = blockDuration ?? TimeSpan.FromMinutes(2);
        _failThreshold = failThreshold;
    }

    /// <summary>
    /// 动态更新熔断参数，新配置应尽快影响后续请求。
    /// </summary>
    public void UpdateOptions(TimeSpan blockDuration, int failThreshold)
    {
        lock (_syncRoot)
        {
            _blockDuration = blockDuration;
            _failThreshold = failThreshold;
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
        var failThreshold = _failThreshold;
        var blockDuration = _blockDuration;

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
}
