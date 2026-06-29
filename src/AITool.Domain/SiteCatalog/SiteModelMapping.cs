using SqlSugar;

namespace AITool.Domain.SiteCatalog;

/// <summary>
/// 表示站点与其支持的模型之间的映射关系，用于记录某站点以何种名称对外提供某个模型。
/// </summary>
[SugarTable("SiteModelMappings")]
[SugarIndex("UX_SiteModelMappings_SiteId_RemoteModelName", nameof(SiteId), OrderByType.Asc, nameof(RemoteModelName), OrderByType.Asc, true)]
public sealed class SiteModelMapping
{
    /// <summary>
    /// 映射唯一标识，用于在配置管理和健康监控中引用该映射关系。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 所属站点标识，用于指明这条映射关系关联到哪个站点。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 关联的模型库项标识，用于把站点侧的模型名称与统一模型库对应起来。
    /// </summary>
    public Guid ModelLibraryItemId { get; set; }

    /// <summary>
    /// 远程模型名称，用于记录该站点上模型在调用时使用的实际名称。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string RemoteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 最近一次健康检查的状态，用于反映该映射对应模型当前的可用性结果。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string LastStatus { get; set; } = string.Empty;

    /// <summary>
    /// 最大并发数，用于约束该映射对应模型在同一时刻允许的调用上限。
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// 标记该映射当前是否启用，禁用后不再参与模型发现和路由匹配。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 最近一次健康检查的时间，用于记录该映射对应模型的上次检测时刻。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastCheckedAt { get; set; }
}
