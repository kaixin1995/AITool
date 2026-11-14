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
    private static void IncludeCompactAttempt(ProxyForwardResult result, ProxyForwardResult previous)
    {
        result.InputTokens += previous.InputTokens;
        result.CachedTokens += previous.CachedTokens;
        result.OutputTokens += previous.OutputTokens;
        result.TotalDurationMs += previous.TotalDurationMs;
        result.RateLimitRetryCount += previous.RateLimitRetryCount;
    }

    private static bool IsCompactShapeFailure(ProxyForwardResult result)
    {
        if (result.StatusCode is 404 or 405 or 413 or 501) return true;
        if (result.StatusCode is not (400 or 422 or 502)) return false;
        var message = string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.ResponseBody : result.ErrorMessage;
        return new[] { "unsupported_input", "unsupported input", "unknown item", "item not found", "invalid_request", "upstream request failed",
            "system messages", "too large", "context length", "content filter", "request too long", "stream was incomplete", "no usable" }
            .Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
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

            AccountSessionIsolationContext isolationContext;
            UpstreamReferenceRestriction? referenceRestriction;
            var exactReplay = false;
            try
            {
                var rawRoot = JsonNode.Parse(rawRequest) as JsonObject;
                var previousId = rawRoot?["previous_response_id"]?.ToString();
                exactReplay = previousId is not null && sessionState.HasFullTranscript
                    && previousId == sessionState.ActualLastCompletedResponseId && sessionState.LastCompletedOwner is not null;
                try
                {
                    // Always validate the original wire JSON, including duplicate/invalid fields,
                    // expiry and ownership. Only a proven local synthetic replay may bypass native replay.
                    referenceRestriction = await ResolveReferencesAsync(accessKey.Id.ToString("D"), rawRequest, cancellationToken);
                }
                catch (UpstreamReferenceException ex) when (exactReplay && ex.Code == "reference_replay_required")
                {
                    rawRoot!.Remove("previous_response_id");
                    referenceRestriction = await ResolveReferencesAsync(accessKey.Id.ToString("D"), rawRoot.ToJsonString(), cancellationToken);
                }
                if (previousId is not null && !exactReplay)
                    throw new UpstreamReferenceException("reference_replay_required", "WebSocket continuation requires the exact last completed response and complete local history.");
                if (exactReplay)
                {
                    if (referenceRestriction is not null && referenceRestriction != sessionState.LastCompletedOwner)
                        throw new UpstreamReferenceException("reference_conflict", "References must belong to the same upstream owner.");
                    referenceRestriction = sessionState.LastCompletedOwner;
                }
                isolationContext = AccountSessionIsolationService.Capture(accessKey.Id.ToString("D"), Request.Headers,
                    rawRequest, sessionState.ConnectionSeed);
            }
            catch (UpstreamReferenceException ex)
            {
                await WriteResponsesWebSocketErrorAsync(webSocket, 400, ex.Code + ": " + ex.Message, cancellationToken);
                continue;
            }
            catch (JsonException)
            {
                await WriteResponsesWebSocketErrorAsync(webSocket, 400, "Invalid JSON request.", cancellationToken);
                continue;
            }
            if (!TryNormalizeResponsesWebSocketRequest(rawRequest,
                sessionState.HasFullTranscript ? sessionState.LastRequestJson : string.Empty,
                sessionState.HasFullTranscript ? sessionState.LastResponseOutputJson : "[]", out var normalizedRequest, out var errorMessage))
            {
                await WriteResponsesWebSocketErrorAsync(webSocket, StatusCodes.Status400BadRequest, errorMessage ?? "WebSocket 请求无效，请检查请求格式", cancellationToken);
                continue;
            }

            GatewayWebSocketTurn? enhancementTurn;
            try { enhancementTurn = await GatewayWebSocketTurn.BeginAsync(HttpContext, accessKey.Id, rawRequest, cancellationToken); }
            catch (InvalidOperationException ex)
            {
                await WriteResponsesWebSocketErrorAsync(webSocket, 429, ex.Message, cancellationToken);
                continue;
            }
            await using var enhancementSettlement = enhancementTurn;

            try
            {
            // A failed turn invalidates replay state until a new complete transcript is observed.
            sessionState.HasFullTranscript = false;
            var turnCompleted = await ProcessResponsesWebSocketTurnAsync(
                webSocket,
                accessKey.Id,
                requestSource,
                runtimeSettings,
                rawRequest,
                normalizedRequest!,
                sessionState,
                isolationContext,
                referenceRestriction,
                cancellationToken);

            if (!turnCompleted)
            {
                continue;
            }
            }
            catch (UpstreamReferenceException ex)
            {
                sessionState.HasFullTranscript = false;
                await TryWriteResponsesWebSocketErrorAsync(webSocket, 400, ex.Code + ": " + ex.Message, cancellationToken);
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
        try { return await ResponsesCoreGuarded(cancellationToken, isCompact); }
        catch (UpstreamReferenceException ex)
        {
            return Response.HasStarted ? new EmptyResult() : ReferenceError(ex);
        }
    }

    private async Task<IActionResult> ResponsesCoreGuarded(CancellationToken cancellationToken, bool isCompact)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        var modelName = ProxyProtocolBridge.ExtractResponsesModel(requestBody);
        // 压缩端点不支持流式（CLIProxyAPI 对 stream:true 直接 400）：统一按非流式处理，
        // body 中的 stream 字段在 NormalizeResponsesBody（Codex 目标）中删除。
        var hasCompactTrigger = ProxyProtocolBridge.HasCompactionTrigger(requestBody);
        var compactAsStream = hasCompactTrigger && !isCompact && ProxyProtocolBridge.ExtractResponsesStream(requestBody);
        var enableStreaming = !isCompact && !hasCompactTrigger && ProxyProtocolBridge.ExtractResponsesStream(requestBody);
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

        HttpContext.RequestServices.GetService<GatewayRequestContext>()?.CaptureSession(accessKey.Id, Request.Headers, requestBody);
        var isolationContext = AccountSessionIsolationService.Capture(accessKey.Id.ToString("D"), Request.Headers, requestBody);
        UpstreamReferenceRestriction? referenceRestriction;
        try
        {
            referenceRestriction = await ResolveReferencesAsync(accessKey.Id.ToString("D"), requestBody, cancellationToken);
        }
        catch (UpstreamReferenceException ex) { return ReferenceError(ex); }
        try { requestBody = ProxyProtocolBridge.ExpandLocalCompactions(requestBody); }
        catch (FormatException) { return BadRequest(new { error = new { message = "Invalid local compaction content.", type = "invalid_request_error", code = "invalid_compaction" } }); }
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

        // 路由候选策略（全部默认关闭）：会话亲和把上次成功候选挪到最前，配额排序按「最早重置优先」。
        allRoutes = await RestrictReferenceRoutesAsync(allRoutes, referenceRestriction, cancellationToken);
        if (referenceRestriction is not null && allRoutes.Count == 0)
            return ReferenceError(new UpstreamReferenceException("reference_owner_mismatch", "The reference owner is not available in the authorized route."));
        var conversationKey = AccountSessionIsolationService.ScopeAffinity(accessKey.Id.ToString("D"),
            ResolveConversationKeySafely(runtimeSettings, Request, modelName, requestBody));
        allRoutes = await PreorderRoutesByPolicyAsync(allRoutes, runtimeSettings, conversationKey, cancellationToken);
        if (HasPreviousResponseReference(requestBody))
        {
            allRoutes = allRoutes.Where(r => !string.Equals(r.ManagedSource, "Codex", StringComparison.OrdinalIgnoreCase)
                && !ProxyProtocolBridge.IsCodexTarget(r.BaseUrl)
                && string.Equals(r.ResolveProtocolForClient("Responses"), "Responses", StringComparison.OrdinalIgnoreCase)).ToList();
            if (allRoutes.Count == 0)
                return ReferenceError(new UpstreamReferenceException("reference_replay_required", "This upstream does not support previous_response_id; a complete replay is required."));
        }

        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var routeIndex = -1;
        var attemptedRoutes = new RouteAttemptTracker(allRoutes);
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            routeIndex++;
            if (!attemptedRoutes.CanAttempt(route) || IsRouteBlockedSafely(route.CircuitKey))
                continue;

            if (!attemptedRoutes.UsesGroupedFallback) attemptIndex++;
            // Responses 客户端优先选择上游原生 Responses；不支持时再降级为 OpenAI/Anthropic 协议转换。
            var actualProtocolType = route.ResolveProtocolForClient("Responses");
            if (!string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                && ProxyProtocolBridge.ContainsOpaqueCompaction(requestBody))
            {
                lastResult = new ProxyForwardResult { Success = false, StatusCode = 400,
                    ErrorMessage = "This upstream cannot read an opaque compaction from another provider." };
                continue;
            }

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteKeyId ?? route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken, displaySiteId: route.SiteId);

            if (!concurrencyHandle.Acquired || !attemptedRoutes.TryStart(route))
            {
                continue;
            }
            if (attemptedRoutes.UsesGroupedFallback) attemptIndex++;

            // Responses 端点的转发逻辑：
            // - 原生 Responses 站点：直接透传 Responses 请求体，URL 指向 /responses
            // - OpenAI Chat 站点：转换为 Chat Completions，URL 指向 /chat/completions
            // - Anthropic 站点：由 PrepareRequestBody 的直转分支转换为 Anthropic 请求（不经 Chat 中转）
            // 不能把 actualProtocolType=OpenAI 当作 Responses 透传，否则普通 OpenAI Chat 上游会收到
            // 不支持的 /responses 请求，常见表现为 HTTP 406。
            var isPassthrough = string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase);
            var summarizeCompact = hasCompactTrigger || (isCompact && !isPassthrough);
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Responses",
                actualProtocolType,
                summarizeCompact ? ProxyProtocolBridge.PrepareCompactSummaryRequest(requestBody) : requestBody,
                route.SiteModelName,
                enableStreaming,
                route.OverrideReasoningEffort,
                route.BaseUrl,
                route.CompatibilityRules,
                isPassthrough: isPassthrough,
                isCompact: isCompact && !summarizeCompact,
                geminiProjectId: route.GoogleProjectId);

            var forwardHeaders = BuildForwardHeaders(route, actualProtocolType, preparedRequestBody);
            if (_sessionIsolation is not null)
            {
                var isolated = await _sessionIsolation.ApplyAsync(isolationContext, route.AccountIdentity,
                    actualProtocolType, ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl), preparedRequestBody,
                    forwardHeaders, cancellationToken);
                preparedRequestBody = isolated.PreparedRequestBody;
                forwardHeaders = isolated.ForwardHeaders;
            }
            await SetReferenceOutputContextAsync(isolationContext.ClientScope, route, actualProtocolType, referenceRestriction, forwardHeaders, cancellationToken);
            if (summarizeCompact) _nativeReferenceOutput = false;
            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType, preparedRequestBody, forwardHeaders);

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
                SiteId = route.SiteId,
                ManagedSource = route.ManagedSource,
                ProtocolType = actualProtocolType,
                TargetModelName = route.SiteModelName,
                RequestBody = isCompact || hasCompactTrigger ? ProxyProtocolBridge.PrepareCompactSummaryRequest(requestBody) : requestBody,
                PreparedRequestBody = preparedRequestBody,
                EnableStreaming = enableStreaming,
                RequestTimeoutSeconds = summarizeCompact || isCompact ? Math.Min(240, Math.Max(1, runtimeSettings.ProxyRequestTimeoutSeconds)) : runtimeSettings.ProxyRequestTimeoutSeconds,
                StreamIdleTimeoutSeconds = runtimeSettings.ProxyStreamIdleTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                    RateLimitRetryCount = runtimeSettings.RateLimitRetryCount,
                ForwardHeaders = forwardHeaders,
                EgressProxyUrl = route.EgressProxyUrl,
                AccountIdentity = route.AccountIdentity,
                RefreshTargetApiKeyAsync = CreateCredentialRefreshCallback(route),
                InvalidateTargetCredentialAsync = CreateCredentialInvalidationCallback(route),
                DisableTargetCredentialAsync = CreateCredentialDisableCallback(route),
                // Codex 远程压缩走专用端点 responses/compact（对照 cc-switch endpoint_with_query("/responses/compact")）；
                // 普通 Responses 请求端点不变，正常对话行为不受影响。
                TargetPath = string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                    ? ResolveGeminiTargetPath(enableStreaming)
                    : isPassthrough
                        ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, isCompact && !summarizeCompact ? "responses/compact" : "responses")
                        : null
            };

            if (enableStreaming)
            {
                StreamForwardOutcome streamOutcome;
                AITool.Application.Proxy.UpstreamReferenceException? referenceFault = null;
                try
                {
                    if (isPassthrough)
                    {
                        // OpenAI 上游直接透传
                        streamOutcome = await ForwardOpenAiStreamPassthroughAsync(forwardRequest, cancellationToken);
                    }
                    else if (string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
                    {
                        // OpenAI Chat Completions 上游：逐个 SSE 块转换为 Responses 事件。
                        var responsesState = new ChatToResponsesStreamState { Model = route.SiteModelName, DeferChatCompletion = true };
                        ProxyProtocolBridge.ConfigureResponsesTools(responsesState, requestBody);
                        streamOutcome = await ForwardOpenAiStreamPassthroughAsync(
                            forwardRequest,
                            cancellationToken,
                            chunk => ConvertOpenAiChatSseBlockToResponses(chunk, responsesState), responsesState);
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
                }
                catch (AITool.Application.Proxy.UpstreamReferenceException ex)
                {
                    // 引用守卫在流中拒绝输出：合成失败结果，让追踪/用量/熔断走正常收口（不卡「等待」）；
                    // 终态返回保留 ReferenceError 的显式 JSON 语义（503/400，防 SSE 协商 406）。
                    referenceFault = ex;
                    streamOutcome = StreamReferenceFaultOutcome(ex);
                }

                var streamResult = streamOutcome.Result;
                // 客户端在上游完整完成（response.completed/[DONE] 已收到）后才断开：上游已按完整
                // 输出计费，按成功入账而非 canceled——否则真实消耗全部逃逸用量统计。
                if (streamResult.IsCanceled && streamOutcome.UpstreamCompleted)
                {
                    streamResult.IsCanceled = false;
                    streamResult.Success = true;
                    streamResult.IsStreamInterrupted = false;
                    streamResult.ErrorMessage = null;
                }
                // 防御：守卫错误被更内层吞掉时不以成功口径收口（正常路径已在上方 catch 处理）。
                if (referenceFault is null && _referenceOutputError is not null && !streamResult.Success)
                {
                    referenceFault = _referenceOutputError;
                    streamResult.ErrorMessage ??= $"upstream reference guard rejected stream output ({_referenceOutputError.Code})";
                }
                if (streamResult.IsCanceled)
                {
                    // 客户端中途取消：上游已消耗的部分 token 照常入账（status=canceled），
                    // 追踪收口为 canceled，绝不静默丢行。
                    SafeRecordProxyDiagnostic("Responses", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, streamResult, requestId, traceId);
                    await SafeLogUsageAsync(new UsageLogEntry
                    {
                        RequestId = requestId,
                        AccessKeyId = accessKey.Id,
                        ProtocolType = "Responses",
                        ForwardingMode = isPassthrough ? "direct" : "bridge",
                        RequestModel = modelName,
                        AttemptedModel = route.UpstreamModelName,
                        TargetSiteId = route.SiteId,
                        Status = "canceled",
                        Source = requestSource,
                        RetryCount = attemptIndex - 1,
                        RateLimitRetries = streamResult.RateLimitRetryCount,
                        AttemptIndex = attemptIndex,
                        IsFinalResult = true,
                        FallbackTriggered = false,
                        ErrorMessage = streamResult.ErrorMessage ?? string.Empty,
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
                    // 客户端取消：同样收口追踪，避免调用记录永远停留在「等待」。
                    SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                    {
                        Status = "canceled",
                        StatusCode = streamResult.StatusCode,
                        IsStreaming = true,
                        TotalDurationMs = streamResult.TotalDurationMs
                    });
                    return new EmptyResult();
                }

                SafeRecordProxyDiagnostic("Responses", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, streamResult, requestId, traceId);
                var streamCanFallback = !streamResult.Success
                    && streamOutcome.CanFallback
                    && !RouteCandidatePolicyService.ClientErrorBlocksFallback(runtimeSettings.StrictClientErrorFallbackEnabled, streamResult)
                    && allRoutes.Skip(routeIndex + 1).Any(candidate => attemptedRoutes.CanAttempt(candidate) && !IsRouteBlockedSafely(candidate.CircuitKey));

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
                    RateLimitRetries = streamResult.RateLimitRetryCount,
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
                    RecordSessionAffinitySafely(conversationKey, route.CircuitKey);
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
                await RecordRouteFailureByPolicyAsync(route, streamResult, runtimeSettings);
                lastResult = streamResult;
                if (!streamOutcome.CanFallback
                    || RouteCandidatePolicyService.ClientErrorBlocksFallback(runtimeSettings.StrictClientErrorFallbackEnabled, streamResult))
                {
                    // 引用守卫拒绝：显式 JSON 错误（503/400）；已开始写流时客户端已收到内容，补终止符安静结束。
                    if (referenceFault is not null)
                    {
                        if (Response.HasStarted)
                        {
                            await TryWriteReferenceFailureAsync(referenceFault, true, cancellationToken);
                            return new EmptyResult();
                        }
                        return ReferenceError(referenceFault);
                    }
                    // 严格回退命中：客户端尚未收到任何字节（SSE 只在 2xx 才回调），把上游 4xx 原样回给
                    // 客户端——绝不能返回 EmptyResult（流式桥已预置 200，会变成静默空流）。
                    return WriteStreamFallbackError(streamResult);
                }

                continue;
            }

            // 非流式：仍按单路由超时控制；但若客户端主动取消，则直接结束，不再继续回退后续候选。
            // 上游可能已消耗部分资源：照常入账 canceled 行，绝不静默丢行。
            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
            var compactRequest = isCompact || hasCompactTrigger;
            var compactCanRetry = compactRequest && referenceRestriction is null
                && !ProxyProtocolBridge.ContainsOpaqueCompaction(requestBody)
                && !HasPreviousResponseReference(requestBody)
                && JsonNode.Parse(requestBody)?["conversation"] is null;
            var retryShape = IsCompactShapeFailure(result);
            var compactRetried = false;
            if (compactCanRetry && !result.IsCanceled && !result.Success && retryShape)
            {
                var plainBody = ProxyProtocolBridge.PreparePlainCompactSummaryRequest(requestBody);
                forwardRequest.PreparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("Responses", actualProtocolType,
                    plainBody, route.SiteModelName, false, route.OverrideReasoningEffort, route.BaseUrl,
                    route.CompatibilityRules, isPassthrough: isPassthrough, geminiProjectId: route.GoogleProjectId);
                if (_sessionIsolation is not null)
                {
                    var isolated = await _sessionIsolation.ApplyAsync(isolationContext, route.AccountIdentity, actualProtocolType,
                        ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl), forwardRequest.PreparedRequestBody, forwardHeaders, cancellationToken);
                    forwardRequest.PreparedRequestBody = isolated.PreparedRequestBody;
                    forwardRequest.ForwardHeaders = isolated.ForwardHeaders;
                }
                if (isPassthrough) forwardRequest.TargetPath = SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses");
                var firstAttempt = result;
                result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
                IncludeCompactAttempt(result, firstAttempt);
                retryShape = true;
                compactRetried = true;
                summarizeCompact = true;
                _nativeReferenceOutput = false;
            }
            if (result.IsCanceled)
            {
                SafeRecordProxyDiagnostic("Responses", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, result, requestId, traceId);
                await SafeLogUsageAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "Responses",
                    ForwardingMode = isPassthrough ? "direct" : "bridge",
                    RequestModel = modelName,
                    AttemptedModel = route.UpstreamModelName,
                    TargetSiteId = route.SiteId,
                    Status = "canceled",
                    Source = requestSource,
                    RetryCount = attemptIndex - 1,
                    RateLimitRetries = result.RateLimitRetryCount,
                    AttemptIndex = attemptIndex,
                    IsFinalResult = true,
                    FallbackTriggered = false,
                    ErrorMessage = result.ErrorMessage ?? string.Empty,
                    HttpStatusCode = result.StatusCode > 0 ? result.StatusCode : null,
                    InputTokens = result.InputTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.OutputTokens,
                    IsStreaming = false,
                    IsStreamInterrupted = result.IsStreamInterrupted,
                    FirstTokenLatencyMs = result.FirstTokenLatencyMs,
                    StreamDurationMs = result.StreamDurationMs,
                    TotalDurationMs = result.TotalDurationMs,
                    ReasoningEffort = reasoningEffort
                }, CancellationToken.None);
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "canceled",
                    StatusCode = result.StatusCode,
                    IsStreaming = false,
                    TotalDurationMs = result.TotalDurationMs
                });
                return new EmptyResult();
            }

            SafeRecordProxyDiagnostic("Responses", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, result, requestId, traceId);
            var canFallback = !RouteCandidatePolicyService.ClientErrorBlocksFallback(runtimeSettings.StrictClientErrorFallbackEnabled, result)
                && allRoutes.Skip(routeIndex + 1).Any(candidate => attemptedRoutes.CanAttempt(candidate) && !IsRouteBlockedSafely(candidate.CircuitKey));

            // 协议转换先行：转换失败必须在写日志之前置为失败，保证该次尝试按 fail 入账，
            // 避免出现"日志已记成功、客户端却收到 502"的口径错位。
            // Anthropic 上游走直转（不经 Chat 中转）；Chat 上游仍是单次转换。
            string? convertedResponseBody = null;
            if (result.Success && (!isPassthrough || summarizeCompact))
            {
                if (isPassthrough) convertedResponseBody = result.ResponseBody;
                else if (string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    var anthropicBody = ProxyProtocolBridge.BuildAnthropicResponseFromGemini(result.ResponseBody, route.SiteModelName);
                    convertedResponseBody = anthropicBody is null ? null : ProxyProtocolBridge.ConvertAnthropicResponseToResponses(anthropicBody, requestBody);
                }
                else
                    convertedResponseBody = string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                        ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(result.ResponseBody, requestBody)
                        : ProxyProtocolBridge.ConvertChatResponseToResponses(result.ResponseBody, requestBody);
                if (summarizeCompact && !string.IsNullOrEmpty(convertedResponseBody))
                    convertedResponseBody = ProxyProtocolBridge.BuildCompactResponse(convertedResponseBody);
                if (string.IsNullOrEmpty(convertedResponseBody))
                {
                    // 转换失败不能伪装成成功响应，保留 fallback 机会给下一条路由。
                    result.Success = false;
                    result.ErrorMessage ??= "upstream response protocol conversion failed";
                }
            }
            if (compactCanRetry && summarizeCompact && !result.Success && !result.IsCanceled
                && !compactRetried && result.StatusCode is >= 200 and < 300)
            {
                var plainBody = ProxyProtocolBridge.PreparePlainCompactSummaryRequest(requestBody);
                forwardRequest.PreparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("Responses", actualProtocolType,
                    plainBody, route.SiteModelName, false, route.OverrideReasoningEffort, route.BaseUrl,
                    route.CompatibilityRules, isPassthrough: isPassthrough, geminiProjectId: route.GoogleProjectId);
                if (_sessionIsolation is not null)
                {
                    var isolated = await _sessionIsolation.ApplyAsync(isolationContext, route.AccountIdentity, actualProtocolType,
                        ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl), forwardRequest.PreparedRequestBody, forwardHeaders, cancellationToken);
                    forwardRequest.PreparedRequestBody = isolated.PreparedRequestBody;
                    forwardRequest.ForwardHeaders = isolated.ForwardHeaders;
                }
                if (isPassthrough) forwardRequest.TargetPath = SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses");
                var firstAttempt = result;
                result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
                IncludeCompactAttempt(result, firstAttempt);
                retryShape = true;
                compactRetried = true;
                if (result.Success)
                {
                    var body = isPassthrough ? result.ResponseBody
                        : string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                            ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(result.ResponseBody, requestBody)
                            : string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                                ? ProxyProtocolBridge.BuildAnthropicResponseFromGemini(result.ResponseBody, route.SiteModelName) is { } gemini
                                    ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(gemini, requestBody) : null
                                : ProxyProtocolBridge.ConvertChatResponseToResponses(result.ResponseBody, requestBody);
                    convertedResponseBody = string.IsNullOrEmpty(body) ? null : ProxyProtocolBridge.BuildCompactResponse(body);
                    if (convertedResponseBody is null) { result.Success = false; result.ErrorMessage = "Compaction produced no complete summary."; }
                }
            }
            if (compactCanRetry && !result.Success && !result.IsCanceled && (retryShape || compactRetried && result.StatusCode is >= 200 and < 300)
                && result.StatusCode is not (401 or 403 or 429))
            {
                convertedResponseBody = ProxyProtocolBridge.BuildLocalCompactResponse(requestBody);
                if (convertedResponseBody is not null)
                {
                    result.Success = true;
                    result.StatusCode = 200;
                    result.ErrorMessage = null;
                    summarizeCompact = true;
                    _nativeReferenceOutput = false;
                }
            }
            if (summarizeCompact && !result.Success && result.StatusCode is >= 200 and < 300) result.StatusCode = 502;

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
                RateLimitRetries = result.RateLimitRetryCount,
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
                RecordSessionAffinitySafely(conversationKey, route.CircuitKey);
                var responseContentType = result.IsStreaming ? "text/event-stream" : "application/json";

                if (isPassthrough && !summarizeCompact)
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
                    await CommitReferenceOutputAsync(result.ResponseBody, cancellationToken);
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
                await CommitReferenceOutputAsync(convertedResponseBody!, cancellationToken);
                return compactAsStream
                    ? Content(ProxyProtocolBridge.CompactResponseAsSse(convertedResponseBody!), "text/event-stream")
                    : Content(convertedResponseBody!, "application/json");
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
            await RecordRouteFailureByPolicyAsync(route, result, runtimeSettings);
            lastResult = result;
            // 客户端错误严格回退：纯请求形态错误直接把上游 4xx 回给客户端，不再消耗其余候选。
            if (RouteCandidatePolicyService.ClientErrorBlocksFallback(runtimeSettings.StrictClientErrorFallbackEnabled, result))
            {
                break;
            }
        }

        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return new JsonResult(new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } })
        {
            StatusCode = statusCode,
            ContentType = "application/json"
        };
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
        AccountSessionIsolationContext isolationContext,
        UpstreamReferenceRestriction? referenceRestriction,
        CancellationToken cancellationToken)
    {
        try { normalizedRequestBody = ProxyProtocolBridge.ExpandLocalCompactions(normalizedRequestBody); }
        catch (FormatException)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, 400, "Invalid local compaction content.", cancellationToken);
            return false;
        }
        try
        {
            // A WebSocket append may inherit native compaction from the previous output;
            // validate the merged transcript as well as the original wire request.
            var mergedRestriction = await ResolveReferencesAsync(isolationContext.ClientScope, normalizedRequestBody, cancellationToken);
            if (referenceRestriction is not null && mergedRestriction is not null && referenceRestriction != mergedRestriction)
                throw new UpstreamReferenceException("reference_owner_mismatch", "The request references different upstream owners or namespaces.");
            referenceRestriction ??= mergedRestriction;
        }
        catch (UpstreamReferenceException ex)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, ReferenceFailureStatusCode(ex), ex.Code + ": " + ex.Message, cancellationToken);
            return false;
        }
        var modelName = ProxyProtocolBridge.ExtractResponsesModel(normalizedRequestBody);
        var compactTrigger = ProxyProtocolBridge.HasCompactionTrigger(normalizedRequestBody);
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
        if (wsAccessKey is null)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, 401, "The access key is no longer enabled.", cancellationToken);
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

        // 路由候选策略（全部默认关闭）：会话亲和把上次成功候选挪到最前，配额排序按「最早重置优先」。
        allRoutes = await RestrictReferenceRoutesAsync(allRoutes, referenceRestriction, cancellationToken);
        if (referenceRestriction is not null && allRoutes.Count == 0)
        {
            await WriteResponsesWebSocketErrorAsync(webSocket, 400, "The reference owner is not available in the authorized route.", cancellationToken);
            return false;
        }
        var conversationKey = AccountSessionIsolationService.ScopeAffinity(accessKeyId.ToString("D"),
            ResolveConversationKeySafely(runtimeSettings, Request, modelName, rawRequestBody));
        allRoutes = await PreorderRoutesByPolicyAsync(allRoutes, runtimeSettings, conversationKey, cancellationToken);

        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var routeIndex = -1;
        var attemptedRoutes = new RouteAttemptTracker(allRoutes);
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            routeIndex++;
            if (!attemptedRoutes.CanAttempt(route) || IsRouteBlockedSafely(route.CircuitKey))
                continue;

            if (!attemptedRoutes.UsesGroupedFallback) attemptIndex++;
            // Responses 客户端优先选择上游原生 Responses；不支持时再降级为 OpenAI/Anthropic 协议转换。
            var actualProtocolType = route.ResolveProtocolForClient("Responses");
            if (!string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                && ProxyProtocolBridge.ContainsOpaqueCompaction(normalizedRequestBody))
            {
                lastResult = new ProxyForwardResult { Success = false, StatusCode = 400,
                    ErrorMessage = "This upstream cannot read an opaque compaction from another provider." };
                continue;
            }

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteKeyId ?? route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken, displaySiteId: route.SiteId);
            if (!concurrencyHandle.Acquired || !attemptedRoutes.TryStart(route))
            {
                continue;
            }
            if (attemptedRoutes.UsesGroupedFallback) attemptIndex++;

            var isPassthrough = string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase);
            // Anthropic 上游走 PrepareRequestBody 的直转分支；Chat 上游经 ConvertResponsesRequestToChat。
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Responses",
                actualProtocolType,
                compactTrigger ? ProxyProtocolBridge.PrepareCompactSummaryRequest(normalizedRequestBody) : normalizedRequestBody,
                route.SiteModelName,
                !compactTrigger,
                route.OverrideReasoningEffort,
                route.BaseUrl,
                route.CompatibilityRules,
                isPassthrough: isPassthrough,
                geminiProjectId: route.GoogleProjectId);

            var forwardHeaders = BuildForwardHeaders(route, actualProtocolType, preparedRequestBody);
            if (_sessionIsolation is not null)
            {
                var isolated = await _sessionIsolation.ApplyAsync(isolationContext, route.AccountIdentity,
                    actualProtocolType, ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl), preparedRequestBody,
                    forwardHeaders, cancellationToken);
                preparedRequestBody = isolated.PreparedRequestBody;
                forwardHeaders = isolated.ForwardHeaders;
            }
            await SetReferenceOutputContextAsync(isolationContext.ClientScope, route, actualProtocolType, referenceRestriction, forwardHeaders, cancellationToken);
            if (compactTrigger) _nativeReferenceOutput = false;
            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType, preparedRequestBody, forwardHeaders);

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
                SiteId = route.SiteId,
                ManagedSource = route.ManagedSource,
                ProtocolType = actualProtocolType,
                TargetModelName = route.SiteModelName,
                RequestBody = compactTrigger ? ProxyProtocolBridge.PrepareCompactSummaryRequest(normalizedRequestBody) : normalizedRequestBody,
                PreparedRequestBody = preparedRequestBody,
                EnableStreaming = compactTrigger ? false : enableStreaming,
                RequestTimeoutSeconds = compactTrigger ? Math.Min(240, Math.Max(1, runtimeSettings.ProxyRequestTimeoutSeconds)) : runtimeSettings.ProxyRequestTimeoutSeconds,
                StreamIdleTimeoutSeconds = runtimeSettings.ProxyStreamIdleTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                    RateLimitRetryCount = runtimeSettings.RateLimitRetryCount,
                ForwardHeaders = forwardHeaders,
                EgressProxyUrl = route.EgressProxyUrl,
                AccountIdentity = route.AccountIdentity,
                RefreshTargetApiKeyAsync = CreateCredentialRefreshCallback(route),
                InvalidateTargetCredentialAsync = CreateCredentialInvalidationCallback(route),
                DisableTargetCredentialAsync = CreateCredentialDisableCallback(route),
                TargetPath = string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                    ? ResolveGeminiTargetPath(!compactTrigger)
                    : isPassthrough ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses") : null
            };

            StreamForwardOutcome streamOutcome;
            AITool.Application.Proxy.UpstreamReferenceException? referenceFault = null;
            try
            {
                if (compactTrigger)
                {
                    var compactResult = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
                    var retryAllowed = referenceRestriction is null && !ProxyProtocolBridge.ContainsOpaqueCompaction(normalizedRequestBody)
                        && !HasPreviousResponseReference(normalizedRequestBody) && JsonNode.Parse(normalizedRequestBody)?["conversation"] is null;
                    var retryShape = IsCompactShapeFailure(compactResult);
                    var compactRetried = false;
                    if (retryAllowed && !compactResult.Success && !compactResult.IsCanceled && retryShape)
                    {
                        var plain = ProxyProtocolBridge.PreparePlainCompactSummaryRequest(normalizedRequestBody);
                        forwardRequest.PreparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("Responses", actualProtocolType, plain,
                            route.SiteModelName, false, route.OverrideReasoningEffort, route.BaseUrl, route.CompatibilityRules,
                            isPassthrough: isPassthrough, geminiProjectId: route.GoogleProjectId);
                        if (_sessionIsolation is not null)
                        {
                            var isolated = await _sessionIsolation.ApplyAsync(isolationContext, route.AccountIdentity, actualProtocolType,
                                ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl), forwardRequest.PreparedRequestBody, forwardHeaders, cancellationToken);
                            forwardRequest.PreparedRequestBody = isolated.PreparedRequestBody;
                            forwardRequest.ForwardHeaders = isolated.ForwardHeaders;
                        }
                        var firstAttempt = compactResult;
                        compactResult = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
                        IncludeCompactAttempt(compactResult, firstAttempt);
                        retryShape = true;
                        compactRetried = true;
                    }
                    string? compactResponse = null;
                    if (compactResult.Success)
                    {
                        var body = isPassthrough ? compactResult.ResponseBody
                            : string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                                ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(compactResult.ResponseBody, normalizedRequestBody)
                                : string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                                    ? ProxyProtocolBridge.BuildAnthropicResponseFromGemini(compactResult.ResponseBody, route.SiteModelName) is { } geminiBody
                                        ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(geminiBody, normalizedRequestBody) : null
                                    : ProxyProtocolBridge.ConvertChatResponseToResponses(compactResult.ResponseBody, normalizedRequestBody);
                        if (!string.IsNullOrEmpty(body)) compactResponse = ProxyProtocolBridge.BuildCompactResponse(body);
                        if (compactResponse is null) { compactResult.Success = false; compactResult.ErrorMessage = "Compaction produced no complete summary."; }
                    }
                    if (compactResponse is null && retryAllowed && !compactResult.IsCanceled && !compactRetried
                        && compactResult.StatusCode is >= 200 and < 300)
                    {
                        var plain = ProxyProtocolBridge.PreparePlainCompactSummaryRequest(normalizedRequestBody);
                        forwardRequest.PreparedRequestBody = ProxyProtocolBridge.PrepareRequestBody("Responses", actualProtocolType, plain,
                            route.SiteModelName, false, route.OverrideReasoningEffort, route.BaseUrl, route.CompatibilityRules,
                            isPassthrough: isPassthrough, geminiProjectId: route.GoogleProjectId);
                        if (_sessionIsolation is not null)
                        {
                            var isolated = await _sessionIsolation.ApplyAsync(isolationContext, route.AccountIdentity, actualProtocolType,
                                ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl), forwardRequest.PreparedRequestBody, forwardHeaders, cancellationToken);
                            forwardRequest.PreparedRequestBody = isolated.PreparedRequestBody;
                            forwardRequest.ForwardHeaders = isolated.ForwardHeaders;
                        }
                        var firstAttempt = compactResult;
                        compactResult = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
                        IncludeCompactAttempt(compactResult, firstAttempt);
                        retryShape = true;
                        compactRetried = true;
                        if (compactResult.Success)
                        {
                            var body = isPassthrough ? compactResult.ResponseBody
                                : string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                                    ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(compactResult.ResponseBody, normalizedRequestBody)
                                    : string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                                        ? ProxyProtocolBridge.BuildAnthropicResponseFromGemini(compactResult.ResponseBody, route.SiteModelName) is { } gemini
                                            ? ProxyProtocolBridge.ConvertAnthropicResponseToResponses(gemini, normalizedRequestBody) : null
                                        : ProxyProtocolBridge.ConvertChatResponseToResponses(compactResult.ResponseBody, normalizedRequestBody);
                            if (!string.IsNullOrEmpty(body)) compactResponse = ProxyProtocolBridge.BuildCompactResponse(body);
                            if (compactResponse is null) { compactResult.Success = false; compactResult.ErrorMessage = "Compaction produced no complete summary."; }
                        }
                    }
                    if (compactResponse is null && retryAllowed && (retryShape || compactRetried && compactResult.StatusCode is >= 200 and < 300) && !compactResult.IsCanceled
                        && compactResult.StatusCode is not (401 or 403 or 429))
                        compactResponse = ProxyProtocolBridge.BuildLocalCompactResponse(normalizedRequestBody);
                    if (compactResponse is not null)
                    {
                        compactResult.Success = true;
                        compactResult.StatusCode = 200;
                        compactResult.ErrorMessage = null;
                        var sse = ProxyProtocolBridge.CompactResponseAsSse(compactResponse);
                        foreach (var payload in ExtractWebSocketJsonPayloadsFromSseText(sse))
                        {
                            await CommitReferenceOutputAsync(payload, cancellationToken);
                            await SendWebSocketJsonPayloadAsync(webSocket, payload, cancellationToken);
                            _referenceOutputExposed = true;
                        }
                        streamOutcome = new StreamForwardOutcome { Result = compactResult, CompletedOutputJson = JsonNode.Parse(compactResponse)!["output"]!.ToJsonString(), UpstreamCompleted = true };
                    }
                    else
                    {
                        if (compactResult.StatusCode is >= 200 and < 300) compactResult.StatusCode = 502;
                        streamOutcome = new StreamForwardOutcome { Result = compactResult, CanFallback = true };
                    }
                }
                else if (isPassthrough)
                {
                    streamOutcome = await ForwardOpenAiResponsesAsWebSocketAsync(webSocket, forwardRequest, cancellationToken);
                }
                else if (string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    // OpenAI Chat Completions SSE 逐块转换后再发送 Responses WebSocket 事件。
                    var responsesState = new ChatToResponsesStreamState { Model = route.SiteModelName, DeferChatCompletion = true };
                    ProxyProtocolBridge.ConfigureResponsesTools(responsesState, normalizedRequestBody);
                    streamOutcome = await ForwardOpenAiResponsesAsWebSocketAsync(
                        webSocket,
                        forwardRequest,
                        cancellationToken,
                        payload => ProxyProtocolBridge.ConvertChatStreamChunkToResponses(payload, responsesState), responsesState);
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
            }
            catch (AITool.Application.Proxy.UpstreamReferenceException ex)
            {
                // 引用守卫在流中拒绝输出：合成失败结果，让追踪/用量/熔断走正常收口（不卡「等待」）。
                referenceFault = ex;
                streamOutcome = StreamReferenceFaultOutcome(ex);
            }

            var streamResult = streamOutcome.Result;
            // 防御：守卫错误被更内层吞掉时不以成功口径收口（正常路径已在上方 catch 处理）。
            if (_referenceOutputError is not null && !streamResult.Success)
            {
                streamResult.ErrorMessage ??= $"upstream reference guard rejected stream output ({_referenceOutputError.Code})";
            }
            // 客户端断开导致的取消：不记失败、不计熔断、不尝试下一条路由（与 HTTP 路径一致），
            // 但上游已消耗的部分 token 照常入账（status=canceled），追踪收口为 canceled。
            if (streamResult.IsCanceled)
            {
                SafeRecordProxyDiagnostic("ResponsesWebSocket", requestSource, modelName, route, actualProtocolType, rawRequestBody, preparedRequestBody, streamResult, requestId, traceId);
                await SafeLogUsageAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKeyId,
                    ProtocolType = "Responses",
                    ForwardingMode = isPassthrough ? "direct" : "bridge",
                    RequestModel = modelName,
                    AttemptedModel = route.UpstreamModelName,
                    TargetSiteId = route.SiteId,
                    Status = "canceled",
                    Source = requestSource,
                    RetryCount = attemptIndex - 1,
                    RateLimitRetries = streamResult.RateLimitRetryCount,
                    AttemptIndex = attemptIndex,
                    IsFinalResult = true,
                    FallbackTriggered = false,
                    ErrorMessage = streamResult.ErrorMessage ?? string.Empty,
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
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "canceled",
                    StatusCode = streamResult.StatusCode,
                    IsStreaming = true,
                    TotalDurationMs = streamResult.TotalDurationMs
                });
                return false;
            }

            SafeRecordProxyDiagnostic("ResponsesWebSocket", requestSource, modelName, route, actualProtocolType, rawRequestBody, preparedRequestBody, streamResult, requestId, traceId);
            var canFallback = !streamResult.Success
                && streamOutcome.CanFallback
                && !RouteCandidatePolicyService.ClientErrorBlocksFallback(runtimeSettings.StrictClientErrorFallbackEnabled, streamResult)
                && allRoutes.Skip(routeIndex + 1).Any(candidate => attemptedRoutes.CanAttempt(candidate) && !IsRouteBlockedSafely(candidate.CircuitKey));

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
                RateLimitRetries = streamResult.RateLimitRetryCount,
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
                RecordSessionAffinitySafely(conversationKey, route.CircuitKey);
                sessionState.ActualLastCompletedResponseId = _observedCompletedResponseId;
                sessionState.LastCompletedOwner = _observedCompletedResponseId is null ? null
                    : new UpstreamReferenceRestriction((await ReferenceOwnerAsync(route, cancellationToken)).Owner, UpstreamReferenceNamespace.Create(route.BaseUrl, actualProtocolType));
                sessionState.HasFullTranscript = _observedCompletedResponseId is not null
                    && HasCompleteLocalTranscript(normalizedRequestBody);
                if (compactTrigger)
                {
                    var compactedRequest = JsonNode.Parse(normalizedRequestBody)!.AsObject();
                    compactedRequest["input"] = new JsonArray();
                    sessionState.LastRequestJson = compactedRequest.ToJsonString();
                }
                else sessionState.LastRequestJson = normalizedRequestBody;
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
            await RecordRouteFailureByPolicyAsync(route, streamResult, runtimeSettings);
            lastResult = streamResult;
            if (!streamOutcome.CanFallback
                || RouteCandidatePolicyService.ClientErrorBlocksFallback(runtimeSettings.StrictClientErrorFallbackEnabled, streamResult))
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    var effectiveError = referenceFault ?? _referenceOutputError;
                    var failureStatusCode = effectiveError is not null
                        ? ReferenceFailureStatusCode(effectiveError)
                        : (streamResult.StatusCode > 0 ? streamResult.StatusCode : StatusCodes.Status502BadGateway);
                    await TryWriteResponsesWebSocketErrorAsync(webSocket, failureStatusCode, streamResult.ErrorMessage ?? "All upstream routes failed", cancellationToken);
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
    private async Task TryWriteResponsesWebSocketErrorAsync(WebSocket webSocket, int statusCode, string message, CancellationToken cancellationToken)
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
