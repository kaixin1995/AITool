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

    /// <summary>
    /// 最近一次套餐额度查询的原始响应 JSON（按站点所属供应商的原样报文），供「额度查询」
    /// 页面打开时免查询直接展示缓存值。仅额度供应商（当前为智谱 GLM）的站点会写入；
    /// 查询失败时保留上次成功值，配合 <see cref="LastQuotaStatus"/> 置灰展示。
    /// <para>
    /// 注意列类型必须显式 text：SqlSugar 对既有表的 ALTER 补列路径会把 Length&gt;8000 的
    /// 字符串列映射成 varchar(max)，SQLite 语法不认（CREATE 路径则无此问题）。
    /// </para>
    /// </summary>
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? LastQuotaRawJson { get; set; }

    /// <summary>
    /// 最近一次额度查询时间（含失败尝试），用于展示「上次查询 X 分钟前」。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastQuotaCheckedAt { get; set; }

    /// <summary>
    /// 最近一次额度查询结果状态：null=从未查询，ok=成功，invalid_credential=密钥失效（401/403），error=其他错误。
    /// </summary>
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? LastQuotaStatus { get; set; }

    /// <summary>
    /// 最近一次额度查询的错误信息；成功时清空。
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? LastQuotaError { get; set; }
}
