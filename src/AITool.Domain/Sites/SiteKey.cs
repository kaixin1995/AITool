using SqlSugar;

namespace AITool.Domain.Sites;

/// <summary>
/// 站点访问密钥，允许一个站点配置多个 Key，分别控制启用状态、优先级和备注。
/// <para>
/// 转发链路在缓存层把"路由 × 多个 Key"展开成多条候选路由，复用现有的优先级排序、
/// 故障熔断和并发占满跳下一个机制，实现"主备 Key + 各自独立并发计数"。
/// </para>
/// <para>
/// 兼容历史数据：<see cref="Site.ApiKey"/> 字段保留不删，老站点在首次启动时会被迁移为
/// 一条 Priority=0 的默认 SiteKey；Codex 托管站点不迁移，仍直接使用 Site.ApiKey。
/// </para>
/// </summary>
[SugarTable("SiteKeys")]
[SugarIndex("IX_SiteKeys_SiteId", nameof(SiteId), OrderByType.Asc)]
public sealed class SiteKey
{
    /// <summary>
    /// 密钥唯一标识，用于在路由展开、并发统计和凭证管理中引用该 Key。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 所属站点标识，指明该 Key 归属哪个站点。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 实际密钥值，调用上游时用于身份认证。
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = false)]
    public string KeyValue { get; set; } = string.Empty;

    /// <summary>
    /// 备注信息，用于区分同一站点的多个 Key（如"主号""备用号""测试号"）。可空。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Remark { get; set; }

    /// <summary>
    /// 优先级，数字越小越优先被选中。同站点的 Key 按此字段升序参与主备调度。
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 标记该 Key 当前是否启用，禁用后不参与路由展开和实际调用。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 密钥创建时间，用于记录该 Key 何时被加入系统。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
