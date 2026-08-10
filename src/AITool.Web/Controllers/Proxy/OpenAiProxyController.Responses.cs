using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
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
            return BadRequest(new { error = new { message = "该接口需要 WebSocket 连接，请使用 WebSocket 客户端访问", type = "invalid_request_error", code = "websocket_required" } });
        }

        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;
        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "访问密钥无效或缺失，请在请求头中携带有效的 Authorization Bearer 令牌", type = "authentication_error", code = "invalid_access_key" } });
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
                await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status400BadRequest, errorMessage ?? "WebSocket 请求无效，请检查请求格式", cancellationToken);
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
            return BadRequest(new { error = new { message = "请求体缺少 model 字段，请指定要调用的模型名称", type = "invalid_request_error", code = "model_required" } });
        }

        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "访问密钥无效或缺失，请在请求头中携带有效的 Authorization Bearer 令牌", type = "authentication_error", code = "invalid_access_key" } });
        }

        var requestSource = ResolveRequestSource(Request);
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "Responses", modelName, requestBody);

        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        // AccessKey 路由限定（同 ProcessOpenAiLikeRequestAsync）。
        var allowedRoutes = ProxyRequestMetadataCache.GetAllowedRouteNames(accessKey);
        if (allowedRoutes is not null && allRoutes.Count > 0)
        {
            allRoutes = allRoutes.Where(r => allowedRoutes.Contains(r.ExternalModelName)).ToList();
            if (allRoutes.Count == 0)
            {
                return StatusCode(403, new { error = new { message = $"当前访问密钥无权访问路由: {modelName}", type = "permission_error", code = "route_forbidden" } });
            }
        }

        if (allRoutes.Count == 0)
        {
            return StatusCode(403, new { error = new { message = $"模型 '{modelName}' 没有可用的路由，请检查路由配置或联系管理员", type = "invalid_request_error", code = "no_available_route" } });
        }

        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var routeIndex = -1;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            routeIndex++;
            if (IsRouteBlockedSafely(route.CircuitKey))
                continue;

            attemptIndex++;
            // Responses 客户端优先选择上游原生 Responses；不支持时再降级为 OpenAI/Anthropic 协议转换。
            var actualProtocolType = route.ResolveProtocolForClient("Responses");

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteKeyId ?? route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken, displaySiteId: route.SiteId);

            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            // Responses 端点的转发逻辑：
            // - 原生 Responses 站点：直接透传 Responses 请求体，URL 指向 /responses
            // - OpenAI Chat 站点：先转换为 Chat Completions，URL 指向 /chat/completions
            // - Anthropic 站点：先转换为 Chat Completions，再由协议桥接转为 Anthropic 请求
            // 不能把 actualProtocolType=OpenAI 当作 Responses 透传，否则普通 OpenAI Chat 上游会收到
            // 不支持的 /responses 请求，常见表现为 HTTP 406。
            var isPassthrough = string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase);
            string preparedRequestBody;

            if (isPassthrough)
            {
                preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("Responses", "Responses", requestBody, route.SiteModelName, enableStreaming, route.OverrideReasoningEffort, route.BaseUrl, route.CompatibilityRules, isPassthrough: true);
            }
            else
            {
                // Responses → Chat Completions → OpenAI/Anthropic：先转为 Chat Completions，再由协议桥接转发。
                var chatBody = ProxyProtocolBridge.ConvertResponsesRequestToChat(requestBody, route.SiteModelName, enableStreaming);
                preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("OpenAI", actualProtocolType, chatBody, route.SiteModelName, enableStreaming, route.OverrideReasoningEffort, route.BaseUrl, route.CompatibilityRules, isPassthrough: false);
            }

            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType, preparedRequestBody);

            // PrepareRequestBody 已内联覆盖思考等级，同步更新日志变量
            if (!string.IsNullOrWhiteSpace(route.OverrideReasoningEffort))
            {
                reasoningEffort = route.OverrideReasoningEffort;
            }

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
                ForwardHeaders = MergeExtraHeaders(route.ExtraHeaders),
                RefreshTargetApiKeyAsync = CreateCodexCredentialRefreshCallback(route),
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
                else if (string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    // OpenAI Chat Completions 上游：逐个 SSE 块转换为 Responses 事件。
                    var responsesState = new ChatToResponsesStreamState { Model = route.SiteModelName };
                    streamOutcome = await ForwardOpenAiStreamPassthroughAsync(
                        forwardRequest,
                        cancellationToken,
                        chunk => ConvertOpenAiChatSseBlockToResponses(chunk, responsesState));
                }
                else
                {
                    // Anthropic 上游：流式 Anthropic → Responses。
                    streamOutcome = await ForwardAnthropicStreamAsResponsesAsync(forwardRequest, modelName, cancellationToken);
                }

                var streamResult = streamOutcome.Result;
                if (streamResult.IsCanceled)
                {
                    return new EmptyResult();
                }

                SafeWriteConsoleProxyLog("Responses", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);
                var streamCanFallback = !streamResult.Success
                    && streamOutcome.CanFallback
                    && allRoutes.Skip(routeIndex + 1).Any(candidate => !IsRouteBlockedSafely(candidate.CircuitKey));

                await SafeLogUsageAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "Responses",
                    ForwardingMode = isPassthrough ? "direct" : "bridge",
                    RequestModel = modelName,
                    AttemptedModel = route.UpstreamModelName,
                    TargetSiteId = route.SiteId,
                    Status = streamResult.Success ? "success" : "fail",
                    Source = requestSource,
                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                    AttemptIndex = attemptIndex,
                    IsFinalResult = streamResult.Success || !streamCanFallback,
                    FallbackTriggered = streamCanFallback,
                    ErrorMessage = streamResult.Success ? string.Empty : (streamResult.ErrorMessage ?? string.Empty),
                    HttpStatusCode = streamResult.StatusCode > 0 ? streamResult.StatusCode : null,
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
                    SafeSucceedRoute(route.CircuitKey);
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
                SafeBlockRoute(route.CircuitKey);
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
            var canFallback = !result.Success
                && allRoutes.Skip(routeIndex + 1).Any(candidate => !IsRouteBlockedSafely(candidate.CircuitKey));

            await SafeLogUsageAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKey.Id,
                ProtocolType = "Responses",
                ForwardingMode = isPassthrough ? "direct" : "bridge",
                RequestModel = modelName,
                AttemptedModel = route.UpstreamModelName,
                TargetSiteId = route.SiteId,
                Status = result.Success ? "success" : "fail",
                Source = requestSource,
                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,
                AttemptIndex = attemptIndex,
                IsFinalResult = result.Success || !canFallback,
                FallbackTriggered = canFallback,
                ErrorMessage = result.Success ? string.Empty : (result.ErrorMessage ?? string.Empty),
                HttpStatusCode = result.StatusCode > 0 ? result.StatusCode : null,
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
                SafeSucceedRoute(route.CircuitKey);
                var responseContentType = result.IsStreaming ? "text/event-stream" : "application/json";

                if (isPassthrough)
                {
                    // OpenAI 上游直接透传
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
            SafeBlockRoute(route.CircuitKey);
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
            await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status400BadRequest, "请求体缺少 model 字段，请指定要调用的模型名称", cancellationToken);
            return false;
        }

        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "ResponsesWebSocket", modelName, rawRequestBody);
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        // AccessKey 路由限定（同 HTTP 入口）。WebSocket 方法接收的是 accessKeyId，需要从缓存查 accessKey 对象。
        var wsAccessKey = await _metadataCache.GetAccessKeyByIdAsync(accessKeyId, cancellationToken);
        var allowedRoutes = ProxyRequestMetadataCache.GetAllowedRouteNames(wsAccessKey);
        if (allowedRoutes is not null && allRoutes.Count > 0)
        {
            allRoutes = allRoutes.Where(r => allowedRoutes.Contains(r.ExternalModelName)).ToList();
            if (allRoutes.Count == 0)
            {
                await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status403Forbidden, $"当前访问密钥无权访问路由: {modelName}", cancellationToken);
                return false;
            }
        }

        if (allRoutes.Count == 0)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status403Forbidden, $"模型 '{modelName}' 没有可用的路由，请检查路由配置或联系管理员", cancellationToken);
            return false;
        }

        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var routeIndex = -1;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            routeIndex++;
            if (IsRouteBlockedSafely(route.CircuitKey))
                continue;

            attemptIndex++;
            // Responses 客户端优先选择上游原生 Responses；不支持时再降级为 OpenAI/Anthropic 协议转换。
            var actualProtocolType = route.ResolveProtocolForClient("Responses");

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteKeyId ?? route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken, displaySiteId: route.SiteId);
            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            var isPassthrough = string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase);
            var preparedRequestBody = isPassthrough
                ? ProxyProtocolBridge.PrepareRequestBody(
                    "Responses",
                    "Responses",
                    normalizedRequestBody,
                    route.SiteModelName,
                    true,
                    route.OverrideReasoningEffort,
                    route.BaseUrl,
                    route.CompatibilityRules,
                    isPassthrough: true)
                : ProxyProtocolBridge.PrepareRequestBody(
                    "OpenAI",
                    actualProtocolType,
                    ProxyProtocolBridge.ConvertResponsesRequestToChat(normalizedRequestBody, route.SiteModelName, true),
                    route.SiteModelName,
                    true,
                    route.OverrideReasoningEffort,
                    route.BaseUrl,
                    route.CompatibilityRules,
                    isPassthrough: false);

            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType, preparedRequestBody);

            // PrepareRequestBody 已内联覆盖思考等级，同步更新日志变量
            if (!string.IsNullOrWhiteSpace(route.OverrideReasoningEffort))
            {
                reasoningEffort = route.OverrideReasoningEffort;
            }

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
                ForwardHeaders = MergeExtraHeaders(route.ExtraHeaders),
                RefreshTargetApiKeyAsync = CreateCodexCredentialRefreshCallback(route),
                TargetPath = isPassthrough ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses") : null
            };

            StreamForwardOutcome streamOutcome;
            if (isPassthrough)
            {
                streamOutcome = await ForwardOpenAiResponsesAsWebSocketAsync(webSocket, forwardRequest, cancellationToken);
            }
            else if (string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                // OpenAI Chat Completions SSE 逐块转换后再发送 Responses WebSocket 事件。
                var responsesState = new ChatToResponsesStreamState { Model = route.SiteModelName };
                streamOutcome = await ForwardOpenAiResponsesAsWebSocketAsync(
                    webSocket,
                    forwardRequest,
                    cancellationToken,
                    payload => ProxyProtocolBridge.ConvertChatStreamChunkToResponses(payload, responsesState));
            }
            else
            {
                streamOutcome = await ForwardAnthropicResponsesAsWebSocketAsync(webSocket, forwardRequest, modelName, cancellationToken);
            }
            var streamResult = streamOutcome.Result;
            SafeWriteConsoleProxyLog("ResponsesWebSocket", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, rawRequestBody.Length);
            var canFallback = !streamResult.Success
                && streamOutcome.CanFallback
                && allRoutes.Skip(routeIndex + 1).Any(candidate => !IsRouteBlockedSafely(candidate.CircuitKey));

            await SafeLogUsageAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKeyId,
                ProtocolType = "Responses",
                ForwardingMode = isPassthrough ? "direct" : "bridge",
                RequestModel = modelName,
                AttemptedModel = route.UpstreamModelName,
                TargetSiteId = route.SiteId,
                Status = streamResult.Success ? "success" : "fail",
                Source = requestSource,
                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                AttemptIndex = attemptIndex,
                IsFinalResult = streamResult.Success || !canFallback,
                FallbackTriggered = canFallback,
                ErrorMessage = streamResult.Success ? string.Empty : (streamResult.ErrorMessage ?? string.Empty),
                HttpStatusCode = streamResult.StatusCode > 0 ? streamResult.StatusCode : null,
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
                SafeSucceedRoute(route.CircuitKey);
                sessionState.LastRequestJson = normalizedRequestBody;
                sessionState.LastResponseOutputJson = string.IsNullOrWhiteSpace(streamOutcome.CompletedOutputJson)
                    ? "[]"
                    : streamOutcome.CompletedOutputJson;
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
            SafeBlockRoute(route.CircuitKey);
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
