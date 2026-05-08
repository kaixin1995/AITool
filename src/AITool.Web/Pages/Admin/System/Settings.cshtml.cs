using AITool.Application.Operations;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.System;

// 系统设置页面模型，负责加载、保存运行时配置与执行危险操作
public class SettingsModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly AnalyticsBackgroundQueryExecutor _analyticsQueryExecutor;

    public SettingsModel(
        ISystemRuntimeSettingsService systemRuntimeSettingsService,
        ProxyRequestMetadataCache metadataCache,
        RouteCircuitStateStore circuitStore,
        AnalyticsBackgroundQueryExecutor analyticsQueryExecutor)
    {
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
        _metadataCache = metadataCache;
        _circuitStore = circuitStore;
        _analyticsQueryExecutor = analyticsQueryExecutor;
    }

    [BindProperty]
    public UpdateSystemRuntimeSettingsRequest Input { get; set; } = new();

    [BindProperty]
    public ClearUsageLogsInput ClearUsageLogs { get; set; } = new();

    public int LastUsageLogPrunedCount { get; set; }

    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var settings = await _systemRuntimeSettingsService.UpdateAsync(Input, cancellationToken);
        _metadataCache.InvalidateRuntimeSettings();
        _circuitStore.UpdateOptions(
            TimeSpan.FromMinutes(settings.CircuitBreakerRecoveryMinutes),
            settings.CircuitBreakerFailureThreshold);
        return RedirectToPage(new { statusMessage = "设置已保存" });
    }

    public async Task<IActionResult> OnPostClearUsageLogsAsync(bool clearAll, CancellationToken cancellationToken)
    {
        var deletedCount = await _systemRuntimeSettingsService.ClearUsageLogsAsync(new ClearUsageLogsRequest
        {
            Source = clearAll ? string.Empty : ClearUsageLogs.Source,
            StartTime = clearAll ? null : ClearUsageLogs.StartTime,
            EndTime = clearAll ? null : ClearUsageLogs.EndTime
        }, cancellationToken);

        _metadataCache.InvalidateRuntimeSettings();
        _analyticsQueryExecutor.InvalidateAll();
        return RedirectToPage(new { statusMessage = clearAll ? $"已清空全部 UsageLogs，共 {deletedCount} 条" : $"已清空 {deletedCount} 条 UsageLogs" });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await _systemRuntimeSettingsService.GetOrCreateAsync(cancellationToken);
        Input = new UpdateSystemRuntimeSettingsRequest
        {
            ProxyRequestTimeoutSeconds = settings.ProxyRequestTimeoutSeconds,
            ProxyRetryCount = settings.ProxyRetryCount,
            DetectionRequestTimeoutSeconds = settings.DetectionRequestTimeoutSeconds,
            DetectionRetryCount = settings.DetectionRetryCount,
            DetectionConcurrency = settings.DetectionConcurrency,
            CircuitBreakerFailureThreshold = settings.CircuitBreakerFailureThreshold,
            CircuitBreakerRecoveryMinutes = settings.CircuitBreakerRecoveryMinutes,
            UsageLogRetentionDays = settings.UsageLogRetentionDays,
            UsageLogAutoCleanupEnabled = settings.UsageLogAutoCleanupEnabled,
            DeveloperFeaturesEnabled = settings.DeveloperFeaturesEnabled
        };
        LastUsageLogPrunedCount = settings.LastUsageLogPrunedCount;
        StatusMessage = Request.Query["statusMessage"];
    }
}

public sealed class ClearUsageLogsInput
{
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset? StartTime { get; set; }

    public DateTimeOffset? EndTime { get; set; }
}
