namespace AITool.Infrastructure.Proxy;

// 路由级熔断状态存储，站点连续失败达到阈值后被临时屏蔽
public sealed class RouteCircuitStateStore
{
    private readonly TimeSpan _blockDuration;
    private readonly int _failThreshold;
    // 站点连续失败次数记录
    private readonly Dictionary<Guid, int> _failCounts = [];
    // 被熔断的站点及其解除时间
    private readonly Dictionary<Guid, DateTimeOffset> _blockedSites = [];

    public RouteCircuitStateStore(TimeSpan? blockDuration = null, int failThreshold = 5)
    {
        _blockDuration = blockDuration ?? TimeSpan.FromMinutes(2);
        _failThreshold = failThreshold;
    }

    // 记录一次失败，连续失败达到阈值时触发熔断
    public void Block(Guid siteId)
    {
        // 如果已经被熔断，不再重复计数
        if (IsBlocked(siteId)) return;

        var count = _failCounts.GetValueOrDefault(siteId) + 1;
        _failCounts[siteId] = count;

        if (count >= _failThreshold)
        {
            _blockedSites[siteId] = DateTimeOffset.UtcNow.Add(_blockDuration);
        }
    }

    // 记录一次成功，清除该站点的连续失败计数
    public void Succeed(Guid siteId)
    {
        _failCounts.Remove(siteId);
    }

    // 判断站点当前是否仍处于熔断窗口内
    public bool IsBlocked(Guid siteId)
    {
        if (_blockedSites.TryGetValue(siteId, out var until))
        {
            if (until > DateTimeOffset.UtcNow) return true;
            _blockedSites.Remove(siteId);
        }
        return false;
    }
}
