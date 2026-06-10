namespace AITool.Core.Services;

/// <summary>
/// 路由规则配置页中展示的模型项，包含该模型的站点数量和路由配置状态。
/// 这类类型由 ProxyRequestMetadataCache 的后台查询职责复用，先从控制器和页面文件中抽离，降低服务层对 Web 端点类型的耦合。
/// </summary>
public sealed class RouteModelItem
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 站点数量。
    /// </summary>
    public int SiteCount { get; set; }

    /// <summary>
    /// 是否已配置路由规则。
    /// </summary>
    public bool HasRouteRules { get; set; }
}

/// <summary>
/// 路由主入口列表项，展示入口名称和其下的候选实例数量。
/// </summary>
public sealed class RouteEntryListItem
{
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;

    /// <summary>
    /// 候选实例数量。
    /// </summary>
    public int CandidateCount { get; set; }
}

/// <summary>
/// 可选站点实例项，用于路由规则配置页中展示可绑定的站点映射。
/// </summary>
public sealed class SiteInstanceItem
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 站点是否启用，用于在管理页面区分已禁用站点。
    /// </summary>
    public bool SiteEnabled { get; set; } = true;
}

/// <summary>
/// 按模型名称发现的可用站点项，展示站点信息和远端模型名称。
/// </summary>
public sealed class DiscoveredSiteItem
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// 远端模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 站点是否启用。
    /// </summary>
    public bool SiteEnabled { get; set; }
}

/// <summary>
/// 路由规则列表项，展示单条规则的详细信息，包括站点、模型、优先级和启用状态。
/// </summary>
public sealed class RouteRuleListItem
{
    /// <summary>
    /// 规则标识。
    /// </summary>
    public Guid RuleId { get; set; }

    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// 上游模型名称。
    /// </summary>
    public string UpstreamModelName { get; set; } = string.Empty;

    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 全局优先级。
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 模型优先级。
    /// </summary>
    public int ModelPriority { get; set; }

    /// <summary>
    /// 实例优先级。
    /// </summary>
    public int InstancePriority { get; set; }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 站点是否启用，用于在管理页面区分已禁用站点。
    /// </summary>
    public bool SiteEnabled { get; set; } = true;

    /// <summary>
    /// 时间可用性模式，旧规则为空时按全天可用处理。
    /// </summary>
    public string AvailabilityMode { get; set; } = "AllDay";

    /// <summary>
    /// 每日时间范围 JSON，空值表示不限制。
    /// </summary>
    public string TimeRangesJson { get; set; } = string.Empty;
}

/// <summary>
/// 客户端模拟器中的模型展示项。
/// </summary>
public sealed class ClientSimulatorModelItemViewModel
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 当前模型可命中的路由数量。
    /// </summary>
    public int RouteCount { get; set; }

    /// <summary>
    /// 模型是否支持 OpenAI 协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; }

    /// <summary>
    /// 模型是否支持 Anthropic 协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 当前环境下是否允许通过 OpenAI 协议调用。
    /// </summary>
    public bool CanUseOpenAi { get; set; }

    /// <summary>
    /// 当前环境下是否允许通过 Anthropic 协议调用。
    /// </summary>
    public bool CanUseAnthropic { get; set; }
}
