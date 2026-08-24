using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using AITool.Protocol;
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
        return await ResponsesCore(cancellationToken, isCompact: false);
    }

    /// <summary>
    /// Responses 主链路：isCompact=true 时为 Codex 远程压缩请求——
    /// 上游端点使用 responses/compact（对照 cc-switch 的 handle_responses_compact_for_app），
    /// 且压缩端点只接受非流式（不继承 Codex 的 stream=true 强制，反而删除 stream 字段）。
    /// </summary>
    private async Task<IActionResult> ResponsesCore(CancellationToken cancellationToken, bool isCompact)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        var modelName = ProxyProtocolBridge.ExtractResponsesModel(requestBody);
        // 压缩端点不支持流式（CLIProxyAPI 对 stream:true 直接 400）：统一按非流式处理，
        // body 中的 stream 字段在 NormalizeResponsesBody（Codex 目标）中删除。
        var enableStreaming = !isCompact && ProxyProtocolBridge.ExtractResponsesStream(requestBody);
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
            // - OpenAI Chat 站点：转换为 Chat Completions，URL 指向 /chat/completions
            // - Anthropic 站点：由 PrepareRequestBody 的直转分支转换为 Anthropic 请求（不经 Chat 中转）
            // 不能把 actualProtocolType=OpenAI 当作 Responses 透传，否则普通 OpenAI Chat 上游会收到
            // 不支持的 /responses 请求，常见表现为 HTTP 406。
            var isPassthrough = string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase);
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Responses",
                actualProtocolType,
                requestBody,
                route.SiteModelName,
                enableStreaming,
                route.OverrideReasoningEffort,
                route.BaseUrl,
                route.CompatibilityRules,
                isPassthrough: isPassthrough,
                isCompact: isCompact,
                geminiProjectId: route.GoogleProjectId);

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
                StreamIdleTimeoutSeconds = runtimeSettings.ProxyStreamIdleTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                ForwardHeaders = BuildForwardHeaders(route, actualProtocolType, preparedRequestBody),
                RefreshTargetApiKeyAsync = CreateCredentialRefreshCallback(route),
                PrepareTargetCredentialAsync = CreateCredentialPreparationCallback(route),
                DisableTargetCredentialAsync = CreateCredentialDisableCallback(route),
                // Codex 远程压缩走专用端点 responses/compact（对照 cc-switch endpoint_with_query("/responses/compact")）；
                // 普通 Responses 请求端点不变，正常对话行为不受影响。
                TargetPath = string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                    ? ResolveGeminiTargetPath(enableStreaming)
                    : isPassthrough
                        ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, isCompact ? "responses/compact" : "responses")
                        : null
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
                else if (string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    // Gemini 上游：Gemini SSE → Anthropic 事件 → Responses 事件（两级桥接）。
                    streamOutcome = await ForwardGeminiStreamAsResponsesAsync(forwardRequest, modelName, cancellationToken);
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

                SafeRecordProxyDiagnostic("Responses", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, streamResult, requestId, traceId);
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
                SafeBlockRoute(route.CircuitKey, new CircuitRouteMeta(route.SiteName, route.SiteModelName));
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

            SafeRecordProxyDiagnostic("Responses", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, result, requestId, traceId);
            var canFallback = allRoutes.Skip(routeIndex + 1).Any(candidate => !IsRouteBlockedSafely(candidate.CircuitKey));

            // 协议转换先行：转换失败必须在写日志之前置为失败，保证该次尝试按 fail 入账，
            // 避免出现"日志已记成功、客户端却收到 502"的口径错位。
            // Anthropic 上游走直转（不经 Chat 中转）；Chat 上游仍是单次转换。
            string? convertedResponseBody = null;
            if (result.Success && !isPassthrough)
            {
                convertedResponseBody = string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                    ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(result.ResponseBody)
                    : ProxyProtocolBridge.ConvertChatResponseToResponses(result.ResponseBody);
                if (string.IsNullOrEmpty(convertedResponseBody))
                {
                    // 转换失败不能伪装成成功响应，保留 fallback 机会给下一条路由。
                    result.Success = false;
                    result.ErrorMessage ??= "upstream response protocol conversion failed";
                }
            }

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
                FallbackTriggered = !result.Success && canFallback,
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

                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "success",
                    StatusCode = result.StatusCode,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(convertedResponseBody!),
                    ResponseContentType = "application/json",
                    IsStreaming = false,
                    InputTokens = result.InputTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.OutputTokens,
                    TotalDurationMs = result.TotalDurationMs
                });
                return Content(convertedResponseBody!, "application/json");
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
            SafeBlockRoute(route.CircuitKey, new CircuitRouteMeta(route.SiteName, route.SiteModelName));
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
            // Anthropic 上游走 PrepareRequestBody 的直转分支；Chat 上游经 ConvertResponsesRequestToChat。
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Responses",
                actualProtocolType,
                normalizedRequestBody,
                route.SiteModelName,
                true,
                route.OverrideReasoningEffort,
                route.BaseUrl,
                route.CompatibilityRules,
                isPassthrough: isPassthrough,
                geminiProjectId: route.GoogleProjectId);

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
                StreamIdleTimeoutSeconds = runtimeSettings.ProxyStreamIdleTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                ForwardHeaders = BuildForwardHeaders(route, actualProtocolType, preparedRequestBody),
                RefreshTargetApiKeyAsync = CreateCredentialRefreshCallback(route),
                PrepareTargetCredentialAsync = CreateCredentialPreparationCallback(route),
                DisableTargetCredentialAsync = CreateCredentialDisableCallback(route),
                TargetPath = string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                    ? ResolveGeminiTargetPath(true)
                    : isPassthrough ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses") : null
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
            else if (string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                // Gemini 上游：Gemini SSE → Anthropic 事件 → Responses WebSocket JSON。
                streamOutcome = await ForwardGeminiResponsesAsWebSocketAsync(webSocket, forwardRequest, modelName, cancellationToken);
            }
            else
            {
                streamOutcome = await ForwardAnthropicResponsesAsWebSocketAsync(webSocket, forwardRequest, modelName, cancellationToken);
            }
            var streamResult = streamOutcome.Result;
            // 客户端断开导致的取消：不记失败、不计熔断、不尝试下一条路由（与 HTTP 路径的 EmptyResult 早退一致）。
            if (streamResult.IsCanceled)
            {
                return false;
            }

            SafeRecordProxyDiagnostic("ResponsesWebSocket", requestSource, modelName, route, actualProtocolType, rawRequestBody, preparedRequestBody, streamResult, requestId, traceId);
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
            SafeBlockRoute(route.CircuitKey, new CircuitRouteMeta(route.SiteName, route.SiteModelName));
            lastResult = streamResult;
            if (!streamOutcome.CanFallback)
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await TryWriteResponsesWebSocketErrorAsync(webSocket, streamResult.StatusCode > 0 ? streamResult.StatusCode : StatusCodes.Status502BadGateway, streamResult.ErrorMessage ?? "All upstream routes failed", cancellationToken);
                }
                return false;
            }
        }

        if (webSocket.State == WebSocketState.Open)
        {
            await TryWriteResponsesWebSocketErrorAsync(webSocket, lastResult?.StatusCode > 0 ? lastResult.StatusCode : StatusCodes.Status502BadGateway, lastResult?.ErrorMessage ?? "All upstream routes failed", cancellationToken);
        }
        return false;
    }

    /// <summary>
    /// 对可能已断开的 WebSocket 安全发送终态错误帧：发送失败只吞异常，不把
    /// ObjectDisposedException 抛穿整个会话循环（与 SSE 终态写入的防护一致）。
    /// </summary>
    private static async Task TryWriteResponsesWebSocketErrorAsync(WebSocket webSocket, int statusCode, string message, CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, statusCode, message, cancellationToken);
        }
        catch
        {
            // 客户端已断开或连接已释放，无需处理。
        }
    }

}
