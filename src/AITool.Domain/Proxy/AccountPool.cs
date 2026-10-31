using SqlSugar;

namespace AITool.Domain.Proxy;

/// <summary>
/// 账号号池：把多个同厂商 OAuth 账号打包成一个对外路由入口（生成式）。
/// <para>
/// 池本身不参与转发——由 <c>AccountPoolSyncService</c> 把成员展开成普通的
/// <see cref="ProxyRouteRule"/>（ExternalModelName = 池名），托管标记挂在
/// <see cref="ProxyRouteEntry.ManagedByPoolId"/> 上。转发热路径、优先级/熔断/
/// 分级冷却/会话亲和/配额排序等既有机制全部原样复用，删除池仅解除托管（规则保留）。
/// </para>
/// <para>
/// 成员模型：对外暴露的模型 = 全体成员已启用映射的并集（每个模型只路由到持有它的成员），
/// 对外名透出上游名。同厂商同套餐的模型清单一致（用户实证）。</para>
/// </summary>
[SugarTable("AccountPools")]
[SugarIndex("UX_AccountPools_Name", nameof(Name), OrderByType.Asc, true)]
[SugarIndex("IX_AccountPools_Vendor", nameof(VendorKey), OrderByType.Asc, false)]
public sealed class AccountPool
{
    /// <summary>池唯一标识。</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 池名 = 对外路由条目名（ExternalModelName 命名空间）。创建后不可改：
    /// 改名需连带迁移既有规则与调用方，V1 不支持。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 成员账号厂商：codex / kimi / xai / antigravity，或设备 OAuth 厂商键
    /// （zcode / qoder / minimax-cn 等）。池只收同厂商账号（模型清单与协议一致的前提）。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = false)]
    public string VendorKey { get; set; } = string.Empty;

    /// <summary>套餐过滤（当前仅 codex 有意义：team/plus/…）。null = 该厂商全部账号。</summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? PlanType { get; set; }

    /// <summary>
    /// 自动纳入：同步时把厂商（+套餐）匹配的全部账号补进成员行；false = 仅手工成员。
    /// 排除靠成员行的 <see cref="AccountPoolMember.Excluded"/>（自动池排除后不会再被补回）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool AutoIncludeMembers { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最近一次同步/修改时间（UTC）。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 号池成员行。AccountRefId 是厂商账号表的行 Id（跨表逻辑引用，不做数据库外键）；
/// LinkedSiteId 冗余存成员的隐藏站点（规则生成直接使用，同步时随账号刷新）。
/// </summary>
[SugarTable("AccountPoolMembers")]
[SugarIndex("UX_AccountPoolMembers_Pool_Account", nameof(PoolId), OrderByType.Asc, nameof(AccountRefId), OrderByType.Asc, true)]
[SugarIndex("IX_AccountPoolMembers_Pool_Priority", nameof(PoolId), OrderByType.Asc, nameof(Priority), OrderByType.Asc)]
public sealed class AccountPoolMember
{
    /// <summary>成员行唯一标识。</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>所属池。</summary>
    [SugarColumn(IsNullable = false)]
    public Guid PoolId { get; set; }

    /// <summary>厂商账号表行 Id（CodexAccounts / KimiAccounts / XaiAccounts / GoogleAccounts / DeviceOAuthAccounts）。</summary>
    [SugarColumn(IsNullable = false)]
    public Guid AccountRefId { get; set; }

    /// <summary>成员的隐藏站点 Id（生成 ProxyRouteRule.SiteId）。</summary>
    [SugarColumn(IsNullable = false)]
    public Guid LinkedSiteId { get; set; }

    /// <summary>池内静态顺序（生成规则的 InstancePriority 基线，越小越优先；运行时叠加配额排序等策略）。</summary>
    [SugarColumn(IsNullable = false)]
    public int Priority { get; set; }

    /// <summary>排除位：自动池排除后不再补回；手动池等价移除但保留行以便恢复。</summary>
    [SugarColumn(IsNullable = false)]
    public bool Excluded { get; set; }

    /// <summary>加入时间（UTC）。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
