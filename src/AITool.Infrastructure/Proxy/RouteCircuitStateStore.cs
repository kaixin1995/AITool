namespace AITool.Infrastructure.Proxy;

// 路由级熔断状态存储，记录因连续失败而被临时屏蔽的站点
public sealed class RouteCircuitStateStore
{
    private readonly Dictionary<Guid, DateTimeOffset> _blockedSites = [];
    private readonly TimeSpan _blockDuration;

    public RouteCircuitStateStore(TimeSpan? blockDuration = null)
    {
        _blockDuration = blockDuration ?? TimeSpan.FromMinutes(2);
    }

    // 将站点标记为熔断状态
    public void Block(Guid siteId)
    {
        _blockedSites[siteId] = DateTimeOffset.UtcNow.Add(_blockDuration);
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