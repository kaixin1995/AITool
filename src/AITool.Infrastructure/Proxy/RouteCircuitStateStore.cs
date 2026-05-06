namespace AITool.Infrastructure.Proxy;

// 路由级熔断状态存储，单条路由连续失败达到阈值后被临时屏蔽
public sealed class RouteCircuitStateStore
{
    private readonly TimeSpan _blockDuration;
    private readonly int _failThreshold;
    // 路由连续失败次数记录
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _failCounts = [];
    // 被熔断的路由及其解除时间
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _blockedRoutes = [];

    public RouteCircuitStateStore(TimeSpan? blockDuration = null, int failThreshold = 5)
    {
        _blockDuration = blockDuration ?? TimeSpan.FromMinutes(2);
        _failThreshold = failThreshold;
    }

    // 记录一次失败，连续失败达到阈值时触发熔断
    public void Block(Guid routeId)
    {
        // 如果已经被熔断，不再重复计数
        if (IsBlocked(routeId)) return;

        var count = _failCounts.AddOrUpdate(routeId, 1, (_, current) => current + 1);

        if (count >= _failThreshold)
        {
            _blockedRoutes[routeId] = DateTimeOffset.UtcNow.Add(_blockDuration);
        }
    }

    // 记录一次成功，清除该路由的连续失败计数
    public void Succeed(Guid routeId)
    {
        _failCounts.TryRemove(routeId, out _);
    }

    // 判断路由当前是否仍处于熔断窗口内
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
