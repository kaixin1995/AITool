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
    public List<CodexQuotaWindow> Windows { get; set; } = new();

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private double? _fiveHourUsedPercent;

    [ObservableProperty]
    private double? _weeklyUsedPercent;

    public string StatusText => IsQuotaCooling ? "冷却中" : IsEnabled ? "正常" : "已禁用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";
    public bool HasWindows => Windows.Count > 0;
    public bool HasNoWindows => !HasWindows;
    public string QuotaText => WeeklyUsedPercent.HasValue
        ? $"周额度剩余 {Math.Max(0, 100 - WeeklyUsedPercent.Value):0.#}%"
        : "暂无额度";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
    }
}

public sealed class CodexQuotaWindow
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double? UsedPercent { get; set; }
    public string? ResetLabel { get; set; }
    public double UsedPercentValue => Math.Clamp(UsedPercent ?? 0, 0, 100);
    public string UsedPercentText => UsedPercent.HasValue ? $"{UsedPercent.Value:0.#}%" : "-";
    public string ResetText => string.IsNullOrWhiteSpace(ResetLabel) ? "暂无重置时间" : $"重置于 {ResetLabel}";
}

public sealed class CodexOAuthResult
{
    public string Url { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}
