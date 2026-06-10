using AITool.Web.Services;

namespace AITool.Web.Services;

/// <summary>
/// 后台管理侧的运行时并发控制门面。
/// 把 Admin 控制器对 <c>ModelConcurrencyLimiter</c> 运行时写操作的直接依赖收口到独立门面中，
/// 使管理侧代码不再直接了解并发限制器的内部方法签名。
/// </summary>
public sealed class AdminConcurrencyControlService
{
    /// <summary>
    /// 运行时模型并发限制器。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;

    /// <summary>
    /// 初始化后台并发控制服务。
    /// </summary>
    public AdminConcurrencyControlService(ModelConcurrencyLimiter concurrencyLimiter)
    {
        _concurrencyLimiter = concurrencyLimiter;
    }

    /// <summary>
    /// 如果受影响的模型正在调用中，则在同一把锁内捕获槽位并登记延迟刷新，
    /// 避免请求刚结束时丢失通知。
    /// </summary>
    public bool TryDeferRuntimeRouteTargetsRefresh(
        string externalModelName,
        IReadOnlyCollection<RouteTargetIdentity> affectedRouteTargets,
        IReadOnlyList<CachedProxyRouteTarget> previousRoutes)
    {
        return _concurrencyLimiter.TryDeferRuntimeRouteTargetsRefresh(
            externalModelName,
            affectedRouteTargets,
            previousRoutes);
    }

    /// <summary>
    /// 配置变更后同步新的最大并发数，并尽快唤醒可立即放行的等待请求。
    /// </summary>
    public void UpdateLimit(Guid siteId, string remoteModelName, int maxConcurrency)
    {
        _concurrencyLimiter.UpdateLimit(siteId, remoteModelName, maxConcurrency);
    }
}
