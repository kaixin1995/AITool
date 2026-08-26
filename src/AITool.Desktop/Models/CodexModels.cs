using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public partial class CodexAccount : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AccountId { get; set; }
    public string? PlanType { get; set; }
    public string? LastQuotaCheckedAt { get; set; }
    public string? TokenExpiresAt { get; set; }
    public bool IsQuotaCooling { get; set; }
    public DateTimeOffset? QuotaCoolingUntil { get; set; }
    public int? ResetCreditsAvailableCount { get; set; }
    public List<CodexQuotaWindow>? Windows { get; set; }

    // —— Google 账号（Antigravity）扩展字段；Codex 账号为空 ——
    public string? AccountKind { get; set; }
    public string? ProjectId { get; set; }
    public string? SubscriptionTier { get; set; }
    public int? CreditAmount { get; set; }

    /// <summary>厂商标识：Codex / Antigravity（Google 账号按 AccountKind 推导，默认 Codex）。</summary>
    public string Provider
    {
        get
        {
            if (string.IsNullOrEmpty(AccountKind)) return "Codex";
            return "Antigravity";
        }
    }

    public bool IsCodex => string.IsNullOrEmpty(AccountKind);
    public bool IsGoogle => !IsCodex;
    public string ProviderBadge => Provider;
    /// <summary>卡片徽标底色（与网页端 NTag 配色对齐：Codex 蓝 / Antigravity 橙）。</summary>
    public IBrush ProviderBadgeBrush => Provider switch
    {
        "Antigravity" => new SolidColorBrush(Color.Parse("#F0A016")),
        _ => new SolidColorBrush(Color.Parse("#2080F0"))
    };
    public string ProjectText => string.IsNullOrWhiteSpace(ProjectId) ? "" : $"项目：{ProjectId}";
    public string CreditText => CreditAmount.HasValue ? $"积分 {CreditAmount}" : "";
    public string EmptyWindowsHint => "暂无额度窗口数据";

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isExportSelected = true;

    [ObservableProperty]
    private double? _fiveHourUsedPercent;

    [ObservableProperty]
    private double? _weeklyUsedPercent;

    public string StatusText => IsQuotaCooling ? "冷却中" : IsEnabled ? "正常" : "已禁用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";
    public bool IsDisabled => !IsEnabled;

    /// <summary>
    /// Token 是否已过期或即将过期（10 分钟内）。
    /// </summary>
    public bool IsTokenExpiringSoon
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TokenExpiresAt) || !DateTimeOffset.TryParse(TokenExpiresAt, out var expiresAt))
                return false;
            return expiresAt <= DateTimeOffset.UtcNow.AddMinutes(10);
        }
    }

    /// <summary>
    /// Token 是否已过期。
    /// </summary>
    public bool IsTokenExpired
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TokenExpiresAt) || !DateTimeOffset.TryParse(TokenExpiresAt, out var expiresAt))
                return false;
            return expiresAt <= DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Token 过期时间的可读文本。
    /// </summary>
    public string TokenExpiresText => CodexDateText.Format(TokenExpiresAt);

    public bool HasWindows => Windows is { Count: > 0 };
    public bool HasNoWindows => !HasWindows;
    public string QuotaText => WeeklyUsedPercent.HasValue
        ? $"周额度剩余 {Math.Max(0, 100 - WeeklyUsedPercent.Value):0.#}%"
        : "暂无额度";
    public string LastQuotaCheckedText => CodexDateText.Format(LastQuotaCheckedAt);

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
        OnPropertyChanged(nameof(IsDisabled));
    }
}

public sealed class CodexQuotaWindow
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double? UsedPercent { get; set; }
    public string? ResetLabel { get; set; }
    // 与网页端一致，进度条和百分比展示剩余额度而不是已用额度。
    public double UsedPercentValue => Math.Clamp(100 - (UsedPercent ?? 0), 0, 100);
    public string UsedPercentText => UsedPercent.HasValue ? $"{Math.Max(0, 100 - UsedPercent.Value):0.#}%" : "-";
    public string ResetText => string.IsNullOrWhiteSpace(ResetLabel) ? "暂无重置时间" : $"重置于 {ResetLabel}";
}

public sealed class CodexOAuthResult
{
    public string Url { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public sealed class CodexCredentialImportResult
{
    public List<CodexAccount> Successes { get; set; } = new();
    public List<CodexCredentialImportFailure> Failures { get; set; } = new();
}

public sealed class CodexCredentialImportFailure
{
    public string? FileName { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed partial class CodexRemoteModelItem : ObservableObject
{
    public string RemoteModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ExistingMappingId { get; set; }
    public bool IsEnabled { get; set; }
    public string? ExistingDisplayName { get; set; }

    [ObservableProperty]
    private string _alias = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(Alias)
        ? RemoteModelName
        : Alias.Trim();

    partial void OnAliasChanged(string value) => OnPropertyChanged(nameof(EffectiveDisplayName));
}

public sealed class CodexResetCredit
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? GrantedAt { get; set; }
    public string? ExpiresAt { get; set; }
    public string GrantedAtText => CodexDateText.Format(GrantedAt);
    public string ExpiresAtText => CodexDateText.Format(ExpiresAt);
}

public sealed class CodexResetCreditsInfo
{
    public int AvailableCount { get; set; }
    public List<CodexResetCredit> Credits { get; set; } = new();
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? RawJson { get; set; }
    public string AvailableCountText => $"可用重置次数：{AvailableCount}";
}

public sealed class CodexInspectionStatus
{
    public bool IsRunning { get; set; }
    public string? NextScheduledAt { get; set; }
    public string? LastFinishedAt { get; set; }
    public string NextScheduledText => CodexDateText.Format(NextScheduledAt);
    public string LastFinishedText => CodexDateText.Format(LastFinishedAt);
}

public sealed class CodexInspectionRunResult
{
    public bool IsRunning { get; set; }
    public bool ForcedRefresh { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public List<CodexInspectionAccountResult> Accounts { get; set; } = new();
    public int KeepCount { get; set; }
    public int DisableCount { get; set; }
    public int EnableCount { get; set; }
    public int CacheCount { get; set; }
    public int RealRefreshCount { get; set; }
    public bool AutoTriggered { get; set; }
    public string FinishedText => CodexDateText.Format(FinishedAt);
    public string RunModeText => AutoTriggered ? "自动巡检" : "手动巡检";
    public string RefreshModeText => ForcedRefresh ? "强制真实刷新" : "允许使用缓存";
}

public sealed class CodexInspectionAccountResult
{
    public string AccountId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Action { get; set; } = "keep";
    public string Reason { get; set; } = string.Empty;
    public bool FromCache { get; set; }
    public double? WeeklyUsedPercent { get; set; }
    public double? FiveHourUsedPercent { get; set; }
    public string? CheckedAt { get; set; }

    public string ActionText => Action.ToLowerInvariant() switch
    {
        "disable" => "禁用",
        "enable" => "启用",
        _ => "保留"
    };

    public string FiveHourText => FiveHourUsedPercent.HasValue ? $"{FiveHourUsedPercent.Value:0.0}%" : "-";
    public string WeeklyText => WeeklyUsedPercent.HasValue ? $"{WeeklyUsedPercent.Value:0.0}%" : "-";
    public string SourceText => FromCache ? "缓存" : "实时";
    public string CheckedAtText => CodexDateText.Format(CheckedAt);
}

public sealed class CodexInspectionLog
{
    public string At { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AtText => CodexDateText.Format(At);
}

internal static class CodexDateText
{
    public static string Format(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "从未";
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }
}

// —— Google 账号（Antigravity）OAuth 支撑模型 ——

public sealed class GoogleOAuthResult
{
    public string Url { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Kind { get; set; } = "Antigravity";
}

public sealed class GoogleCredentialImportResult
{
    public List<object> Successes { get; set; } = new();
    public List<CodexCredentialImportFailure> Failures { get; set; } = new();
}

/// <summary>客户端特征模拟预设选项（站点/映射编辑下拉与网页端 clientEmulationOptions 对齐）。</summary>
public sealed class ClientEmulationOption
{
    public string Value { get; set; } = "None";
    public string Label { get; set; } = "无 (None - 标准API直连)";

    public static List<ClientEmulationOption> Defaults() =>
    [
        new() { Value = "None", Label = "无 (None - 标准API直连)" },
        new() { Value = "CodexCli", Label = "Codex Desktop 官方客户端 (默认)" },
        new() { Value = "CodexVsCode", Label = "Codex VS Code 插件" },
        new() { Value = "OpenCode", Label = "OpenCode CLI 终端" },
        new() { Value = "ClaudeCode", Label = "Claude Code 官方命令行" },
        new() { Value = "ZCode", Label = "ZCode / GLM 客户端" },
        new() { Value = "Antigravity", Label = "Google Antigravity CLI" },
        new() { Value = "Custom", Label = "自定义特征 (Custom)" }
    ];
}

/// <summary>网络代理池方案（ProxyProfile，站点/映射出口代理下拉数据源）。</summary>
public sealed class ProxyProfileItem
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProxyUrl { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public string DisplayLabel => $"{Name} ({ProxyUrl})";
}
