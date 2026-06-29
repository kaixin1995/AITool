using SqlSugar;

namespace AITool.Domain.Proxy;

/// <summary>
/// 表示一条代理路由规则，用于将对外暴露的模型请求映射到具体站点及其实际模型名称。
/// </summary>
[SugarTable("ProxyRouteRules")]
[SugarIndex("IX_ProxyRouteRules_ExternalModelName_Priority", nameof(ExternalModelName), OrderByType.Asc, nameof(Priority), OrderByType.Asc)]
[SugarIndex("IX_ProxyRouteRules_ExternalModelName_IsEnabled_Priorities", nameof(ExternalModelName), OrderByType.Asc, nameof(IsEnabled), OrderByType.Asc, nameof(ModelPriority), OrderByType.Asc, nameof(InstancePriority), OrderByType.Asc, nameof(Priority), OrderByType.Asc)]
public sealed class ProxyRouteRule
{
    /// <summary>
    /// 路由规则唯一标识，用于在配置管理、排序和引用时定位该规则。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 对外暴露的模型名称，客户端请求代理时通常使用该名称进行访问。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string ExternalModelName { get; set; } = string.Empty;

    /// <summary>
    /// 当前规则所属的上游模型名称，用于在同一外部模型下进一步划分上游模型组。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string UpstreamModelName { get; set; } = string.Empty;

    /// <summary>
    /// 目标站点标识，用于确定该规则最终会将请求转发到哪个站点。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 目标站点上的实际模型名称，用于处理外部名称与站点内部模型名称不一致的情况。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string SiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 全局优先级，数值越小越优先，用于决定同类候选规则的整体排序。
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 上游模型组优先级，数值越小越优先，用于控制不同上游模型组之间的先后顺序。
    /// </summary>
    public int ModelPriority { get; set; }

    /// <summary>
    /// 同组内实例优先级，数值越小越优先，用于控制同一模型组内不同站点实例的选择顺序。
    /// </summary>
    public int InstancePriority { get; set; }

    /// <summary>
    /// 标记该路由规则是否启用，禁用后不应参与请求匹配和转发。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 时间可用性模式，空值或 AllDay 均表示全天可用。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string AvailabilityMode { get; set; } = "AllDay";

    /// <summary>
    /// 保存每日时间范围 JSON；为空时按全天可用兼容旧规则。
    /// </summary>
    [SugarColumn(Length = 2000, IsNullable = false)]
    public string TimeRangesJson { get; set; } = string.Empty;
}
