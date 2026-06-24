using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

using AITool.Core.Services;
namespace AITool.Core.Controllers.Proxy;

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

        // 生成本次请求的唯一标识，供 callContext、UsageLog、ConversationLog 共用
        var requestId = Guid.NewGuid();

        // 构建统一调用上下文，整个请求链路共享同一份上下文数据
        var callContext = new ProxyCallContext
        {
            RequestId = requestId,
            AccessKeyId = accessKey.Id,
            ProtocolType = "OpenAI",
            Source = requestSource,
            RequestModel = modelName,
            ReasoningEffort = reasoningEffort,
            IsStreaming = enableStreaming,
            RequestBody = requestBody,
            RequestPath = "/v1/responses",
            RequestedAt = DateTimeOffset.UtcNow,
            UserAgent = Request.Headers.UserAgent.ToString(),
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            RequestHeaders = DeveloperInvocationTraceStore.CaptureHeaders(Request.Headers)
        };

        // 始终创建调用追踪记录，用于事件发布（UsageLog 等依赖 trace 完成时触发）。
        // DeveloperFeaturesEnabled 仅控制 Invocations 页面是否可见，不影响数据采集和推送。
        var traceId = _proxyCallRecorder.BeginTrace(callContext);

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
        var attemptIndex = 0;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        try
        {
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

            // 更新统一上下文中的本次尝试级字段
            callContext.AttemptIndex = attemptIndex;
            callContext.AttemptedModel = route.UpstreamModelName;
            callContext.UpstreamProtocolType = actualProtocolType;
            callContext.ForwardingMode = isPassthrough ? "direct" : "bridge";
            callContext.TargetSiteId = route.SiteId;
            callContext.TargetSiteName = route.SiteName;
            callContext.RouteId = route.RouteId;

            var traceAttemptId = _proxyCallRecorder.BeginTraceAttempt(traceId, callContext);

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

                // 更新统一上下文中的结果字段并写入用量日志
                callContext.Success = streamResult.Success;
                callContext.StatusCode = streamResult.StatusCode;
                callContext.ErrorMessage = streamResult.ErrorMessage ?? string.Empty;
                callContext.ResponseBody = streamResult.ResponseBody;
                callContext.InputTokens = streamResult.InputTokens;
                callContext.CachedTokens = streamResult.CachedTokens;
                callContext.OutputTokens = streamResult.OutputTokens;
                callContext.IsStreaming = true;
                callContext.IsStreamInterrupted = streamResult.IsStreamInterrupted;
                callContext.FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs;
                callContext.StreamDurationMs = streamResult.StreamDurationMs;
                callContext.TotalDurationMs = streamResult.TotalDurationMs;
                callContext.RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex;
                callContext.IsFinalResult = streamResult.Success;
                callContext.FallbackTriggered = !streamResult.Success;
                await _proxyCallRecorder.RecordUsageAsync(callContext, CancellationToken.None);

                if (streamResult.Success)
                {
                    // 流式成功：记录对话并完成追踪
                    callContext.ResponseContentType = "text/event-stream";
                    await _proxyCallRecorder.RecordConversationAsync(callContext, CancellationToken.None);
                    SafeSucceedRoute(route.RouteId);
                    _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                    return new EmptyResult();
                }

                // 流式失败：完成追踪
                callContext.ResponseContentType = "text/event-stream";
                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, streamResult);
                if (!streamResult.HasStartedStreaming)
                {
                    SafeBlockRoute(route.RouteId);
                }
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

            // 更新统一上下文中的结果字段并写入用量日志
            callContext.Success = result.Success;
            callContext.StatusCode = result.StatusCode;
            callContext.ErrorMessage = result.ErrorMessage ?? string.Empty;
            callContext.ResponseBody = result.ResponseBody;
            callContext.InputTokens = result.InputTokens;
            callContext.CachedTokens = result.CachedTokens;
            callContext.OutputTokens = result.OutputTokens;
            callContext.IsStreaming = result.IsStreaming;
            callContext.IsStreamInterrupted = result.IsStreamInterrupted;
            callContext.FirstTokenLatencyMs = result.FirstTokenLatencyMs;
            callContext.StreamDurationMs = result.StreamDurationMs;
            callContext.TotalDurationMs = result.TotalDurationMs;
            callContext.RetryCount = result.Success ? attemptIndex - 1 : attemptIndex;
            callContext.IsFinalResult = result.Success;
            callContext.FallbackTriggered = !result.Success;
            await _proxyCallRecorder.RecordUsageAsync(callContext, cancellationToken);

            if (result.Success)
            {
                SafeSucceedRoute(route.RouteId);
                var responseContentType = result.IsStreaming ? "text/event-stream" : "application/json";

                if (isPassthrough)
                {
                    // OpenAI 上游直接透传：记录对话并完成追踪
                    callContext.ResponseContentType = responseContentType;
                    await _proxyCallRecorder.RecordConversationAsync(callContext, cancellationToken);
                    _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                    return Content(result.ResponseBody, responseContentType);
                }

                // Anthropic 上游：将 Chat Completions 响应转为 Responses 格式
                var chatResponseBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI", actualProtocolType, result.ResponseBody,
                    result.IsStreaming, modelName,
                    result.InputTokens, result.CachedTokens, result.OutputTokens);
                var responsesBody = ProxyProtocolBridge.ConvertChatResponseToResponses(chatResponseBody);

                // 桥接成功：记录转换后的响应体到对话日志和开发者追踪
                callContext.ResponseBody = responsesBody;
                callContext.AdaptedResponseBody = responsesBody;
                callContext.ResponseContentType = "application/json";
                await _proxyCallRecorder.RecordConversationAsync(callContext, cancellationToken);
                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                return Content(responsesBody, "application/json");
            }

            // 非流式失败：完成追踪
            callContext.ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json";
            _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
            SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);
            if (!result.HasStartedStreaming)
            {
                SafeBlockRoute(route.RouteId);
            }
            lastResult = result;
        }

        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "全部上游路由均失败，请检查站点配置或联系管理员" } });
        }
        catch (OperationCanceledException)
        {
            _proxyCallRecorder.CancelTrace(traceId, "客户端已断开连接");
            throw;
        }
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

        // 生成本轮 WebSocket 请求的唯一标识
        var requestId = Guid.NewGuid();

        // 构建统一调用上下文，本轮 WebSocket 请求共享同一份上下文数据
        var callContext = new ProxyCallContext
        {
            RequestId = requestId,
            AccessKeyId = accessKeyId,
            ProtocolType = "OpenAI",
            Source = requestSource,
            RequestModel = modelName,
            ReasoningEffort = reasoningEffort,
            IsStreaming = true,
            RequestBody = rawRequestBody,
            RequestPath = "/v1/responses",
            RequestedAt = DateTimeOffset.UtcNow,
            UserAgent = Request.Headers.UserAgent.ToString(),
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            RequestHeaders = DeveloperInvocationTraceStore.CaptureHeaders(Request.Headers)
        };

        // 始终创建调用追踪记录，用于事件发布（UsageLog 等依赖 trace 完成时触发）。
        // DeveloperFeaturesEnabled 仅控制 Invocations 页面是否可见，不影响数据采集和推送。
        var traceId = _proxyCallRecorder.BeginTrace(callContext);

        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        // AccessKey 路由限定。WebSocket 方法接收的是 accessKeyId，需要从缓存查 accessKey 对象。
        // 如果密钥已被禁用或缓存失效，GetAccessKeyByIdAsync 返回 null，此时按"最小权限"拒绝（fail-close），
        // 而非默认放行全部路由（避免被禁用密钥的活跃 WebSocket 会话权限被放大）。
        var wsAccessKey = await _metadataCache.GetAccessKeyByIdAsync(accessKeyId, cancellationToken);
        if (wsAccessKey is null)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status401Unauthorized, "访问密钥无效或已被禁用", cancellationToken);
            return false;
        }
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
        var attemptIndex = 0;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        try
        {
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

            // 更新统一上下文中的本次尝试级字段
            callContext.AttemptIndex = attemptIndex;
            callContext.AttemptedModel = route.UpstreamModelName;
            callContext.UpstreamProtocolType = actualProtocolType;
            callContext.ForwardingMode = isPassthrough ? "direct" : "bridge";
            callContext.TargetSiteId = route.SiteId;
            callContext.TargetSiteName = route.SiteName;
            callContext.RouteId = route.RouteId;

            var traceAttemptId = _proxyCallRecorder.BeginTraceAttempt(traceId, callContext);

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

            // 更新统一上下文中的结果字段并写入用量日志
            callContext.Success = streamResult.Success;
            callContext.StatusCode = streamResult.StatusCode;
            callContext.ErrorMessage = streamResult.ErrorMessage ?? string.Empty;
            callContext.ResponseBody = streamResult.ResponseBody;
            callContext.InputTokens = streamResult.InputTokens;
            callContext.CachedTokens = streamResult.CachedTokens;
            callContext.OutputTokens = streamResult.OutputTokens;
            callContext.IsStreaming = true;
            callContext.IsStreamInterrupted = streamResult.IsStreamInterrupted;
            callContext.FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs;
            callContext.StreamDurationMs = streamResult.StreamDurationMs;
            callContext.TotalDurationMs = streamResult.TotalDurationMs;
            callContext.RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex;
            callContext.IsFinalResult = streamResult.Success;
            callContext.FallbackTriggered = !streamResult.Success;
            await _proxyCallRecorder.RecordUsageAsync(callContext, CancellationToken.None);

            if (streamResult.Success)
            {
                // WebSocket 流式成功：记录对话并完成追踪
                SafeSucceedRoute(route.RouteId);
                sessionState.LastRequestJson = normalizedRequestBody;
                sessionState.LastResponseOutputJson = string.IsNullOrWhiteSpace(streamOutcome.CompletedOutputJson)
                    ? "[]"
                    : streamOutcome.CompletedOutputJson;
                callContext.ResponseContentType = "application/websocket+json";
                await _proxyCallRecorder.RecordConversationAsync(callContext, CancellationToken.None);
                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                return true;
            }

            // WebSocket 流式失败：完成追踪
            callContext.ResponseContentType = "application/websocket+json";
            _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
            SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, streamResult);
            if (!streamResult.HasStartedStreaming)
            {
                SafeBlockRoute(route.RouteId);
            }
            lastResult = streamResult;
            if (!streamOutcome.CanFallback)
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await WriteResponsesWebSocketErrorAsync(webSocket, streamResult.StatusCode > 0 ? streamResult.StatusCode : StatusCodes.Status502BadGateway, streamResult.ErrorMessage ?? "全部上游路由均失败，请检查站点配置或联系管理员", cancellationToken);
                }
                return false;
            }
        }

        if (webSocket.State == WebSocketState.Open)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, lastResult?.StatusCode > 0 ? lastResult.StatusCode : StatusCodes.Status502BadGateway, lastResult?.ErrorMessage ?? "全部上游路由均失败，请检查站点配置或联系管理员", cancellationToken);
        }
        return false;
        }
        catch (OperationCanceledException)
        {
            _proxyCallRecorder.CancelTrace(traceId, "客户端已断开连接");
            throw;
        }
    }

}
