using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

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
    [ObservableProperty] private bool _codexFeaturesEnabled;
    [ObservableProperty] private bool _codexInspectionEnabled;
    [ObservableProperty] private int _codexInspectionIntervalMinutes = 30;
    [ObservableProperty] private int _codexQuotaMaxCacheHours = 6;
    [ObservableProperty] private int _codexAutoDisableThresholdPercent = 95;
    public string? LastUsageLogPrunedAt { get; set; }
    public int LastUsageLogPrunedCount { get; set; }
}
