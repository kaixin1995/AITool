using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class ClearLogSourceOption
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public partial class SystemSettings : ObservableObject
{
    [ObservableProperty] private int _proxyRequestTimeoutSeconds = 60;
    [ObservableProperty] private int _proxyRetryCount = 1;
    [ObservableProperty] private int _detectionRequestTimeoutSeconds = 60;
    [ObservableProperty] private int _detectionRetryCount;
    [ObservableProperty] private int _detectionConcurrency = 1;
    [ObservableProperty] private int _circuitBreakerFailureThreshold = 5;
    [ObservableProperty] private int _circuitBreakerRecoveryMinutes = 2;
    [ObservableProperty] private int _usageLogRetentionDays = 7;
    [ObservableProperty] private bool _usageLogAutoCleanupEnabled = true;
    [ObservableProperty] private bool _developerFeaturesEnabled;
    [ObservableProperty] private int _concurrencyMode;
    [ObservableProperty] private int _concurrencyQueueTimeoutSeconds = 120;
    [ObservableProperty] private bool _oAuthFeaturesEnabled;
    [ObservableProperty] private bool _oAuthInspectionEnabled;
    [ObservableProperty] private int _oAuthInspectionIntervalSeconds = 1800;
    [ObservableProperty] private int _oAuthQuotaMaxCacheHours = 6;
    [ObservableProperty] private int _oAuthAutoDisableThresholdPercent = 95;
    public string? LastUsageLogPrunedAt { get; set; }
    public int LastUsageLogPrunedCount { get; set; }
    public string LastUsageLogPrunedText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LastUsageLogPrunedAt))
            {
                return $"最近一次自动清理数量：{LastUsageLogPrunedCount}";
            }

            if (DateTimeOffset.TryParse(
                    LastUsageLogPrunedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return $"最近一次自动清理：{parsed.ToLocalTime():yyyy-MM-dd HH:mm}，共 {LastUsageLogPrunedCount} 条";
            }

            return $"最近一次自动清理：{LastUsageLogPrunedAt}，共 {LastUsageLogPrunedCount} 条";
        }
    }
}
