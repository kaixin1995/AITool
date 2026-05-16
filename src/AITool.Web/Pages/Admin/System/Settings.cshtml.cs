using AITool.Application.Operations;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.System;

/// <summary>
/// 系统设置页面模型。
/// </summary>
public class SettingsModel : PageModel
{
    /// <summary>
    /// 系统运行时设置服务。
    /// </summary>
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>
    /// 熔断状态存储。
    /// </summary>
    private readonly RouteCircuitStateStore _circuitStore;
    /// <summary>
    /// 统计查询执行器。
    /// </summary>
    private readonly AnalyticsBackgroundQueryExecutor _analyticsQueryExecutor;

    /// <summary>
    /// 系统设置页面模型。
    /// </summary>
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

    /// <summary>
    /// 系统设置表单提交数据。
    /// </summary>
    [BindProperty]
    public UpdateSystemRuntimeSettingsRequest Input { get; set; } = new();

    /// <summary>
    /// 清理 UsageLogs 表单提交数据。
    /// </summary>
    [BindProperty]
    public ClearUsageLogsInput ClearUsageLogs { get; set; } = new();

    /// <summary>
    /// 最近一次清理的 UsageLogs 数量。
    /// </summary>
    public int LastUsageLogPrunedCount { get; set; }

    /// <summary>
    /// 状态提示。
    /// </summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// 处理页面提交请求。
    /// </summary>
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

    /// <summary>
    /// 清理 UsageLogs。
    /// </summary>
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

    /// <summary>
    /// 加载系统设置。
    /// </summary>
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

/// <summary>
/// 清理 UsageLogs 的输入条件。
/// </summary>
public sealed class ClearUsageLogsInput
{
    /// <summary>
    /// 来源。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 开始时间。
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// 结束时间。
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }
}
