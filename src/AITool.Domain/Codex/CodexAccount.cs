using SqlSugar;

namespace AITool.Domain.Codex;

/// <summary>
/// 表示一个通过 Codex（ChatGPT/OpenAI Codex CLI）OAuth 登录或导入凭证建立的账号。
/// <para>
/// 每个账号在创建时会自动关联一个隐藏的 <see cref="Sites.Site"/>（<see cref="LinkedSiteId"/>），
/// 该隐藏 Site 以 Responses 协议接入转发链路，从而复用现有的 Models / Routes / Chat 机制。
/// OAuth token（<see cref="AccessToken"/>）会同步写回隐藏 Site 的 ApiKey，由后台服务自动刷新。
/// </para>
/// </summary>
[SugarTable("CodexAccounts")]
[SugarIndex("IX_CodexAccounts_LinkedSiteId", nameof(LinkedSiteId), OrderByType.Asc, false)]
[SugarIndex("IX_CodexAccounts_TokenExpiresAt", nameof(TokenExpiresAt), OrderByType.Asc, false)]
public sealed class CodexAccount
{
    /// <summary>
    /// 账号唯一标识。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 用户自定义名称（同站点管理，便于区分多个 Codex 账号）。唯一性由应用层提示，不强制数据库约束。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 从 id_token JWT 解析的邮箱，用于展示与去重兜底。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Email { get; set; }

    /// <summary>
    /// chatgpt_account_id（来自 id_token JWT claim），作为账号去重的首选依据。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? AccountId { get; set; }

    /// <summary>
    /// 订阅计划类型：free / plus / team / pro，决定该账号可见的 Codex 模型分层。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = true)]
    public string? PlanType { get; set; }

    /// <summary>
    /// 当前 access_token，会同步写回 <see cref="LinkedSiteId"/> 指向的隐藏 Site 的 ApiKey。
    /// </summary>
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? AccessToken { get; set; }

    /// <summary>
    /// OAuth refresh_token，用于后台服务定期刷新 access_token。
    /// </summary>
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? RefreshToken { get; set; }

    /// <summary>
    /// JWT id_token，包含订阅窗口等附加信息，供面板展示使用。
    /// </summary>
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? IdToken { get; set; }

    /// <summary>
    /// access_token 过期时间（UTC，由 SqlSugar AOP 自动转本地时区存储）。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>
    /// 最近一次成功刷新 token 的时间。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastRefreshAt { get; set; }

    /// <summary>
    /// 指向自动创建的隐藏 Site 的标识（逻辑外键，非数据库约束）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public Guid LinkedSiteId { get; set; }

    /// <summary>
    /// 标记账号是否启用。禁用后会同步禁用隐藏 Site，转发链路自动绕开该账号。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 标记该账号是否因「Codex 功能总开关」被关闭而禁用（区别于额度耗尽/手动禁用）。
    /// 总开关重新开启时，仅恢复此标记为 true 的账号，避免误启用冷却中或手动禁用的账号。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool DisabledByFeatureToggle { get; set; }

    /// <summary>
    /// 剩余额度自动禁用阈值：当主动查询到的剩余额度低于此值时自动禁用账号。
    /// null 表示不启用自动禁用。单位由上游返回决定。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public decimal? AutoDisableThreshold { get; set; }

    /// <summary>
    /// 是否处于被动冷却（转发命中上游 usage_limit_reached 时标记为 true）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsQuotaCooling { get; set; }

    /// <summary>
    /// 冷却恢复时间（UTC）。到期后由后台恢复服务自动清除冷却并重新启用账号（若未被手动禁用）。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? QuotaCoolingUntil { get; set; }

    /// <summary>
    /// 最近一次主动额度查询的原始响应 JSON，供面板展示与解析使用。
    /// </summary>
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? LastQuotaRawJson { get; set; }

    /// <summary>
    /// 最近一次主动额度查询的时间。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastQuotaCheckedAt { get; set; }

    /// <summary>
    /// 账号创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
