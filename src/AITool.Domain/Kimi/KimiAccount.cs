using SqlSugar;

namespace AITool.Domain.Kimi;

/// <summary>
/// 表示一个通过 Kimi (Moonshot AI) OAuth 设备码登录或凭证导入建立的账号。
/// <para>
/// 采用与 Codex / Google（Antigravity）一致的「隐藏 Site 复用」方案：每个账号自动创建一个
/// ProtocolType=OpenAI、ManagedSource="kimi_oauth" 的隐藏 Site，OAuth access_token 同步写回隐藏 Site 的 ApiKey，
/// 由后台服务自动刷新，转发链路经 SiteId 自动联动 Models / Routes / Chat / 诊断，无需改动转发核心业务代码。
/// </para>
/// </summary>
[SugarTable("KimiAccounts")]
[SugarIndex("IX_KimiAccounts_LinkedSiteId", nameof(LinkedSiteId), OrderByType.Asc, false)]
[SugarIndex("IX_KimiAccounts_TokenExpiresAt", nameof(TokenExpiresAt), OrderByType.Asc, false)]
public sealed class KimiAccount
{
    /// <summary>
    /// 浅拷贝当前账号。元数据缓存返回副本，避免调用方原地修改共享实体污染缓存。
    /// </summary>
    public KimiAccount Clone() => (KimiAccount)MemberwiseClone();

    /// <summary>
    /// 账号唯一标识。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 用户自定义名称（便于区分多个 Kimi 账号）。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 授权 Kimi 账号邮箱或用户名（若有）。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Email { get; set; }

    /// <summary>
    /// Kimi 用户标识（若有）。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? UserId { get; set; }

    /// <summary>
    /// 设备指纹 ID（UUID），用于请求头 X-Msh-Device-Id。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? DeviceId { get; set; }

    /// <summary>
    /// 当前 access_token，会同步写回 LinkedSiteId 指向的隐藏 Site 的 ApiKey。
    /// </summary>
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? AccessToken { get; set; }

    /// <summary>
    /// OAuth refresh_token，用于后台服务定期刷新 access_token。
    /// </summary>
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? RefreshToken { get; set; }

    /// <summary>
    /// 令牌类型，默认 bearer。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? TokenType { get; set; } = "bearer";

    /// <summary>
    /// 授权范围。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Scope { get; set; }

    /// <summary>
    /// access_token 过期时间（UTC）。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>
    /// 最近一次成功刷新 token 的时间。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastRefreshAt { get; set; }

    /// <summary>
    /// 指向自动创建的隐藏 Site 的标识（逻辑外键）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public Guid LinkedSiteId { get; set; }

    /// <summary>
    /// 标记账号是否启用。禁用后会同步禁用隐藏 Site，转发链路自动绕开该账号。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 用户在 OAuth 页手动禁用（区别于额度巡检/总开关禁用，巡检不会自动恢复手动禁用的账号）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool ManuallyDisabled { get; set; } = false;

    /// <summary>
    /// 因 OAuth 功能总开关关闭而被禁用（重新开启后自动恢复）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool DisabledByFeatureToggle { get; set; } = false;

    /// <summary>
    /// 最近一次额度查询的原始响应（/coding/v1/usages），用于页面缓存展示与巡检解析。
    /// </summary>
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? LastQuotaRawJson { get; set; }

    /// <summary>
    /// 最近一次额度查询时间。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastQuotaCheckedAt { get; set; }

    /// <summary>
    /// 逻辑删除标记。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsDeleted { get; set; } = false;

    /// <summary>
    /// 记录创建时间（UTC）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 记录更新时间（UTC）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
