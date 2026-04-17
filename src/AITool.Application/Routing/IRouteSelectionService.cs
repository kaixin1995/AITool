using AITool.Domain.Proxy;

namespace AITool.Application.Routing;

// 路由选择结果，包含匹配到的路由规则和目标站点信息
public sealed class RouteSelectionResult
{
    // 匹配到的路由规则
    public ProxyRouteRule? Route { get; set; }

    // 是否找到有效路由
    public bool Found => Route is not null;
}

// 路由选择服务接口，根据优先级为给定模型名称选择最佳启用的路由规则
public interface IRouteSelectionService
{
    // 根据外部模型名称选择优先级最高的启用路由
    Task<RouteSelectionResult> SelectRouteAsync(
        string externalModelName,
        CancellationToken cancellationToken = default);
}
