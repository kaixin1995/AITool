using AITool.Application.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 开发者调用追踪 API：查看近期代理请求的全链路详情 + 并发面板 + 熔断状态。
/// </summary>
[ApiController]
[Route("api/admin/developer/invocations")]
public sealed class DeveloperInvocationsApiController : ControllerBase
{
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    private readonly DeveloperInvocationTraceStore _traceStore;
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly AppDbContext _dbContext;

    public DeveloperInvocationsApiController(
        ISystemRuntimeSettingsService runtimeSettingsService,
        DeveloperInvocationTraceStore traceStore,
        ModelConcurrencyLimiter concurrencyLimiter,
        ProxyRequestMetadataCache metadataCache,
        RouteCircuitStateStore circuitStore,
        AppDbContext dbContext)
    {
        _runtimeSettingsService = runtimeSettingsService;
        _traceStore = traceStore;
        _concurrencyLimiter = concurrencyLimiter;
        _metadataCache = metadataCache;
        _circuitStore = circuitStore;
        _dbContext = dbContext;
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
            requestBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(entry.RequestBody) : entry.RequestBody,
            targetSiteId = entry.TargetSiteId,
            targetSiteName = entry.TargetSiteName,
            attemptedModel = entry.AttemptedModel,
            status = entry.Status,
            statusCode = entry.StatusCode,
            errorMessage = entry.ErrorMessage,
            responseBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(entry.ResponseBody) : entry.ResponseBody,
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
                preparedRequestBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(a.PreparedRequestBody) : a.PreparedRequestBody,
                responseBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(a.ResponseBody) : a.ResponseBody,
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

        // x.SiteId 是真实站点 Id（displaySiteId），用于反查站点名；
        // x.MaxConcurrency 已由限制器的 EnrichWithStateInfo 从运行时 state 填充，无需再查缓存字典。
        var items = snapshots.Select(x =>
        {
            return new
            {
                siteId = x.SiteId,
                concurrencyKey = x.ConcurrencyKey,
                modelName = x.SiteModelName,
                siteName = siteNames.TryGetValue(x.SiteId, out var n) ? n : "-",
                activeCount = x.ActiveCount,
                maxConcurrency = x.MaxConcurrency > 0 ? (int?)x.MaxConcurrency : null,
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
    /// 获取当前所有熔断/失败计数中的路由状态。
    /// </summary>
    [HttpGet("circuit-breaker")]
    public async Task<IActionResult> GetCircuitBreakerStates(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var states = _circuitStore.GetAllCircuitStates();
        if (states.Count == 0)
        {
            return Ok(ApiResponse.Ok(new { routes = Array.Empty<object>() }));
        }

        // 熔断存储的 key 现在是 CircuitKey（多 Key 候选为合成 Guid，单 Key/兼容候选为 RouteId 本身）。
        // 从缓存层展开后的路由候选构建 CircuitKey → 候选信息 字典，正确匹配每条熔断状态。
        var allTargets = await _metadataCache.GetAllRouteTargetsAsync(cancellationToken);
        var targetByCircuitKey = allTargets.ToDictionary(x => x.CircuitKey, x => x);

        var result = new List<object>(states.Count);
        foreach (var pair in states)
        {
            var circuitKey = pair.Key;
            var state = pair.Value;
            // 匹配缓存候选；匹配不到（候选已被删除或缓存未刷新）时仅展示熔断状态本身。
            if (targetByCircuitKey.TryGetValue(circuitKey, out var target))
            {
                result.Add(new
                {
                    routeId = target.RouteId,
                    circuitKey,
                    entryName = target.ExternalModelName,
                    upstreamModelName = target.UpstreamModelName,
                    siteName = target.SiteName,
                    siteKeyId = target.SiteKeyId,
                    isBlocked = state.IsBlocked,
                    failureCount = state.FailureCount,
                    blockedUntil = state.BlockedUntil,
                    remainingSeconds = state.RemainingTime != null
                        ? Math.Max(0, (int)Math.Ceiling(state.RemainingTime.Value.TotalSeconds))
                        : (int?)null
                });
            }
            else
            {
                // 候选已不存在（路由/Key 被删除），仍展示熔断状态以便手动解除。
                result.Add(new
                {
                    routeId = Guid.Empty,
                    circuitKey,
                    entryName = "(候选已移除)",
                    upstreamModelName = string.Empty,
                    siteName = string.Empty,
                    siteKeyId = (Guid?)null,
                    isBlocked = state.IsBlocked,
                    failureCount = state.FailureCount,
                    blockedUntil = state.BlockedUntil,
                    remainingSeconds = state.RemainingTime != null
                        ? Math.Max(0, (int)Math.Ceiling(state.RemainingTime.Value.TotalSeconds))
                        : (int?)null
                });
            }
        }

        return Ok(ApiResponse.Ok(new { routes = result }));
    }

    /// <summary>
    /// 手动解除指定路由的熔断状态。
    /// 路径参数 circuitKey 为熔断身份键（多 Key 候选为合成 Guid，兼容候选为 RouteId）。
    /// </summary>
    [HttpPost("circuit-breaker/{circuitKey}/reset")]
    public async Task<IActionResult> ResetCircuitBreaker(Guid circuitKey, CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var removed = _circuitStore.Reset(circuitKey);
        return Ok(ApiResponse.Ok(new { circuitKey, reset = removed }, removed ? "已解除熔断" : "该路由未被熔断"));
    }

    /// <summary>
    /// 解除所有路由的熔断状态。
    /// </summary>
    [HttpPost("circuit-breaker/reset-all")]
    public async Task<IActionResult> ResetAllCircuitBreakers(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var count = _circuitStore.ResetAll();
        return Ok(ApiResponse.Ok(new { resetCount = count }, $"已解除 {count} 条路由的熔断"));
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
