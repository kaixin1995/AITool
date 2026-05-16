using AITool.Domain.Proxy;

namespace AITool.Application.Routing;

/// <summary>
/// 路由选择结果，用于封装一次匹配后得到的路由信息。
/// </summary>
public sealed class RouteSelectionResult
{
    /// <summary>
    /// 保存最终匹配到的路由规则；未命中时保持为空。
    /// </summary>
    public ProxyRouteRule? Route { get; set; }

    /// <summary>
    /// 通过是否存在路由规则，快速判断本次匹配是否成功。
    /// </summary>
    public bool Found => Route is not null;
}

/// <summary>
/// 路由选择服务接口，负责根据外部模型名挑选可用且优先级合适的路由规则。
/// </summary>
public interface IRouteSelectionService
{
    /// <summary>
    /// 根据外部模型名称选择优先级最高的可用路由。
    /// </summary>
    Task<RouteSelectionResult> SelectRouteAsync(
        string externalModelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在指定排除站点集合的前提下选择路由，通常用于失败后的重试或熔断避让。
    /// </summary>
    Task<RouteSelectionResult> SelectRouteAsync(
        string externalModelName,
        HashSet<Guid> excludedSiteIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定模型名称对应的全部启用路由，并按优先级顺序返回，供上层依次尝试。
    /// </summary>
    Task<List<RouteSelectionResult>> SelectAllRoutesAsync(
        string externalModelName,
        CancellationToken cancellationToken = default);
}
