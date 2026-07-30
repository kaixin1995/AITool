using AITool.Application.Operations;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 开发者调用追踪 API：查看近期代理请求的全链路详情 + 并发面板。
/// <para>
/// 迁移自 <c>Pages/Admin/Developer/Invocations/Index.cshtml.cs</c> 的 OnGetList/OnGetDetail/OnGetConcurrency。
/// 数据源是内存环形缓冲区 <see cref="DeveloperInvocationTraceStore"/>（最多 40 条，20 分钟过期）。
/// 所有端点都先检查 <c>DeveloperFeaturesEnabled</c> 开关，关闭时返回 404。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/developer/invocations")]
public sealed class DeveloperInvocationsApiController : ControllerBase
{
    /// <summary>
    /// 系统运行时设置服务（读取 DeveloperFeaturesEnabled 开关）。
    /// </summary>
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    /// <summary>
    /// 开发者调用追踪存储。
    /// </summary>
    private readonly DeveloperInvocationTraceStore _traceStore;
    /// <summary>
    /// 模型并发限制器（提供并发面板快照）。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    /// <summary>
    /// 代理元数据缓存（提供站点名、并发限制、调试模型清单）。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 初始化开发者调用追踪 API 控制器。
    /// </summary>
    public DeveloperInvocationsApiController(
        ISystemRuntimeSettingsService runtimeSettingsService,
        DeveloperInvocationTraceStore traceStore,
        ModelConcurrencyLimiter concurrencyLimiter,
        ProxyRequestMetadataCache metadataCache)
    {
        _runtimeSettingsService = runtimeSettingsService;
        _traceStore = traceStore;
        _concurrencyLimiter = concurrencyLimiter;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 获取开发者调试初始信息（计数 + 默认调用参数 + 可调试模型清单）。
    /// </summary>
    [HttpGet("init")]
    public async Task<IActionResult> GetInit(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        var defaultAccessKey = await _metadataCache.GetDeveloperDefaultAccessKeyAsync(cancellationToken);
        var routeModels = await _metadataCache.GetDeveloperDebugModelsAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new
        {
            totalCount = entries.Count,
            failedCount = entries.Count(x => x.Attempts.Any(a => !IsSuccessOrPending(a.Status))),
            pendingCount = entries.Count(x => x.Attempts.Any(a => IsPending(a.Status))),
            defaultBaseUrl = $"{Request.Scheme}://{Request.Host}",
            defaultAccessKey,
            models = routeModels,
            defaultOpenAiModel = routeModels.FirstOrDefault(x => x.CanUseOpenAi)?.ModelName ?? string.Empty,
            defaultAnthropicModel = routeModels.FirstOrDefault(x => x.CanUseAnthropic)?.ModelName ?? string.Empty
        }));
    }

    /// <summary>
    /// 获取调用记录列表（不分页，最多 40 条由 TraceStore 上限控制）。
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> GetList([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        pageSize = Math.Clamp(pageSize, 1, 100);
        var totalPages = entries.Count == 0 ? 0 : (int)Math.Ceiling(entries.Count / (double)pageSize);
        var currentPage = totalPages == 0 ? 1 : Math.Min(Math.Max(1, page), totalPages);
        var summaries = entries.Skip((currentPage - 1) * pageSize).Take(pageSize).Select(e => new
        {
            traceId = e.TraceId,
            createdAt = e.CreatedAt,
            source = e.Source,
            protocolType = e.UpstreamProtocolType,
            requestPath = e.RequestPath,
            requestModel = e.RequestModel,
            targetSiteId = e.TargetSiteId,
            targetSiteName = e.TargetSiteName,
            attemptedModel = e.AttemptedModel,
            status = e.Status,
            statusCode = e.StatusCode,
            totalDurationMs = e.TotalDurationMs,
            attemptCount = e.Attempts.Count,
            successAttemptCount = e.Attempts.Count(a => IsSuccess(a.Status)),
            failedAttemptCount = e.Attempts.Count(a => !IsSuccess(a.Status) && !IsPending(a.Status)),
            pendingAttemptCount = e.Attempts.Count(a => IsPending(a.Status))
        }).ToList();

        return Ok(ApiResponse.Ok(new
        {
            page = currentPage,
            pageSize,
            totalPages,
            totalCount = entries.Count,
            failedCount = entries.Count(x => x.Attempts.Any(a => !IsSuccessOrPending(a.Status))),
            pendingCount = entries.Count(x => x.Attempts.Any(a => IsPending(a.Status))),
            entries = summaries
        }));
    }

    /// <summary>
    /// 获取单条调用记录详情（含请求/响应体、每次尝试详情）。
    /// </summary>
    /// <param name="traceId">追踪 Id。</param>
    /// <param name="summarize">true 时对超长 JSON 字符串做摘要（截断），减少传输量。</param>
    [HttpGet("{traceId:guid}")]
    public async Task<IActionResult> GetDetail(Guid traceId, [FromQuery] bool summarize = false, CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var entry = _traceStore.Get(traceId);
        if (entry is null)
        {
            return NotFound(ApiResponse.Fail("调用记录不存在或已过期", "trace_not_found"));
        }

        return Ok(ApiResponse.Ok(new
        {
            traceId = entry.TraceId,
            requestId = entry.RequestId,
            createdAt = entry.CreatedAt,
            updatedAt = entry.UpdatedAt,
            source = entry.Source,
            userAgent = entry.UserAgent,
            clientIp = entry.ClientIp,
            protocolType = entry.UpstreamProtocolType,
            requestPath = entry.RequestPath,
            requestModel = entry.RequestModel,
            requestHeaders = entry.RequestHeaders,
            requestBody = summarize ? DeveloperInvocationTraceStore.FormatBody(entry.RequestBody) : entry.RequestBody,
            targetSiteId = entry.TargetSiteId,
            targetSiteName = entry.TargetSiteName,
            attemptedModel = entry.AttemptedModel,
            status = entry.Status,
            statusCode = entry.StatusCode,
            errorMessage = entry.ErrorMessage,
            responseBody = summarize ? DeveloperInvocationTraceStore.FormatBody(entry.ResponseBody) : entry.ResponseBody,
            responseContentType = entry.ResponseContentType,
            isStreaming = entry.IsStreaming,
            totalDurationMs = entry.TotalDurationMs,
            inputTokens = entry.InputTokens,
            cachedTokens = entry.CachedTokens,
            outputTokens = entry.OutputTokens,
            attempts = entry.Attempts.Select(a => new
            {
                attemptId = a.AttemptId,
                targetSiteId = a.TargetSiteId,
                targetSiteName = a.TargetSiteName,
                attemptedModel = a.AttemptedModel,
                forwardingMode = a.ForwardingMode,
                upstreamProtocolType = a.UpstreamProtocolType,
                status = a.Status,
                statusCode = a.StatusCode,
                errorMessage = a.ErrorMessage,
                preparedRequestBody = summarize ? DeveloperInvocationTraceStore.FormatBody(a.PreparedRequestBody) : a.PreparedRequestBody,
                responseBody = summarize ? DeveloperInvocationTraceStore.FormatBody(a.ResponseBody) : a.ResponseBody,
                responseContentType = a.ResponseContentType,
                isStreaming = a.IsStreaming,
                inputTokens = a.InputTokens,
                cachedTokens = a.CachedTokens,
                outputTokens = a.OutputTokens,
                totalDurationMs = a.TotalDurationMs
            }).ToList()
        }));
    }

    /// <summary>
    /// 获取并发面板快照（按站点+模型的活跃/排队数）。
    /// </summary>
    [HttpGet("concurrency")]
    public async Task<IActionResult> GetConcurrency(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var snapshots = _concurrencyLimiter.ListRecent(ModelConcurrencyLimiter.RecentRetention);
        if (snapshots.Count == 0)
        {
            return Ok(ApiResponse.Ok(new { refreshedAt = DateTimeOffset.Now, items = Array.Empty<object>() }));
        }

        var siteNames = await _metadataCache.GetEnabledSiteNamesAsync(cancellationToken);
        var mappingLimits = await _metadataCache.GetModelConcurrencyLimitsAsync(cancellationToken);

        var items = snapshots.Select(x =>
        {
            var key = $"{x.SiteId:N}:{x.SiteModelName}";
            return new
            {
                siteId = x.SiteId,
                modelName = x.SiteModelName,
                siteName = siteNames.TryGetValue(x.SiteId, out var n) ? n : "-",
                activeCount = x.ActiveCount,
                maxConcurrency = mappingLimits.TryGetValue(key, out var mc) && mc > 0 ? (int?)mc : null,
                queueCount = x.QueueCount,
                lastSeenAt = x.LastSeenAt
            };
        })
        .OrderByDescending(x => x.queueCount > 0 ? 1 : 0)
        .ThenByDescending(x => x.queueCount)
        .ThenBy(x => x.siteName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.modelName, StringComparer.OrdinalIgnoreCase)
        .ToList<object>();

        return Ok(ApiResponse.Ok(new { refreshedAt = DateTimeOffset.Now, items }));
    }

    /// <summary>
    /// 检查开发者功能开关是否开启。
    /// </summary>
    private async Task<bool> IsDeveloperEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        return settings.DeveloperFeaturesEnabled;
    }

    private static bool IsSuccess(string? status)
        => string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsPending(string? status)
        => string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessOrPending(string? status)
        => IsSuccess(status) || IsPending(status);
}
