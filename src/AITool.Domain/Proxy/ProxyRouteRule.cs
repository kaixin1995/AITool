namespace AITool.Domain.Proxy;

// 代理路由规则，将外部模型名称映射到目标站点和站点模型名
public sealed class ProxyRouteRule
{
    // 路由主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 对外暴露的模型名称，客户端请求时使用
    public string ExternalModelName { get; set; } = string.Empty;

    // 当前路由所属的上游模型名称
    public string UpstreamModelName { get; set; } = string.Empty;

    // 目标站点标识
    public Guid SiteId { get; set; }

    // 目标站点上的模型名称
    public string SiteModelName { get; set; } = string.Empty;

    // 全局优先级，数值越小优先级越高
    public int Priority { get; set; }

    // 上游模型组优先级，数值越小优先级越高
    public int ModelPriority { get; set; }

    // 同组内实例优先级，数值越小优先级越高
    public int InstancePriority { get; set; }

    // 是否启用该路由
    public bool IsEnabled { get; set; } = true;
}
