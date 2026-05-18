namespace AITool.Domain.SiteCatalog;

/// <summary>
/// 表示站点与模型库之间的一条映射关系，用于描述某个站点实际支持的模型以及其站点侧命名。
/// </summary>
public sealed class SiteModelMapping
{
    /// <summary>
    /// 映射记录唯一标识，用于区分不同站点下的模型配置项。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 关联的站点标识，用于确定该映射属于哪个外部服务站点。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 关联的模型库项标识，用于将站点模型归并到系统统一模型定义下。
    /// </summary>
    public Guid ModelLibraryItemId { get; set; }

    /// <summary>
    /// 站点原始模型名称，用于处理站点实际返回或要求传入的模型名与统一模型名不一致的情况。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 最近一次拉取或同步状态，用于反映当前映射项的检测结果或同步结果。
    /// </summary>
    public string LastStatus { get; set; } = "unknown";

    /// <summary>
    /// 标记该站点下的模型映射是否启用，便于按站点维度单独控制模型可用性。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 该站点上此模型的最大并发数，0 表示不限制。
    /// 当多个路由入口指向同一站点的同一模型时，并发总数不会超过此值。
    /// </summary>
    public int MaxConcurrency { get; set; }
}
