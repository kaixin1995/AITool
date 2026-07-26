using AITool.Application.Operations;
using AITool.Admin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages.Admin.System;

/// <summary>
/// 系统设置页面模型。
/// Admin 侧通过 <see cref="ISystemRuntimeSettingsService"/> 读写数据库中的系统设置，
/// 保存后通过 <see cref="AdminCacheInvalidationService"/> 触发向 Core 宿主的全量配置同步，
/// Core 收到新快照后会自动更新熔断参数和运行时缓存。
/// </summary>
public class SettingsModel : PageModel
{
    /// <summary>
    /// 系统运行时设置服务，用于读写数据库中的系统配置。
    /// </summary>
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;

    /// <summary>
    /// Admin 侧缓存失效服务，保存设置后触发向 Core 的全量配置同步。
    /// </summary>
    private readonly AdminCacheInvalidationService _cacheInvalidationService;

    /// <summary>
    /// 初始化系统设置页面模型。
    /// </summary>
    public SettingsModel(
        ISystemRuntimeSettingsService systemRuntimeSettingsService,
        AdminCacheInvalidationService cacheInvalidationService)
    {
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
        _cacheInvalidationService = cacheInvalidationService;
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
    /// 处理设置保存提交请求。
    /// 保存到数据库后触发 Core 全量配置同步，使 Core 的熔断参数和运行时缓存即时更新。
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        // 将设置写入数据库
        await _systemRuntimeSettingsService.UpdateAsync(Input, cancellationToken);

        // 触发向 Core 的全量配置同步，Core 收到新快照后会自动：
        // 1. 更新 ProxyRequestMetadataCache 中的运行时设置缓存
        // 2. 调用 RouteCircuitStateStore.UpdateOptions 更新熔断参数
        await _cacheInvalidationService.InvalidateRuntimeSettingsAsync(cancellationToken);

        // Codex 功能总开关联动了托管 Site 的启用状态（SystemRuntimeSettingsService 内部已改 DB），
        // 需额外失效路由/模型缓存并推到 Core，使转发链路立即感知，否则要等下次缓存 TTL 才生效。
        await _cacheInvalidationService.InvalidateRouteTargetsAsync(cancellationToken);
        await _cacheInvalidationService.InvalidateModelMetadataAsync(cancellationToken);

        return RedirectToPage(new { statusMessage = "设置已保存" });
    }

    /// <summary>
    /// 清理 UsageLogs。
    /// 清理完成后同样触发 Core 全量配置同步。
    /// </summary>
    public async Task<IActionResult> OnPostClearUsageLogsAsync(bool clearAll, CancellationToken cancellationToken)
    {
        var deletedCount = await _systemRuntimeSettingsService.ClearUsageLogsAsync(new ClearUsageLogsRequest
        {
            Source = clearAll ? string.Empty : ClearUsageLogs.Source,
            StartTime = clearAll ? null : ClearUsageLogs.StartTime,
            EndTime = clearAll ? null : ClearUsageLogs.EndTime
        }, cancellationToken);

        // 触发向 Core 的全量配置同步
        await _cacheInvalidationService.InvalidateRuntimeSettingsAsync(cancellationToken);

        return RedirectToPage(new { statusMessage = clearAll ? $"已清空全部 UsageLogs，共 {deletedCount} 条" : $"已清空 {deletedCount} 条 UsageLogs" });
    }

    /// <summary>
    /// 从数据库加载当前系统设置到表单。
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
            DeveloperFeaturesEnabled = settings.DeveloperFeaturesEnabled,
            ConversationLogEnabled = settings.ConversationLogEnabled,
            ConcurrencyMode = settings.ConcurrencyMode,
            ConcurrencyQueueTimeoutSeconds = settings.ConcurrencyQueueTimeoutSeconds,
            CodexFeaturesEnabled = settings.CodexFeaturesEnabled,
            CodexInspectionEnabled = settings.CodexInspectionEnabled,
            CodexInspectionIntervalMinutes = settings.CodexInspectionIntervalMinutes,
            CodexQuotaMaxCacheHours = settings.CodexQuotaMaxCacheHours,
            CodexAutoDisableThresholdPercent = settings.CodexAutoDisableThresholdPercent
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
