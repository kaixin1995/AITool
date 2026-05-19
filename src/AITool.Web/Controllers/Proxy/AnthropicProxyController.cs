using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

/// <summary>
/// 处理 Anthropic 协议代理请求，并在需要时完成与 OpenAI 协议之间的兼容转换。
/// </summary>
[ApiController]
public sealed class AnthropicProxyController : ControllerBase
{
    /// <summary>
    /// 表示一次流式转发的执行结果，以及当前响应是否还能继续回退到下一条路由。
    /// </summary>
    private sealed class StreamForwardOutcome
    {
        /// <summary>
        /// 保存本次流式转发返回的结果。
        /// </summary>
        public ProxyForwardResult Result { get; init; } = new();
        /// <summary>
        /// 指示当前流是否还允许继续尝试下一条候选路由。
        /// </summary>
        public bool CanFallback { get; init; }
    }

    /// <summary>
    /// 负责把代理请求转发到上游站点。
    /// </summary>
    private readonly IProxyForwardService _forwardService;
    /// <summary>
    /// 负责记录代理请求的用量与结果。
    /// </summary>
    private readonly IUsageLogService _usageLogService;
    /// <summary>
    /// 负责维护路由熔断状态，避免持续命中异常站点。
    /// </summary>
    private readonly RouteCircuitStateStore _circuitStore;
    /// <summary>
    /// 提供访问密钥、路由和运行时设置等缓存数据。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>
    /// 保存开发者调试页需要展示的调用追踪信息。
    /// </summary>
    private readonly DeveloperInvocationTraceStore _traceStore;
    /// <summary>
    /// 模型并发限制器，按站点+模型粒度控制最大并发请求数。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    /// <summary>
    /// 记录代理过程中的诊断日志。
    /// </summary>
    private readonly ILogger<AnthropicProxyController> _logger;

    /// <summary>
    /// 初始化 Anthropic 代理控制器依赖。
    /// </summary>
    public AnthropicProxyController(
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache,
        DeveloperInvocationTraceStore traceStore,
        ModelConcurrencyLimiter concurrencyLimiter,
        ILogger<AnthropicProxyController> logger)
    {
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
        _traceStore = traceStore;
        _concurrencyLimiter = concurrencyLimiter;
        _logger = logger;
    }

    /// <summary>
    /// 估算 Anthropic 请求中的输入 token 数量。
    /// </summary>
    [HttpPost("/v1/messages/count_tokens")]
    public async Task<IActionResult> CountTokens(CancellationToken cancellationToken)
    {
        var accessKey = await ValidateAccessKeyAsync(cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { type = "authentication_error", message = "Invalid or missing access key" } });
        }

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            var root = document.RootElement;
            var inputTokens = EstimateInputTokens(root);
            return Ok(new
            {
                input_tokens = inputTokens
            });
        }
        catch
        {
            return BadRequest(new { error = new { type = "invalid_request_error", message = "Invalid request body" } });
        }
    }

    /// <summary>
    /// 处理 Anthropic 消息请求，并按路由配置转发到可用上游。
    /// </summary>
    [HttpPost("/v1/messages")]
    public async Task<IActionResult> Messages(CancellationToken cancellationToken)
    {
        // 读取原始请求体
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        // 解析请求中的模型名称
        string modelName;
        var enableStreaming = false;
        var reasoningEffort = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            modelName = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
            enableStreaming = doc.RootElement.TryGetProperty("stream", out var streamValue)
                && streamValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                && streamValue.GetBoolean();
            reasoningEffort = ResolveReasoningEffort(doc.RootElement);
        }
        catch
        {
            return BadRequest(new { error = new { type = "invalid_request_error", message = "Invalid request body" } });
        }

        // 验证访问密钥
        var accessKey = await ValidateAccessKeyAsync(cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { type = "authentication_error", message = "Invalid or missing access key" } });
        }

        // 优先读取显式来源标记，其次退回到 User-Agent 识别常见客户端工具。
        var requestSource = ResolveRequestSource(Request);
        var forwardHeaders = CollectAnthropicForwardHeaders(Request);

        // 读取运行时设置缓存，后台修改后会在短时间内刷新。
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "Anthropic", modelName, requestBody);

        // 获取已经和站点信息合并后的候选路由，优先尝试支持 Anthropic 原协议的站点。
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("Anthropic", modelName, cancellationToken);

        if (allRoutes.Count == 0)
        {
            return NotFound(new { error = new { type = "not_found_error", message = $"No available route for model: {modelName}" } });
        }

        // 按优先级逐个尝试路由，失败则通知熔断器并继续下一个
        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            // 跳过已被熔断器屏蔽的路由
            if (IsRouteBlockedSafely(route.RouteId))
                continue;

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("Anthropic");

            // 按站点+模型粒度获取并发许可，根据配置决定跳过或排队。
            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken);

            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType);
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Anthropic",
                actualProtocolType,
                requestBody,
                route.SiteModelName,
                enableStreaming);
            var forwardRequest = new ProxyForwardRequest
            {
                TargetBaseUrl = route.BaseUrl,
                TargetApiKey = route.ApiKey,
                ProtocolType = actualProtocolType,
                TargetModelName = route.SiteModelName,
                RequestBody = requestBody,
                PreparedRequestBody = preparedRequestBody,
                EnableStreaming = enableStreaming,
                RequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                ForwardHeaders = forwardHeaders
            };

            if (enableStreaming)
            {
                var streamOutcome = string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase)
                    ? await ForwardOpenAiStreamAsAnthropicAsync(
                        forwardRequest,
                        modelName,
                        traceId,
                        traceAttemptId,
                        cancellationToken)
                    : await ForwardAnthropicStreamPassthroughAsync(
                        forwardRequest,
                        traceId,
                        traceAttemptId,
                        cancellationToken);
                var streamResult = streamOutcome.Result;
                SafeWriteConsoleProxyLog("Anthropic", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);

                await SafeLogUsageAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "Anthropic",
                    ForwardingMode = ResolveForwardingMode("Anthropic", actualProtocolType),
                    RequestModel = modelName,
                    AttemptedModel = route.UpstreamModelName,
                    TargetSiteId = route.SiteId,
                    Status = streamResult.Success ? "success" : "fail",
                    Source = requestSource,
                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                    AttemptIndex = attemptIndex,
                    IsFinalResult = streamResult.Success,
                    FallbackTriggered = !streamResult.Success,
                    ErrorMessage = streamResult.Success ? string.Empty : (streamResult.ErrorMessage ?? string.Empty),
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    IsStreaming = streamResult.IsStreaming,
                    IsStreamInterrupted = streamResult.IsStreamInterrupted,
                    FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                    StreamDurationMs = streamResult.StreamDurationMs,
                    TotalDurationMs = streamResult.TotalDurationMs,
                    ReasoningEffort = reasoningEffort
                }, CancellationToken.None);

                if (streamResult.Success)
                {
                    SafeSucceedRoute(route.RouteId);
                    return new EmptyResult();
                }

                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "fail",
                    StatusCode = streamResult.StatusCode,
                    ErrorMessage = streamResult.ErrorMessage ?? string.Empty,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(streamResult.ResponseBody),
                    ResponseContentType = "text/event-stream",
                    IsStreaming = true,
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    TotalDurationMs = streamResult.TotalDurationMs
                });
                SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, streamResult);

                SafeBlockRoute(route.RouteId);
                lastResult = streamResult;
                if (!streamOutcome.CanFallback)
                {
                    return new EmptyResult();
                }

                continue;
            }

            // 每条路由使用独立超时令牌，不受客户端断连影响，保证后续路由仍能独立尝试。
            var result = await _forwardService.ForwardAsync(forwardRequest, CancellationToken.None);
            SafeWriteConsoleProxyLog("Anthropic", requestSource, modelName, actualProtocolType, preparedRequestBody, result, requestBody.Length);

            await SafeLogUsageAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKey.Id,
                ProtocolType = "Anthropic",
                ForwardingMode = ResolveForwardingMode("Anthropic", actualProtocolType),
                RequestModel = modelName,
                AttemptedModel = route.UpstreamModelName,
                TargetSiteId = route.SiteId,
                Status = result.Success ? "success" : "fail",
                Source = requestSource,
                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,
                AttemptIndex = attemptIndex,
                IsFinalResult = result.Success,
                FallbackTriggered = !result.Success,
                ErrorMessage = result.Success ? string.Empty : (result.ErrorMessage ?? string.Empty),
                InputTokens = result.InputTokens,
                CachedTokens = result.CachedTokens,
                OutputTokens = result.OutputTokens,
                IsStreaming = result.IsStreaming,
                IsStreamInterrupted = result.IsStreamInterrupted,
                FirstTokenLatencyMs = result.FirstTokenLatencyMs,
                StreamDurationMs = result.StreamDurationMs,
                TotalDurationMs = result.TotalDurationMs,
                ReasoningEffort = reasoningEffort
            }, cancellationToken);

            if (result.Success)
            {
                // 成功时清除该路由的连续失败计数
                SafeSucceedRoute(route.RouteId);
                if (result.IsStreaming &&
                    string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                    HttpContext.Response.HasStarted)
                {
                    return new EmptyResult();
                }

                var responseBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "Anthropic",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens);
                if (result.IsStreaming && result.HasStartedStreaming && result.IsStreamInterrupted && string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    responseBody = ProxyProtocolBridge.EnsureAnthropicStreamClosed(responseBody, modelName, result.InputTokens, result.CachedTokens, result.OutputTokens);
                }
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "success",
                    StatusCode = result.StatusCode,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(responseBody),
                    ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json",
                    IsStreaming = result.IsStreaming,
                    InputTokens = result.InputTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.OutputTokens,
                    TotalDurationMs = result.TotalDurationMs
                });
                // 流式响应以 SSE 格式返回，使用 text/event-stream 内容类型
                var contentType = result.IsStreaming ? "text/event-stream" : "application/json";
                return Content(responseBody, contentType);
            }

            SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
            {
                Status = "fail",
                StatusCode = result.StatusCode,
                ErrorMessage = result.ErrorMessage ?? string.Empty,
                ResponseBody = DeveloperInvocationTraceStore.FormatBody(result.ResponseBody),
                ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json",
                IsStreaming = result.IsStreaming,
                InputTokens = result.InputTokens,
                CachedTokens = result.CachedTokens,
                OutputTokens = result.OutputTokens,
                TotalDurationMs = result.TotalDurationMs
            });
            SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);

            // 转发失败，通知熔断器（达到阈值才会真正触发熔断）
            SafeBlockRoute(route.RouteId);
            lastResult = result;
        }

        // 所有路由均失败
        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { type = "api_error", message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }

    /// <summary>
    /// 透传 Anthropic 原生流式响应，并在透传过程中提取用量信息。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardAnthropicStreamPassthroughAsync(
        ProxyForwardRequest forwardRequest,
        Guid? traceId,
        Guid traceAttemptId,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var receivedMessageStop = false;
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;

        async Task WriteRawSseBlockAsync(List<string> lines, CancellationToken token)
        {
            if (lines.Count == 0)
            {
                return;
            }

            var chunkBuilder = new StringBuilder();
            foreach (var line in lines)
            {
                chunkBuilder.Append(line).Append('\n');
            }

            chunkBuilder.Append('\n');
            var chunk = chunkBuilder.ToString();
            responseBuilder.Append(chunk);
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task FlushAnthropicSseBlockAsync(CancellationToken token)
        {
            if (pendingSseLines.Count == 0)
            {
                return;
            }

            if (TryExtractSseEventPayload(pendingSseLines, out var eventName, out var payload))
            {
                if (!string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateAnthropicUsageFromPayload(eventName, payload, ref inputTokens, ref cachedTokens, ref outputTokens, ref receivedMessageStop);
                }
            }

            await WriteRawSseBlockAsync(pendingSseLines, token);
            pendingSseLines.Clear();
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushAnthropicSseBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushAnthropicSseBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = inputTokens;
        result.CachedTokens = cachedTokens;
        result.OutputTokens = outputTokens;

        if (result.Success && !receivedMessageStop)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before message_stop"
                : "stream ended before any complete SSE event";
        }

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        if (result.Success && startedWriting)
        {
            SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
            {
                Status = "success",
                StatusCode = result.StatusCode,
                ResponseBody = DeveloperInvocationTraceStore.FormatBody(result.ResponseBody),
                ResponseContentType = "text/event-stream",
                IsStreaming = true,
                InputTokens = result.InputTokens,
                CachedTokens = result.CachedTokens,
                OutputTokens = result.OutputTokens,
                TotalDurationMs = result.TotalDurationMs
            });
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 把 OpenAI 流式响应转换成 Anthropic 事件流后返回给客户端。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardOpenAiStreamAsAnthropicAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        Guid? traceId,
        Guid traceAttemptId,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        var state = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;

        async Task WriteChunkAsync(string chunk, CancellationToken token)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            responseBuilder.Append(chunk);
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task FlushOpenAiSseBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseDataPayload(pendingSseLines, out var jsonText))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.Equals(jsonText, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                state.ReceivedDoneEvent = true;
                return;
            }

            // 兼容部分 OpenAI 站点在 stream=true 下直接返回完整响应对象，而不是 chunk 流。
            if (!startedWriting && IsOpenAiStreamingResponseEnvelope(jsonText))
            {
                var convertedResponse = ProxyProtocolBridge.BuildAnthropicStreamFromOpenAiResponse(jsonText, modelName, 0, 0, 0);
                if (!string.IsNullOrEmpty(convertedResponse))
                {
                    state.ReceivedDoneEvent = true;
                    await WriteChunkAsync(convertedResponse, token);
                    return;
                }
            }

            if (!startedWriting)
            {
                await WriteChunkAsync(ProxyProtocolBridge.BuildAnthropicStreamStart(modelName, state), token);
            }

            var convertedChunk = ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(jsonText, state);
            await WriteChunkAsync(convertedChunk, token);
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushOpenAiSseBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushOpenAiSseBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;

        if (result.Success)
        {
            if (!state.ReceivedDoneEvent)
            {
                result.IsStreamInterrupted = state.HadAnyContent;
                result.ErrorMessage ??= state.HadAnyContent ? "stream interrupted before DONE" : result.ErrorMessage;
            }

            if (startedWriting)
            {
                var closingChunk = ProxyProtocolBridge.CompleteAnthropicStream(state);
                await WriteChunkAsync(closingChunk, cancellationToken);
                result.ResponseBody = responseBuilder.ToString();
            }

            result.InputTokens = state.InputTokens;
            result.CachedTokens = state.CachedTokens;
            result.OutputTokens = state.OutputTokens;

            if (startedWriting)
            {
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "success",
                    StatusCode = result.StatusCode,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(result.ResponseBody),
                    ResponseContentType = "text/event-stream",
                    IsStreaming = true,
                    InputTokens = result.InputTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.OutputTokens,
                    TotalDurationMs = result.TotalDurationMs
                });
            }

            return new StreamForwardOutcome
            {
                Result = result,
                CanFallback = false
            };
        }

        if (startedWriting)
        {
            result.ResponseBody = responseBuilder.ToString();
            var closingChunk = ProxyProtocolBridge.CompleteAnthropicStream(state);
            await WriteChunkAsync(closingChunk, cancellationToken);
            result.ResponseBody = responseBuilder.ToString();
            result.InputTokens = state.InputTokens;
            result.CachedTokens = state.CachedTokens;
            result.OutputTokens = state.OutputTokens;
            result.IsStreamInterrupted = true;

            return new StreamForwardOutcome
            {
                Result = result,
                CanFallback = false
            };
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = true
        };
    }

    /// <summary>
    /// 根据显式来源标记和 User-Agent 推断请求来源。
    /// </summary>
    private static string ResolveRequestSource(HttpRequest request)
    {
        var explicitSource = request.Headers.TryGetValue("X-AITool-Source", out var sourceHeader)
            ? sourceHeader.ToString().Trim()
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(explicitSource))
        {
            return explicitSource;
        }

        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "proxy";
        }

        var normalizedUserAgent = userAgent.ToLowerInvariant();
        if (normalizedUserAgent.Contains("claude"))
        {
            return "claude-code";
        }

        if (normalizedUserAgent.Contains("codex"))
        {
            return "codex";
        }

        if (normalizedUserAgent.Contains("open-code") || normalizedUserAgent.Contains("opencode"))
        {
            return "open-code";
        }

        return "proxy";
    }

    /// <summary>
    /// 判断当前负载是否是以完整响应对象返回的 OpenAI 流式包裹体。
    /// </summary>
    private static bool IsOpenAiStreamingResponseEnvelope(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            return root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从一组 SSE 行中提取合并后的 data 负载。
    /// </summary>
    private static bool TryExtractSseDataPayload(List<string> sseLines, out string payload)
    {
        payload = string.Empty;
        if (sseLines.Count == 0)
        {
            return false;
        }

        var dataLines = new List<string>();
        foreach (var line in sseLines)
        {
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = line.Length > 5 ? line[5..] : string.Empty;
                if (data.StartsWith(' '))
                {
                    data = data[1..];
                }

                dataLines.Add(data);
            }
        }

        if (dataLines.Count == 0)
        {
            return false;
        }

        payload = string.Join("\n", dataLines);
        return true;
    }

    /// <summary>
    /// 从一组 Anthropic SSE 行中提取事件名和 data 负载。
    /// </summary>
    private static bool TryExtractSseEventPayload(List<string> sseLines, out string eventName, out string payload)
    {
        eventName = string.Empty;
        payload = string.Empty;
        if (sseLines.Count == 0)
        {
            return false;
        }

        var dataLines = new List<string>();
        foreach (var line in sseLines)
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line.Length > 6 ? line[6..].Trim() : string.Empty;
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line.Length > 5 ? line[5..] : string.Empty;
            if (data.StartsWith(' '))
            {
                data = data[1..];
            }

            dataLines.Add(data);
        }

        if (dataLines.Count == 0)
        {
            return false;
        }

        payload = string.Join("\n", dataLines);
        return true;
    }

    /// <summary>
    /// 从 Anthropic 事件负载中刷新当前流的 token 统计。
    /// </summary>
    private static void UpdateAnthropicUsageFromPayload(
        string eventName,
        string payload,
        ref int inputTokens,
        ref int cachedTokens,
        ref int outputTokens,
        ref bool receivedMessageStop)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("usage", out var startUsage))
            {
                if (startUsage.TryGetProperty("input_tokens", out var startInput) && startInput.ValueKind == JsonValueKind.Number)
                {
                    inputTokens = startInput.GetInt32();
                }

                if (startUsage.TryGetProperty("cache_read_input_tokens", out var startCached) && startCached.ValueKind == JsonValueKind.Number)
                {
                    cachedTokens = startCached.GetInt32();
                }

                if (startUsage.TryGetProperty("output_tokens", out var startOutput) && startOutput.ValueKind == JsonValueKind.Number)
                {
                    outputTokens = startOutput.GetInt32();
                }
            }

            if (string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("usage", out var deltaUsage))
                {
                    if (deltaUsage.TryGetProperty("input_tokens", out var deltaInput) && deltaInput.ValueKind == JsonValueKind.Number)
                    {
                        inputTokens = deltaInput.GetInt32();
                    }

                    if (deltaUsage.TryGetProperty("cache_read_input_tokens", out var deltaCached) && deltaCached.ValueKind == JsonValueKind.Number)
                    {
                        cachedTokens = deltaCached.GetInt32();
                    }

                    if (deltaUsage.TryGetProperty("output_tokens", out var deltaOutput) && deltaOutput.ValueKind == JsonValueKind.Number)
                    {
                        outputTokens = deltaOutput.GetInt32();
                    }
                }
            }

            if (string.Equals(eventName, "message_stop", StringComparison.OrdinalIgnoreCase))
            {
                receivedMessageStop = true;
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 在开发者追踪开启时创建一次请求级追踪记录。
    /// </summary>
    private Guid? TryCreateDeveloperTrace(CachedProxyRuntimeSettings runtimeSettings, string requestSource, string protocolType, string modelName, string requestBody)
    {
        if (!runtimeSettings.DeveloperFeaturesEnabled)
        {
            return null;
        }

        return _traceStore.AddRequest(new DeveloperInvocationTraceRequest
        {
            RequestId = Guid.NewGuid(),
            Source = requestSource,
            UserAgent = Request.Headers.UserAgent.ToString(),
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            ProtocolType = protocolType,
            RequestPath = Request.Path,
            RequestModel = modelName,
            RequestBody = DeveloperInvocationTraceStore.FormatBody(requestBody),
            RequestHeaders = DeveloperInvocationTraceStore.CaptureHeaders(Request.Headers)
        });
    }

    /// <summary>
    /// 安全地创建开发者追踪，避免追踪失败影响正常代理。
    /// </summary>
    private Guid? TryCreateDeveloperTraceSafely(CachedProxyRuntimeSettings runtimeSettings, string requestSource, string protocolType, string modelName, string requestBody)
    {
        try
        {
            return TryCreateDeveloperTrace(runtimeSettings, requestSource, protocolType, modelName, requestBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "创建开发者调用追踪失败，但请求继续转发。Protocol={Protocol}, RequestModel={RequestModel}",
                protocolType,
                modelName);
            return null;
        }
    }

    /// <summary>
    /// 为当前追踪追加一次路由尝试记录。
    /// </summary>
    private Guid AddDeveloperTraceAttempt(Guid? traceId, CachedProxyRouteTarget route, string actualProtocolType)
    {
        if (!traceId.HasValue)
        {
            return Guid.Empty;
        }

        return _traceStore.AddAttempt(traceId.Value, new DeveloperInvocationAttempt
        {
            AttemptedModel = route.UpstreamModelName,
            UpstreamProtocolType = actualProtocolType,
            ForwardingMode = ResolveForwardingMode("Anthropic", actualProtocolType),
            TargetSiteId = route.SiteId,
            TargetSiteName = route.SiteName
        });
    }

    /// <summary>
    /// 安全地记录一次路由尝试，避免追踪异常中断主流程。
    /// </summary>
    private Guid AddDeveloperTraceAttemptSafely(Guid? traceId, CachedProxyRouteTarget route, string actualProtocolType)
    {
        try
        {
            return AddDeveloperTraceAttempt(traceId, route, actualProtocolType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "创建开发者调用追踪尝试失败，但请求继续转发。RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                route.ExternalModelName,
                route.UpstreamModelName);
            return Guid.Empty;
        }
    }

    /// <summary>
    /// 安全地写入用量日志，记录失败时不影响响应返回。
    /// </summary>
    private async Task SafeLogUsageAsync(UsageLogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _usageLogService.LogAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "记录使用日志失败，但请求继续返回。Protocol={Protocol}, RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                entry.ProtocolType,
                entry.RequestModel,
                entry.AttemptedModel);
        }
    }

    /// <summary>
    /// 安全地读取路由熔断状态。
    /// </summary>
    private bool IsRouteBlockedSafely(Guid routeId)
    {
        try
        {
            return _circuitStore.IsBlocked(routeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "读取熔断状态失败，按未熔断继续转发。RouteId={RouteId}",
                routeId);
            return false;
        }
    }

    /// <summary>
    /// 安全地标记路由调用成功。
    /// </summary>
    private void SafeSucceedRoute(Guid routeId)
    {
        try
        {
            _circuitStore.Succeed(routeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "更新路由成功状态失败，但请求继续返回。RouteId={RouteId}",
                routeId);
        }
    }

    /// <summary>
    /// 安全地累计路由失败状态。
    /// </summary>
    private void SafeBlockRoute(Guid routeId)
    {
        try
        {
            _circuitStore.Block(routeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "更新路由失败状态失败，但继续尝试后续路由。RouteId={RouteId}",
                routeId);
        }
    }

    /// <summary>
    /// 安全地补全一次开发者追踪尝试记录。
    /// </summary>
    private void SafeCompleteDeveloperTraceAttempt(Guid? traceId, Guid traceAttemptId, DeveloperInvocationResult result)
    {
        try
        {
            CompleteDeveloperTraceAttempt(traceId, traceAttemptId, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "完成开发者调用追踪失败，但请求继续返回。TraceId={TraceId}, AttemptId={AttemptId}",
                traceId,
                traceAttemptId);
        }
    }

    /// <summary>
    /// 安全地记录失败的代理请求明细。
    /// </summary>
    private void SafeLogFailedProxyAttempt(
        string requestSource,
        string modelName,
        CachedProxyRouteTarget route,
        string actualProtocolType,
        string preparedRequestBody,
        ProxyForwardResult result)
    {
        try
        {
            LogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "记录失败代理日志失败，但继续后续流程。RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                modelName,
                route.UpstreamModelName);
        }
    }

    /// <summary>
    /// 安全地输出控制台代理摘要日志。
    /// </summary>
    private void SafeWriteConsoleProxyLog(
        string clientProtocol,
        string requestSource,
        string modelName,
        string actualProtocolType,
        string preparedRequestBody,
        ProxyForwardResult result,
        int requestBodyLength)
    {
        try
        {
            Console.WriteLine(ConsoleProxyLogFormatter.BuildSummary(
                clientProtocol,
                requestSource,
                modelName,
                actualProtocolType,
                result.StatusCode,
                result.Success,
                result.IsStreaming,
                result.IsStreamInterrupted,
                result.TotalDurationMs,
                requestBodyLength,
                result.ResponseBody?.Length ?? 0));
        }
        catch
        {
        }
    }

    /// <summary>
    /// 将一次路由尝试的结果写回开发者追踪。
    /// </summary>
    private void CompleteDeveloperTraceAttempt(Guid? traceId, Guid traceAttemptId, DeveloperInvocationResult result)
    {
        if (!traceId.HasValue || traceAttemptId == Guid.Empty)
        {
            return;
        }

        _traceStore.CompleteAttempt(traceId.Value, traceAttemptId, result);
    }

    /// <summary>
    /// 输出一次失败代理尝试的完整上下文日志。
    /// </summary>
    private void LogFailedProxyAttempt(
        string requestSource,
        string modelName,
        CachedProxyRouteTarget route,
        string actualProtocolType,
        string preparedRequestBody,
        ProxyForwardResult result)
    {
        _logger.LogError(
            "代理请求失败\nSource={Source}\nClientProtocol={ClientProtocol}\nUpstreamProtocol={UpstreamProtocol}\nRequestModel={RequestModel}\nAttemptedModel={AttemptedModel}\nSiteName={SiteName}\nBaseUrl={BaseUrl}\nStatusCode={StatusCode}\nIsStreaming={IsStreaming}\nIsStreamInterrupted={IsStreamInterrupted}\nErrorMessage={ErrorMessage}\nRequestBody={RequestBody}\nResponseBody={ResponseBody}",
            requestSource,
            "Anthropic",
            actualProtocolType,
            modelName,
            route.UpstreamModelName,
            route.SiteName,
            route.BaseUrl,
            result.StatusCode,
            result.IsStreaming,
            result.IsStreamInterrupted,
            result.ErrorMessage ?? string.Empty,
            HttpLogFormatter.FormatBody(preparedRequestBody),
            HttpLogFormatter.FormatBody(result.ResponseBody));
    }

    /// <summary>
    /// 根据客户端协议和上游协议判断当前是直连还是兼容转发。
    /// </summary>
    private static string ResolveForwardingMode(string clientProtocolType, string upstreamProtocolType)
    {
        return string.Equals(clientProtocolType, upstreamProtocolType, StringComparison.OrdinalIgnoreCase)
            ? "direct"
            : "bridge";
    }

    /// <summary>
    /// 从代理请求体中提取思考等级，兼容不同客户端协议的字段命名。
    /// </summary>
    private static string ResolveReasoningEffort(JsonElement rootElement)
    {
        if (TryGetNormalizedString(rootElement, "reasoning_effort", out var directEffort))
        {
            return directEffort;
        }

        if (TryGetNormalizedString(rootElement, "effort", out var effort))
        {
            return effort;
        }

        if (rootElement.TryGetProperty("reasoning", out var reasoningElement) &&
            reasoningElement.ValueKind == JsonValueKind.Object &&
            TryGetNormalizedString(reasoningElement, "effort", out var nestedEffort))
        {
            return nestedEffort;
        }

        if (rootElement.TryGetProperty("thinking", out var thinkingElement) &&
            thinkingElement.ValueKind == JsonValueKind.Object &&
            thinkingElement.TryGetProperty("budget_tokens", out var budgetTokensElement) &&
            budgetTokensElement.TryGetInt32(out var budgetTokens))
        {
            return budgetTokens switch
            {
                <= 1280 => "low",
                <= 2048 => "medium",
                _ => "high"
            };
        }

        return string.Empty;
    }

    /// <summary>
    /// 读取并规范化请求体中的字符串字段。
    /// </summary>
    private static bool TryGetNormalizedString(JsonElement rootElement, string propertyName, out string value)
    {
        value = string.Empty;
        if (!rootElement.TryGetProperty(propertyName, out var propertyElement) || propertyElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var rawValue = propertyElement.GetString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        value = rawValue;
        return true;
    }

    /// <summary>
    /// 从请求头中提取并校验代理访问密钥。
    /// </summary>
    private async Task<CachedProxyAccessKey?> ValidateAccessKeyAsync(CancellationToken cancellationToken)
    {
        var accessToken = Request.Headers.TryGetValue("x-api-key", out var keyHeader)
            ? keyHeader.ToString()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            var authHeader = Request.Headers.Authorization.ToString();
            accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader[7..]
                : string.Empty;
        }

        return await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
    }

    /// <summary>
    /// 收集需要继续透传给 Anthropic 上游的协议相关请求头。
    /// </summary>
    private static Dictionary<string, string> CollectAnthropicForwardHeaders(HttpRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var headerName in new[] { "anthropic-version", "anthropic-beta" })
        {
            if (request.Headers.TryGetValue(headerName, out var headerValue) && !string.IsNullOrWhiteSpace(headerValue))
            {
                headers[headerName] = headerValue.ToString();
            }
        }

        return headers;
    }

    /// <summary>
    /// 根据请求中的文本内容粗略估算输入 token 数量。
    /// </summary>
    private static int EstimateInputTokens(JsonElement root)
    {
        var builder = new StringBuilder();
        if (root.TryGetProperty("system", out var system))
        {
            builder.Append(FlattenText(system)).Append(' ');
        }

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("content", out var content))
                {
                    builder.Append(FlattenText(content)).Append(' ');
                }
            }
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4d));
    }

    /// <summary>
    /// 将不同形态的消息内容展开为纯文本。
    /// </summary>
    private static string FlattenText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(" ", element.EnumerateArray().Select(FlattenText).Where(x => !string.IsNullOrWhiteSpace(x))),
            JsonValueKind.Object => FlattenObjectText(element),
            _ => string.Empty
        };
    }

    /// <summary>
    /// 优先提取对象中的文本字段，并回退到递归拼接所有子字段。
    /// </summary>
    private static string FlattenObjectText(JsonElement element)
    {
        foreach (var propertyName in new[] { "text", "thinking", "content" })
        {
            if (element.TryGetProperty(propertyName, out var value))
            {
                var text = FlattenText(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Join(" ", element.EnumerateObject().Select(x => FlattenText(x.Value)).Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
