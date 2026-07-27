using AITool.Application.Operations;
using AITool.Infrastructure.Proxy;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 系统运行时设置 API：读取/保存代理、检测、熔断、并发、日志、Codex 等全局配置。
/// <para>
/// 迁移自 <c>Pages/Admin/System/Settings.cshtml.cs</c>。
/// 保存设置后会联动熔断参数与缓存失效（Codex 总开关的托管站点启停在 Service 层处理）。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/system")]
public sealed class SystemSettingsApiController : ControllerBase
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
    /// 统计查询执行器（清空 UsageLogs 后需失效其缓存）。
    /// </summary>
    private readonly AnalyticsBackgroundQueryExecutor _analyticsQueryExecutor;

    /// <summary>
    /// 初始化系统设置 API 控制器。
    /// </summary>
    public SystemSettingsApiController(
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
    /// 获取当前系统设置（含最近一次 UsageLogs 清理数量）。
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _systemRuntimeSettingsService.GetOrCreateAsync(cancellationToken);
        return Ok(ApiResponse.Ok(new
        {
            proxyRequestTimeoutSeconds = settings.ProxyRequestTimeoutSeconds,
            proxyRetryCount = settings.ProxyRetryCount,
            detectionRequestTimeoutSeconds = settings.DetectionRequestTimeoutSeconds,
            detectionRetryCount = settings.DetectionRetryCount,
            detectionConcurrency = settings.DetectionConcurrency,
            circuitBreakerFailureThreshold = settings.CircuitBreakerFailureThreshold,
            circuitBreakerRecoveryMinutes = settings.CircuitBreakerRecoveryMinutes,
            usageLogRetentionDays = settings.UsageLogRetentionDays,
            usageLogAutoCleanupEnabled = settings.UsageLogAutoCleanupEnabled,
            developerFeaturesEnabled = settings.DeveloperFeaturesEnabled,
            conversationLogEnabled = settings.ConversationLogEnabled,
            concurrencyMode = settings.ConcurrencyMode,
            concurrencyQueueTimeoutSeconds = settings.ConcurrencyQueueTimeoutSeconds,
            codexFeaturesEnabled = settings.CodexFeaturesEnabled,
            codexInspectionEnabled = settings.CodexInspectionEnabled,
            codexInspectionIntervalMinutes = settings.CodexInspectionIntervalMinutes,
            codexQuotaMaxCacheHours = settings.CodexQuotaMaxCacheHours,
            codexAutoDisableThresholdPercent = settings.CodexAutoDisableThresholdPercent,
            lastUsageLogPrunedAt = settings.LastUsageLogPrunedAt,
            lastUsageLogPrunedCount = settings.LastUsageLogPrunedCount
        }));
    }

    /// <summary>
    /// 保存系统设置。保存后联动：失效运行时设置缓存、更新熔断参数、失效路由/模型缓存
    /// （Codex 总开关的托管站点启停已在 <see cref="ISystemRuntimeSettingsService.UpdateAsync"/> 内部处理）。
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSystemRuntimeSettingsRequest request, CancellationToken cancellationToken)
    {
        // UpdateAsync 内部对各数值字段有下限保护，无效值会被夹紧，无需在此重复校验。
        var settings = await _systemRuntimeSettingsService.UpdateAsync(request, cancellationToken);

        _metadataCache.InvalidateRuntimeSettings();
        _circuitStore.UpdateOptions(
            TimeSpan.FromMinutes(settings.CircuitBreakerRecoveryMinutes),
            settings.CircuitBreakerFailureThreshold);
        // Codex 功能总开关联动了托管站点的启用状态，需同步失效路由/模型缓存，使转发链路立即感知。
        _metadataCache.InvalidateRuntimeRouteTargets();
        _metadataCache.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok("设置已保存"));
    }

    /// <summary>
    /// 按条件清空使用日志。
    /// <param name="clearAll">true 表示清空全部；false 表示按 source/时间范围筛选清空。</param>
    /// </summary>
    [HttpPost("clear-usage-logs")]
    public async Task<IActionResult> ClearUsageLogs([FromQuery] bool clearAll, [FromBody] ClearUsageLogsRequest? request, CancellationToken cancellationToken)
    {
        var effectiveRequest = clearAll
            ? new ClearUsageLogsRequest()
            : request ?? new ClearUsageLogsRequest();

        var deletedCount = await _systemRuntimeSettingsService.ClearUsageLogsAsync(effectiveRequest, cancellationToken);

        _metadataCache.InvalidateRuntimeSettings();
        _analyticsQueryExecutor.InvalidateAll();

        return Ok(ApiResponse.Ok(new { deletedCount }, clearAll ? $"已清空全部 UsageLogs，共 {deletedCount} 条" : $"已清空 {deletedCount} 条 UsageLogs"));
    }
}
