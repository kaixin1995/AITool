using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class ModelHealthDashboard
{
    public List<ModelHealthMonitoredModel> MonitoredModels { get; set; } = new();
    public List<ModelHealthModelOption> AvailableModels { get; set; } = new();
    public Dictionary<string, List<ModelHealthSite>> HealthData { get; set; } = new();
    public List<ModelHealthRangeOption> RangeOptions { get; set; } = new();
}

public sealed class ModelHealthModelOption
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ModelHealthRangeOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed partial class ModelHealthMonitoredModel : ObservableObject
{
    public string ModelLibraryItemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SiteCount { get; set; }
    public int HealthySiteCount { get; set; }
    public int UnhealthySiteCount { get; set; }
    public string? LastCheckedAt { get; set; }
    public double? AverageDurationMs { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int TotalRequestCount { get; set; }
    public double AverageSuccessRate { get; set; }

    public List<ModelHealthTimelineSegment> TimelineSegments { get; set; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [JsonIgnore]
    public ObservableCollection<ModelHealthSite> Sites { get; } = new();

    [JsonIgnore]
    public bool HasSites => Sites.Count > 0;

    [JsonIgnore]
    public bool HasNoSites => !HasSites;

    [JsonIgnore]
    public bool IsCollapsed => !IsExpanded;

    [JsonIgnore]
    public string SiteSummaryText => $"{SiteCount} 个关联站点";

    [JsonIgnore]
    public string HealthySiteText => $"正常 {HealthySiteCount}";

    [JsonIgnore]
    public string UnhealthySiteText => $"异常 {UnhealthySiteCount}";

    [JsonIgnore]
    public string HealthSummaryText => $"正常 {HealthySiteCount} · 异常 {UnhealthySiteCount}";

    [JsonIgnore]
    public string SuccessFailureText => $"{SuccessCount} / {FailureCount}";

    [JsonIgnore]
    public string SuccessRateText => ModelHealthText.FormatPercent(AverageSuccessRate);

    [JsonIgnore]
    public bool IsHighSuccessRate => double.IsFinite(AverageSuccessRate) && AverageSuccessRate >= 0.8;

    [JsonIgnore]
    public bool IsMediumSuccessRate => double.IsFinite(AverageSuccessRate) && AverageSuccessRate >= 0.5 && AverageSuccessRate < 0.8;

    [JsonIgnore]
    public bool IsLowSuccessRate => double.IsFinite(AverageSuccessRate) && AverageSuccessRate < 0.5;

    [JsonIgnore]
    public bool IsUnknownSuccessRate => !double.IsFinite(AverageSuccessRate);

    [JsonIgnore]
    public string AverageDurationText => ModelHealthText.FormatDuration(AverageDurationMs);

    [JsonIgnore]
    public string LastCheckedText => ModelHealthText.FormatDateTime(LastCheckedAt);

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));

    public void SetSites(IEnumerable<ModelHealthSite> sites)
    {
        Sites.Clear();
        foreach (var site in sites)
        {
            Sites.Add(site);
        }

        OnPropertyChanged(nameof(HasSites));
        OnPropertyChanged(nameof(HasNoSites));
    }
}

public sealed class ModelHealthSite
{
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string LastStatus { get; set; } = string.Empty;
    public string? LastCheckedAt { get; set; }
    public double? LastDurationMs { get; set; }
    public double SuccessRate { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int TotalRequestCount { get; set; }
    public List<ModelHealthTimelineSegment> TimelineSegments { get; set; } = new();

    [JsonIgnore]
    public bool IsSuccess => string.Equals(LastStatus, "success", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFailed => string.Equals(LastStatus, "fail", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsUnknown => !IsSuccess && !IsFailed;

    [JsonIgnore]
    public bool HasTimeline => TimelineSegments.Count > 0;

    [JsonIgnore]
    public bool HasNoTimeline => !HasTimeline;

    [JsonIgnore]
    public string SuccessFailureText => $"{SuccessCount} / {FailureCount}";

    [JsonIgnore]
    public string StatusText => IsSuccess ? "正常" : IsFailed ? "异常" : "未知";

    [JsonIgnore]
    public string SuccessRateText => ModelHealthText.FormatPercent(SuccessRate);

    [JsonIgnore]
    public bool IsHighSuccessRate => double.IsFinite(SuccessRate) && SuccessRate >= 0.8;

    [JsonIgnore]
    public bool IsMediumSuccessRate => double.IsFinite(SuccessRate) && SuccessRate >= 0.5 && SuccessRate < 0.8;

    [JsonIgnore]
    public bool IsLowSuccessRate => double.IsFinite(SuccessRate) && SuccessRate < 0.5;

    [JsonIgnore]
    public bool IsUnknownSuccessRate => !double.IsFinite(SuccessRate);

    [JsonIgnore]
    public double SuccessRatePercent => Math.Clamp(SuccessRate * 100, 0, 100);

    [JsonIgnore]
    public string DurationText => ModelHealthText.FormatDuration(LastDurationMs);

    [JsonIgnore]
    public string LastCheckedText => ModelHealthText.FormatDateTime(LastCheckedAt);
}

public sealed class ModelHealthTimelineSegment
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string StartAt { get; set; } = string.Empty;
    public string EndAt { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsAllSuccess => Count > 0 && FailureCount == 0;

    [JsonIgnore]
    public bool IsAllFailed => Count > 0 && SuccessCount == 0;

    [JsonIgnore]
    public bool IsMixed => Count > 0 && SuccessCount > 0 && FailureCount > 0;

    [JsonIgnore]
    public bool IsEmpty => Count <= 0;

    [JsonIgnore]
    public string TooltipText =>
        $"{ModelHealthText.FormatDateTime(StartAt)} - {ModelHealthText.FormatDateTime(EndAt)} · 请求 {Count} · 成功 {SuccessCount} · 失败 {FailureCount}";
}

internal static class ModelHealthText
{
    public static string FormatPercent(double value)
    {
        if (!double.IsFinite(value)) return "-";
        return $"{value * 100:0.0}%";
    }

    public static string FormatDuration(double? value)
    {
        if (!value.HasValue || value <= 0 || !double.IsFinite(value.Value)) return "-";
        return value >= 1000 ? $"{value.Value / 1000:0.0}s" : $"{value.Value:0}ms";
    }

    public static string FormatDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "从未";
        return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var date)
            ? date.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }
}
