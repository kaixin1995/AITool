using SqlSugar;

namespace AITool.Domain.Google;

/// <summary>
/// 表示一个通过 Google OAuth 登录或导入凭证建立的账号（Antigravity 接入方式，
/// 行为对齐 gcli2api 项目的凭证模式；历史 GeminiCLI 接入方式已下线）。
/// <para>
/// 与 Codex 账号一致采用「隐藏 Site 复用」方案：每个账号自动创建一个 ProtocolType=Gemini 的隐藏 Site，
/// OAuth access_token（<see cref="AccessToken"/>）同步写回隐藏 Site 的 ApiKey，由后台服务自动刷新，
/// 转发链路经 SiteId 自动联动 Models / Routes / Chat，无需改动这三处业务代码。
/// </para>
/// </summary>
[SugarTable("GoogleAccounts")]
[SugarIndex("IX_GoogleAccounts_LinkedSiteId", nameof(LinkedSiteId), OrderByType.Asc, false)]
[SugarIndex("IX_GoogleAccounts_TokenExpiresAt", nameof(TokenExpiresAt), OrderByType.Asc, false)]
public sealed class GoogleAccount
{
    /// <summary>
    /// 浅拷贝当前账号。元数据缓存返回副本，避免调用方原地修改共享实体污染缓存。
    /// </summary>
    public GoogleAccount Clone() => (GoogleAccount)MemberwiseClone();

    /// <summary>
    /// 账号唯一标识。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 用户自定义名称（同站点管理，便于区分多个 Google 账号）。唯一性由应用层提示，不强制数据库约束。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 授权 Google 账号邮箱（userinfo 接口获取），用于展示与去重。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Email { get; set; }

    /// <summary>
    /// 接入方式：Antigravity（daily-cloudcode-pa.googleapis.com，Antigravity CLI 客户端身份）。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string AccountKind { get; set; } = "Antigravity";

    /// <summary>
    /// Google Cloud 项目 ID：Antigravity 模式为 loadCodeAssist 返回的 cloudaicompanionProject。
    /// 上游请求体会以该值作为 project 字段。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? ProjectId { get; set; }

    /// <summary>
    /// 订阅等级（仅 Antigravity 登录时由 loadCodeAssist 返回）：free / pro / ultra。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = true)]
    public string? SubscriptionTier { get; set; }

    /// <summary>
    /// 剩余积分数量（仅 Antigravity 登录时由 loadCodeAssist 的 availableCredits 返回，可能为空）。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public int? CreditAmount { get; set; }

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
    /// access_token 过期时间（UTC，由 SqlSugar AOP 自动转本地时区存储）。Google access_token 通常 1 小时有效。
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
    /// 标记该账号是否因「OAuth 功能总开关」被关闭而禁用（区别于额度耗尽/手动禁用）。
    /// 总开关重新开启时，仅恢复此标记为 true 的账号，避免误启用手动禁用的账号。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool DisabledByFeatureToggle { get; set; }

    /// <summary>
    /// 标记该账号是否被用户手动禁用（区别于额度阈值自动禁用/总开关禁用）。
    /// 巡检自动恢复时跳过此标记为 true 的账号；用户再次手动启用时清除该标记。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool ManuallyDisabled { get; set; }

    /// <summary>
    /// 标记账号是否因 Google 上游返回 403 权限/策略错误而被自动禁用。
    /// 该状态需要人工重新启用，避免额度巡检在短时间内自动恢复受限凭证。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool DisabledByUpstream { get; set; }

    /// <summary>
    /// 是否处于被动冷却（转发命中上游 429/RESOURCE_EXHAUSTED 时标记为 true）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsQuotaCooling { get; set; }

    /// <summary>
    /// 冷却恢复时间（UTC）。到期后由后台恢复服务自动清除冷却并重新启用账号（若未被手动禁用）。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? QuotaCoolingUntil { get; set; }

    /// <summary>
    /// 最近一次主动额度查询的原始响应 JSON（Antigravity 的 fetchAvailableModels），供面板展示与解析使用。
    /// </summary>
    [SugarColumn(Length = 20000, IsNullable = true)]
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
