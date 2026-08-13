using System.Text.Json;
using AITool.Application.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Contracts;
using AITool.Protocol;
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
    /// 离线执行协议转换诊断，不调用真实上游或代理转发链路。
    /// </summary>
    [HttpPost("protocol-diagnostics")]
    public async Task<IActionResult> RunProtocolDiagnostics(
        [FromBody] ProtocolDiagnosticsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings is null || !settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        if (!TryValidateProtocolDiagnosticsRequest(request, out var validationError, out var errorCode))
        {
            return BadRequest(ApiResponse.Fail(validationError, errorCode));
        }

        try
        {
            var result = ConvertProtocolDiagnostics(request);
            if (result.ConversionFailed)
            {
                return BadRequest(ApiResponse.Fail("协议转换失败", "conversion_failed"));
            }

            return Ok(ApiResponse.Ok(new
            {
                direction = request.Direction,
                sourceProtocol = request.SourceProtocol,
                targetProtocol = request.TargetProtocol,
                streaming = request.Streaming,
                convertedPayload = result.Payload,
                eventCount = result.EventCount,
                completionDetected = result.CompletionDetected,
                conversionFailed = false
            }));
        }
        catch (JsonException)
        {
            return BadRequest(ApiResponse.Fail("payload 不是合法 JSON", "invalid_json"));
        }
        catch (Exception)
        {
            return BadRequest(ApiResponse.Fail("协议转换失败", "conversion_failed"));
        }
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

    private static bool TryValidateProtocolDiagnosticsRequest(
        ProtocolDiagnosticsRequest request,
        out string error,
        out string errorCode)
    {
        error = string.Empty;
        errorCode = string.Empty;

        var direction = request.Direction.Trim();
        var sourceProtocol = request.SourceProtocol.Trim();
        var targetProtocol = request.TargetProtocol.Trim();
        if (!string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(direction, "response", StringComparison.OrdinalIgnoreCase))
        {
            error = "direction 只支持 request 或 response";
            errorCode = "invalid_direction";
            return false;
        }

        if (!IsSupportedProtocol(sourceProtocol) || !IsSupportedProtocol(targetProtocol))
        {
            error = "协议只支持 OpenAI、Anthropic 和 Responses";
            errorCode = "invalid_protocol";
            return false;
        }

        // 诊断页面与当前项目约定保持一致，避免误用未经授权的真实模型名。
        if (!string.Equals(request.ModelName.Trim(), "deepseek-v4-flash", StringComparison.OrdinalIgnoreCase))
        {
            error = "模型名只允许 deepseek-v4-flash";
            errorCode = "invalid_model";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Payload))
        {
            error = "payload 不能为空";
            errorCode = "empty_payload";
            return false;
        }

        if (request.Payload.Length > 512 * 1024)
        {
            error = "payload 超过 512 KB 限制";
            errorCode = "payload_too_large";
            return false;
        }

        if (!request.Streaming)
        {
            try
            {
                using var document = JsonDocument.Parse(request.Payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "非流式 payload 必须是 JSON 对象";
                    errorCode = "invalid_json";
                    return false;
                }
            }
            catch (JsonException)
            {
                error = "payload 不是合法 JSON";
                errorCode = "invalid_json";
                return false;
            }

            return true;
        }

        if (string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.EventName))
        {
            error = "Anthropic 流式诊断需要 eventName";
            errorCode = "missing_event_name";
            return false;
        }

        if (!IsSupportedStreamingDirection(direction, sourceProtocol, targetProtocol))
        {
            error = "当前流式协议方向暂未提供离线状态转换";
            errorCode = "unsupported_stream_direction";
            return false;
        }

        if (string.Equals(sourceProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            if (!HasValidResponsesSsePayload(request.Payload))
            {
                error = "Responses 流式 payload 必须是完整 SSE 事件块，且 data 必须是 JSON 对象";
                errorCode = "invalid_stream_payload";
                return false;
            }

            return true;
        }

        if (string.Equals(sourceProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase)
            && (request.Payload.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || request.Payload.Contains("event:", StringComparison.OrdinalIgnoreCase)))
        {
            error = "OpenAI 流式 payload 只接受 data 后的原始 JSON";
            errorCode = "invalid_stream_payload";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(request.Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "流式 payload 必须是单个 JSON 对象";
                errorCode = "invalid_stream_payload";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "流式 payload 不是合法 JSON";
            errorCode = "invalid_stream_payload";
            return false;
        }

        return true;
    }

    private static ProtocolDiagnosticsConversionResult ConvertProtocolDiagnostics(ProtocolDiagnosticsRequest request)
    {
        var direction = request.Direction.Trim();
        var sourceProtocol = request.SourceProtocol.Trim();
        var targetProtocol = request.TargetProtocol.Trim();

        if (!request.Streaming)
        {
            var payload = string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase)
                ? ProxyProtocolBridge.PrepareRequestBody(
                    sourceProtocol,
                    targetProtocol,
                    request.Payload,
                    request.ModelName.Trim(),
                    false,
                    request.OverrideReasoningEffort)
                : ProxyProtocolBridge.AdaptResponseBodyForClient(
                    targetProtocol,
                    sourceProtocol,
                    request.Payload,
                    false,
                    request.ModelName.Trim(),
                    request.InputTokens,
                    request.CachedTokens,
                    request.OutputTokens);

            return new ProtocolDiagnosticsConversionResult(
                payload,
                CountSseEvents(payload),
                false,
                string.IsNullOrWhiteSpace(payload));
        }

        var state = new ChatToResponsesStreamState();
        string convertedPayload;
        if (string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                && targetProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase))
            {
                convertedPayload = ProxyProtocolBridge.ConvertChatStreamChunkToResponses(request.Payload, state);
            }
            else if (sourceProtocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                     && targetProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase))
            {
                convertedPayload = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(
                    request.EventName!.Trim(), request.Payload, state);
            }
            else
            {
                var anthropicState = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
                convertedPayload = ProxyProtocolBridge.BuildAnthropicStreamStart(request.ModelName.Trim(), anthropicState)
                    + ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(request.Payload, anthropicState);
                if (request.Payload.Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    convertedPayload += ProxyProtocolBridge.CompleteAnthropicStream(anthropicState);
                }
            }
        }
        else if (sourceProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                 && targetProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var responsesState = new ResponsesToChatStreamState
            {
                Model = request.ModelName.Trim(),
                InputTokens = request.InputTokens,
                CachedTokens = request.CachedTokens,
                OutputTokens = request.OutputTokens
            };
            convertedPayload = ProxyProtocolBridge.ConvertResponsesStreamingToChat(request.Payload, responsesState);
            return new ProtocolDiagnosticsConversionResult(
                convertedPayload,
                CountSseEvents(convertedPayload),
                responsesState.ConversionFailed,
                responsesState.Completed);
        }
        else
        {
            var anthropicState = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
            convertedPayload = ProxyProtocolBridge.BuildAnthropicStreamStart(request.ModelName.Trim(), anthropicState)
                + ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(request.Payload, anthropicState);
            if (request.Payload.Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                convertedPayload += ProxyProtocolBridge.CompleteAnthropicStream(anthropicState);
            }

            return new ProtocolDiagnosticsConversionResult(
                convertedPayload,
                CountSseEvents(convertedPayload),
                anthropicState.ConversionFailed,
                convertedPayload.Contains("event: message_stop", StringComparison.OrdinalIgnoreCase));
        }

        return new ProtocolDiagnosticsConversionResult(
            convertedPayload,
            CountSseEvents(convertedPayload),
            state.ConversionFailed,
            state.Done);
    }

    private static bool IsSupportedProtocol(string protocol)
        => protocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("Responses", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedStreamingDirection(string direction, string source, string target)
        => (direction.Equals("request", StringComparison.OrdinalIgnoreCase)
            && ((source.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                 && target.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))))
        || (direction.Equals("response", StringComparison.OrdinalIgnoreCase)
            && ((source.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                 && target.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))));

    private static bool HasSseFraming(string payload)
        => payload.Contains("data:", StringComparison.OrdinalIgnoreCase)
            && payload.Contains("\n\n", StringComparison.Ordinal);

    private static bool HasValidResponsesSsePayload(string payload)
    {
        if (!HasSseFraming(payload))
        {
            return false;
        }

        var blocks = payload.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var dataLines = block.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[5..].Trim())
                .ToList();
            if (dataLines.Count == 0)
            {
                return false;
            }

            var data = string.Join("\n", dataLines);
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountSseEvents(string payload)
        => payload.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Count(block => block.Contains("data:", StringComparison.OrdinalIgnoreCase));

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

/// <summary>
/// 离线协议诊断请求，仅承载用户手工输入的协议片段，不包含任何路由或凭据字段。
/// </summary>
public sealed class ProtocolDiagnosticsRequest
{
    public string Direction { get; set; } = string.Empty;
    public string SourceProtocol { get; set; } = string.Empty;
    public string TargetProtocol { get; set; } = string.Empty;
    public bool Streaming { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? EventName { get; set; }
    public string? OverrideReasoningEffort { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
}

internal sealed record ProtocolDiagnosticsConversionResult(
    string Payload,
    int EventCount,
    bool ConversionFailed,
    bool CompletionDetected);
