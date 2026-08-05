using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public partial class CodexAccount : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PlanType { get; set; }
    public string? LastQuotaCheckedAt { get; set; }
    public string? TokenExpiresAt { get; set; }
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private double? _fiveHourUsedPercent;
    [ObservableProperty] private double? _weeklyUsedPercent;
    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string QuotaText => WeeklyUsedPercent.HasValue ? $"周额度 {WeeklyUsedPercent:0.#}%" : "暂无额度";
    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public sealed class CodexOAuthResult
{
    public string Url { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}
