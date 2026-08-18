namespace AITool.Application.Accounts;

/// <summary>
/// 可纳入 OAuth 账号额度巡检的账号摘要。
/// 账号凭证和具体存储由对应的额度提供程序负责，巡检器只依赖这些通用字段。
/// </summary>
public sealed class AccountQuotaTarget
{
    public string ProviderKey { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public Guid LinkedSiteId { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsQuotaCooling { get; init; }
    public bool DisabledByFeatureToggle { get; init; }
    public bool ManuallyDisabled { get; init; }
    public DateTimeOffset? TokenExpiresAt { get; init; }
    public DateTimeOffset? LastQuotaCheckedAt { get; init; }
    public string? LastQuotaRawJson { get; init; }
}

/// <summary>
/// 通用额度窗口。不同 OAuth 提供程序可以返回不同数量的窗口，巡检不限定只能有两个窗口。
/// </summary>
public sealed class AccountQuotaWindow
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public double? UsedPercent { get; init; }
    public string ResetLabel { get; init; } = string.Empty;
    public DateTimeOffset? ResetAtUtc { get; init; }
    public double? LimitWindowSeconds { get; init; }
}

/// <summary>
/// 一次额度查询或缓存解析的通用结果。
/// </summary>
public sealed class AccountQuotaSnapshot
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? PlanType { get; init; }
    public string? RawJson { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<AccountQuotaWindow> Windows { get; init; } = [];
}

/// <summary>
/// OAuth 账号额度提供程序扩展点。
/// 新增 OAuth 账号类型时，只需实现账号枚举、额度查询、缓存解析和启停同步，
/// 即可接入统一的多窗口额度巡检，而不需要复制巡检编排逻辑。
/// </summary>
public interface IAccountQuotaProvider
{
    string ProviderKey { get; }

    Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 从账号持久化的最近一次原始响应恢复额度快照；无法解析时返回 null。
    /// </summary>
    AccountQuotaSnapshot? ParseCachedQuota(string rawJson);

    Task<AccountQuotaSnapshot> QueryAsync(
        AccountQuotaTarget account,
        bool forceRefresh,
        CancellationToken cancellationToken);

    /// <summary>
    /// 同步账号及其关联站点的启用状态。
    /// </summary>
    Task SetEnabledAsync(
        AccountQuotaTarget account,
        bool enabled,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// 响应账号额度功能总开关变化。提供程序负责同步自己的账号和关联站点状态，
    /// 并保留“因总开关禁用”与手动/自动禁用之间的区别。
    /// </summary>
    Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>
/// 单个账号的一次通用额度巡检结果。
/// </summary>
public sealed class AccountInspectionAccountResult
{
    public string ProviderKey { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Action { get; set; } = "keep";
    public string Reason { get; set; } = string.Empty;
    public bool FromCache { get; set; }
    public IReadOnlyList<AccountQuotaWindow> Windows { get; set; } = [];
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 一次完整的通用账号额度巡检结果。
/// </summary>
public sealed class AccountInspectionRunResult
{
    public bool IsRunning { get; set; }
    public bool ForcedRefresh { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<AccountInspectionAccountResult> Accounts { get; set; } = [];
    public int KeepCount => Accounts.Count(a => a.Action == "keep");
    public int DisableCount => Accounts.Count(a => a.Action == "disable");
    public int EnableCount => Accounts.Count(a => a.Action == "enable");
    public int CacheCount => Accounts.Count(a => a.FromCache);
    public int RealRefreshCount => Accounts.Count(a => !a.FromCache);
    public bool AutoTriggered { get; set; }
}

/// <summary>
/// 通用账号额度巡检日志条目。
/// </summary>
public sealed class AccountInspectionLogEntry
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Category { get; init; } = "inspection";
    public string Message { get; init; } = string.Empty;
}
