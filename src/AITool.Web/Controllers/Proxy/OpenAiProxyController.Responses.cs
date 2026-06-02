using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Conversations;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

/// <summary>
/// 承载 OpenAI Responses HTTP 与 WebSocket 入口的代理处理逻辑。
/// </summary>
public sealed partial class OpenAiProxyController
{
    /// <summary>
    /// 处理 OpenAI Responses WebSocket 请求，复用现有路由选择、熔断、兼容桥接和日志链路。
    /// </summary>
    [HttpGet("/v1/responses")]
    public async Task<IActionResult> ResponsesWebSocket(CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return BadRequest(new { error = new { message = "WebSocket request required" } });
        }

        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;
        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var requestSource = ResolveRequestSource(Request);
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var sessionState = new ResponsesWebSocketSessionState();

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var rawRequest = await ReceiveWebSocketTextMessageAsync(webSocket, cancellationToken);
            if (rawRequest is null)
            {
                break;
            }

            if (!TryNormalizeResponsesWebSocketRequest(rawRequest, sessionState.LastRequestJson, sessionState.LastResponseOutputJson, out var normalizedRequest, out var errorMessage))
            {
                await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status400BadRequest, errorMessage ?? "Invalid websocket request", cancellationToken);
                continue;
            }

            var turnCompleted = await ProcessResponsesWebSocketTurnAsync(
                webSocket,
                accessKey.Id,
                requestSource,
                runtimeSettings,
                rawRequest,
                normalizedRequest!,
                sessionState,
                cancellationToken);

            if (!turnCompleted)
            {
                continue;
            }
        }

        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session closed", cancellationToken);
            }
            catch
            {
            }
        }

        return new EmptyResult();
    }

    /// <summary>
    /// 处理 OpenAI Responses API 请求，按路由配置转发到可用上游。
    /// </summary>
    [HttpPost("/v1/responses")]
    public async Task<IActionResult> Responses(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        var modelName = ProxyProtocolBridge.ExtractResponsesModel(requestBody);
        var enableStreaming = ProxyProtocolBridge.ExtractResponsesStream(requestBody);
        var reasoningEffort = ProxyProtocolBridge.ExtractResponsesReasoningEffort(requestBody);

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return BadRequest(new { error = new { message = "Invalid request body: model is required" } });
        }

        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        var requestSource = ResolveRequestSource(Request);
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "Responses", modelName, requestBody);

        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);
        if (allRoutes.Count == 0)
        {
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });
        }

        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            if (IsRouteBlockedSafely(route.RouteId))
                continue;

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("OpenAI");

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken);

            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            // Responses 端点的转发逻辑：
            // - 上游 OpenAI：直接透传原始 Responses 请求体，响应也直接透传
            // - 上游 Anthropic：先将 Responses 转为 Chat Completions，再走兼容中转
            var isPassthrough = string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase);
            string preparedRequestBody;

            if (isPassthrough)
            {
                preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("OpenAI", "OpenAI", requestBody, route.SiteModelName, enableStreaming);
            }
            else
            {
                // Responses → Chat Completions → Anthropic：先转为 Chat Completions，再由协议桥接转为目标格式
                var chatBody = ProxyProtocolBridge.ConvertResponsesRequestToChat(requestBody, route.SiteModelName, enableStreaming);
                preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("OpenAI", actualProtocolType, chatBody, route.SiteModelName, enableStreaming);
            }

            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType);

            var forwardRequest = new ProxyForwardRequest
            {
                TargetBaseUrl = route.BaseUrl,
                TargetEndpointPathMode = route.EndpointPathMode,
                TargetApiKey = route.ApiKey,
                ProtocolType = actualProtocolType,
                TargetModelName = route.SiteModelName,
                RequestBody = requestBody,
                PreparedRequestBody = preparedRequestBody,
                EnableStreaming = enableStreaming,
                RequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                TargetPath = isPassthrough ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses") : null
            };

            if (enableStreaming)
            {
                StreamForwardOutcome streamOutcome;
                if (isPassthrough)
                {
                    // OpenAI 上游直接透传
                    streamOutcome = await ForwardOpenAiStreamPassthroughAsync(forwardRequest, cancellationToken);
                }
                else
                {
                    // Anthropic 上游：流式 Anthropic → Responses
                    streamOutcome = await ForwardAnthropicStreamAsResponsesAsync(forwardRequest, modelName, cancellationToken);
                }

                var streamResult = streamOutcome.Result;
                if (streamResult.IsCanceled)
                {
                    return new EmptyResult();
                }

                SafeWriteConsoleProxyLog("Responses", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);

                await SafeLogUsageAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "OpenAI",
                    ForwardingMode = isPassthrough ? "direct" : "bridge",
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
                    IsStreaming = true,
                    IsStreamInterrupted = streamResult.IsStreamInterrupted,
                    FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                    StreamDurationMs = streamResult.StreamDurationMs,
                    TotalDurationMs = streamResult.TotalDurationMs,
                    ReasoningEffort = reasoningEffort
                }, CancellationToken.None);

                if (streamResult.Success)
                {
                    await SafeLogConversationAsync(requestId, accessKey.Id, "OpenAI", requestSource, requestBody, streamResult.ResponseBody, modelName, true, "success", streamResult.InputTokens, streamResult.CachedTokens, streamResult.OutputTokens, DateTimeOffset.UtcNow.AddMilliseconds(-Math.Max(0, streamResult.TotalDurationMs)), CancellationToken.None);
                    SafeSucceedRoute(route.RouteId);
                    SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                    {
                        Status = "success",
                        StatusCode = streamResult.StatusCode,
                        ResponseBody = DeveloperInvocationTraceStore.FormatBody(streamResult.ResponseBody),
                        ResponseContentType = "text/event-stream",
                        IsStreaming = true,
                        InputTokens = streamResult.InputTokens,
                        CachedTokens = streamResult.CachedTokens,
                        OutputTokens = streamResult.OutputTokens,
                        TotalDurationMs = streamResult.TotalDurationMs
                    });
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

            // 非流式：仍按单路由超时控制；但若客户端主动取消，则直接结束，不再继续回退后续候选。
            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
            if (result.IsCanceled)
            {
                return new EmptyResult();
            }

            SafeWriteConsoleProxyLog("Responses", requestSource, modelName, actualProtocolType, preparedRequestBody, result, requestBody.Length);

            await SafeLogUsageAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKey.Id,
                ProtocolType = "OpenAI",
                ForwardingMode = isPassthrough ? "direct" : "bridge",
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
                SafeSucceedRoute(route.RouteId);
                var responseContentType = result.IsStreaming ? "text/event-stream" : "application/json";

                if (isPassthrough)
                {
                    // OpenAI 上游直接透传
                    await SafeLogConversationAsync(requestId, accessKey.Id, "OpenAI", requestSource, requestBody, result.ResponseBody, modelName, result.IsStreaming, "success", result.InputTokens, result.CachedTokens, result.OutputTokens, DateTimeOffset.UtcNow.AddMilliseconds(-Math.Max(0, result.TotalDurationMs)), cancellationToken);
                    SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                    {
                        Status = "success",
                        StatusCode = result.StatusCode,
                        ResponseBody = DeveloperInvocationTraceStore.FormatBody(result.ResponseBody),
                        ResponseContentType = responseContentType,
                        IsStreaming = result.IsStreaming,
                        InputTokens = result.InputTokens,
                        CachedTokens = result.CachedTokens,
                        OutputTokens = result.OutputTokens,
                        TotalDurationMs = result.TotalDurationMs
                    });
                    return Content(result.ResponseBody, responseContentType);
                }

                // Anthropic 上游：将 Chat Completions 响应转为 Responses 格式
                var chatResponseBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI", actualProtocolType, result.ResponseBody,
                    result.IsStreaming, modelName,
                    result.InputTokens, result.CachedTokens, result.OutputTokens);
                var responsesBody = ProxyProtocolBridge.ConvertChatResponseToResponses(chatResponseBody);
                await SafeLogConversationAsync(requestId, accessKey.Id, "OpenAI", requestSource, requestBody, responsesBody, modelName, false, "success", result.InputTokens, result.CachedTokens, result.OutputTokens, DateTimeOffset.UtcNow.AddMilliseconds(-Math.Max(0, result.TotalDurationMs)), cancellationToken);
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "success",
                    StatusCode = result.StatusCode,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(responsesBody),
                    ResponseContentType = "application/json",
                    IsStreaming = false,
                    InputTokens = result.InputTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.OutputTokens,
                    TotalDurationMs = result.TotalDurationMs
                });
                return Content(responsesBody, "application/json");
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
            SafeBlockRoute(route.RouteId);
            lastResult = result;
        }

        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }

    /// <summary>
    /// 处理单轮 Responses WebSocket 请求，沿用现有路由选择、熔断和协议桥接能力。
    /// </summary>
    private async Task<bool> ProcessResponsesWebSocketTurnAsync(
        WebSocket webSocket,
        Guid accessKeyId,
        string requestSource,
        CachedProxyRuntimeSettings runtimeSettings,
        string rawRequestBody,
        string normalizedRequestBody,
        ResponsesWebSocketSessionState sessionState,
        CancellationToken cancellationToken)
    {
        var modelName = ProxyProtocolBridge.ExtractResponsesModel(normalizedRequestBody);
        var enableStreaming = ProxyProtocolBridge.ExtractResponsesStream(normalizedRequestBody);
        var reasoningEffort = ProxyProtocolBridge.ExtractResponsesReasoningEffort(normalizedRequestBody);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status400BadRequest, "Invalid request body: model is required", cancellationToken);
            return false;
        }

        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "ResponsesWebSocket", modelName, rawRequestBody);
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);
        if (allRoutes.Count == 0)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status404NotFound, $"No available route for model: {modelName}", cancellationToken);
            return false;
        }

        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            if (IsRouteBlockedSafely(route.RouteId))
                continue;

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("OpenAI");

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken);
            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            var isPassthrough = string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase);
            var preparedRequestBody = isPassthrough
                ? ProxyProtocolBridge.PrepareRequestBody("OpenAI", "OpenAI", normalizedRequestBody, route.SiteModelName, true)
                : ProxyProtocolBridge.PrepareRequestBody(
                    "OpenAI",
                    actualProtocolType,
                    ProxyProtocolBridge.ConvertResponsesRequestToChat(normalizedRequestBody, route.SiteModelName, true),
                    route.SiteModelName,
                    true);

            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType);
            var forwardRequest = new ProxyForwardRequest
            {
                TargetBaseUrl = route.BaseUrl,
                TargetEndpointPathMode = route.EndpointPathMode,
                TargetApiKey = route.ApiKey,
                ProtocolType = actualProtocolType,
                TargetModelName = route.SiteModelName,
                RequestBody = rawRequestBody,
                PreparedRequestBody = preparedRequestBody,
                EnableStreaming = enableStreaming,
                RequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                TargetPath = isPassthrough ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses") : null
            };

            var streamOutcome = isPassthrough
                ? await ForwardOpenAiResponsesAsWebSocketAsync(webSocket, forwardRequest, cancellationToken)
                : await ForwardAnthropicResponsesAsWebSocketAsync(webSocket, forwardRequest, modelName, cancellationToken);
            var streamResult = streamOutcome.Result;
            SafeWriteConsoleProxyLog("ResponsesWebSocket", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, rawRequestBody.Length);

            await SafeLogUsageAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKeyId,
                ProtocolType = "OpenAI",
                ForwardingMode = isPassthrough ? "direct" : "bridge",
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
                IsStreaming = true,
                IsStreamInterrupted = streamResult.IsStreamInterrupted,
                FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                StreamDurationMs = streamResult.StreamDurationMs,
                TotalDurationMs = streamResult.TotalDurationMs,
                ReasoningEffort = reasoningEffort
            }, CancellationToken.None);

            if (streamResult.Success)
            {
                SafeSucceedRoute(route.RouteId);
                sessionState.LastRequestJson = normalizedRequestBody;
                sessionState.LastResponseOutputJson = string.IsNullOrWhiteSpace(streamOutcome.CompletedOutputJson)
                    ? "[]"
                    : streamOutcome.CompletedOutputJson;
                await SafeLogConversationAsync(requestId, accessKeyId, "OpenAI", requestSource, rawRequestBody, streamResult.ResponseBody, modelName, true, "success", streamResult.InputTokens, streamResult.CachedTokens, streamResult.OutputTokens, DateTimeOffset.UtcNow.AddMilliseconds(-Math.Max(0, streamResult.TotalDurationMs)), CancellationToken.None);
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "success",
                    StatusCode = streamResult.StatusCode,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(streamResult.ResponseBody),
                    ResponseContentType = "application/websocket+json",
                    IsStreaming = true,
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    TotalDurationMs = streamResult.TotalDurationMs
                });
                return true;
            }

            SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
            {
                Status = "fail",
                StatusCode = streamResult.StatusCode,
                ErrorMessage = streamResult.ErrorMessage ?? string.Empty,
                ResponseBody = DeveloperInvocationTraceStore.FormatBody(streamResult.ResponseBody),
                ResponseContentType = "application/websocket+json",
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
                if (webSocket.State == WebSocketState.Open)
                {
                    await WriteResponsesWebSocketErrorAsync(webSocket, streamResult.StatusCode > 0 ? streamResult.StatusCode : StatusCodes.Status502BadGateway, streamResult.ErrorMessage ?? "All upstream routes failed", cancellationToken);
                }
                return false;
            }
        }

        if (webSocket.State == WebSocketState.Open)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, lastResult?.StatusCode > 0 ? lastResult.StatusCode : StatusCodes.Status502BadGateway, lastResult?.ErrorMessage ?? "All upstream routes failed", cancellationToken);
        }
        return false;
    }

}
