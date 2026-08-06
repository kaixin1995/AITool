using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AITool.Core.Services;

namespace AITool.Core.Controllers.Proxy;

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
    /// 代理调用统一记录服务，从一份上下文派发到 UsageLog、DeveloperTrace、ConversationLog 三个存储。
    /// </summary>
    private readonly IProxyCallRecorder _proxyCallRecorder;
    /// <summary>
    /// 负责维护路由熔断状态，避免持续命中异常站点。
    /// </summary>
    private readonly RouteCircuitStateStore _circuitStore;
    /// <summary>
    /// 提供访问密钥、路由和运行时设置等缓存数据。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>
    /// 模型并发限制器，按站点+模型粒度控制最大并发请求数。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    /// <summary>
    /// 负责在路由回退时发布 route-fallback 事件到 Admin 侧。
    /// </summary>
    private readonly CoreRouteFallbackEventPublisher _routeFallbackPublisher;
    /// <summary>
    /// 记录代理过程中的诊断日志。
    /// </summary>
    private readonly ILogger<AnthropicProxyController> _logger;

    /// <summary>
    /// 初始化 Anthropic 代理控制器依赖。
    /// </summary>
    public AnthropicProxyController(
        IProxyForwardService forwardService,
        IProxyCallRecorder proxyCallRecorder,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache,
        ModelConcurrencyLimiter concurrencyLimiter,
        CoreRouteFallbackEventPublisher routeFallbackPublisher,
        ILogger<AnthropicProxyController> logger)
    {
        _forwardService = forwardService;
        _proxyCallRecorder = proxyCallRecorder;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
        _concurrencyLimiter = concurrencyLimiter;
        _routeFallbackPublisher = routeFallbackPublisher;
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
            return Unauthorized(new { error = new { type = "authentication_error", message = "访问密钥无效或缺失，请在请求头中携带有效的 x-api-key 或 Authorization Bearer 令牌", code = "invalid_access_key" } });
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
            return BadRequest(new { error = new { type = "invalid_request_error", message = "请求体格式无效，请检查是否为合法的 JSON", code = "invalid_body" } });
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
            return BadRequest(new { error = new { type = "invalid_request_error", message = "请求体格式无效，请检查是否为合法的 JSON", code = "invalid_body" } });
        }

        // 验证访问密钥
        var accessKey = await ValidateAccessKeyAsync(cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { type = "authentication_error", message = "访问密钥无效或缺失，请在请求头中携带有效的 x-api-key 或 Authorization Bearer 令牌", code = "invalid_access_key" } });
        }

        // 优先读取显式来源标记，其次退回到 User-Agent 识别常见客户端工具。
        var requestSource = ResolveRequestSource(Request);
        var forwardHeaders = CollectAnthropicForwardHeaders(Request);

        // 读取运行时设置缓存，后台修改后会在短时间内刷新。
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);

        // 生成本次请求的唯一标识，供 callContext、UsageLog、ConversationLog 共用
        var requestId = Guid.NewGuid();

        // 构建统一调用上下文，整个请求链路共享同一份上下文数据
        var callContext = new ProxyCallContext
        {
            RequestId = requestId,
            AccessKeyId = accessKey.Id,
            ProtocolType = "Anthropic",
            Source = requestSource,
            RequestModel = modelName,
            ReasoningEffort = reasoningEffort,
            IsStreaming = enableStreaming,
            RequestBody = requestBody,
            RequestPath = Request.Path,
            RequestedAt = DateTimeOffset.UtcNow,
            UserAgent = Request.Headers.UserAgent.ToString(),
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            RequestHeaders = DeveloperInvocationTraceStore.CaptureHeaders(Request.Headers)
        };

        // 始终创建调用追踪记录，用于事件发布（UsageLog 等依赖 trace 完成时触发）。
        // DeveloperFeaturesEnabled 仅控制 Invocations 页面是否可见，不影响数据采集和推送。
        var traceId = _proxyCallRecorder.BeginTrace(callContext);

        // 获取已经和站点信息合并后的候选路由，优先尝试支持 Anthropic 原协议的站点。
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("Anthropic", modelName, cancellationToken);

        // AccessKey 路由限定：AllowedRouteNames 为空=允许全部，非空=只允许配置的路由入口。
        var allowedRoutes = ProxyRequestMetadataCache.GetAllowedRouteNames(accessKey);
        if (allowedRoutes is not null && allRoutes.Count > 0)
        {
            allRoutes = allRoutes.Where(r => allowedRoutes.Contains(r.ExternalModelName)).ToList();
            if (allRoutes.Count == 0)
            {
                return StatusCode(403, new { error = new { type = "permission_error", message = $"当前访问密钥无权访问路由: {modelName}", code = "route_forbidden" } });
            }
        }

        if (allRoutes.Count == 0)
        {
            return StatusCode(403, new { error = new { type = "invalid_request_error", message = $"模型 '{modelName}' 没有可用的路由，请检查路由配置或联系管理员", code = "no_available_route" } });
        }

        // 按优先级逐个尝试路由，失败则通知熔断器并继续下一个
        ProxyForwardResult? lastResult = null;
        var attemptIndex = 0;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);
        // 记录上一条失败路由信息，在下一条候选路由开始时发布回退事件
        (Guid RouteId, Guid SiteId, string SiteModelName, string? ErrorMessage)? lastFailedRoute = null;

        try
        {
        foreach (var route in allRoutes)
        {
            // 客户端已断开则不再尝试任何后续路由（无意义，响应已无法写回）。
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // 跳过已被熔断器屏蔽的路由
            if (IsRouteBlockedSafely(route.CircuitKey))
                continue;

            // 如果上一条路由已失败，此时已知下一条候选路由，发布回退事件
            if (lastFailedRoute is not null)
            {
                await SafePublishRouteFallbackAsync(
                    requestId, modelName,
                    lastFailedRoute.Value.RouteId, lastFailedRoute.Value.SiteId, lastFailedRoute.Value.SiteModelName,
                    route.RouteId, route.SiteId, route.SiteModelName,
                    lastFailedRoute.Value.ErrorMessage ?? "unknown error",
                    CancellationToken.None);
                lastFailedRoute = null;
            }

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("Anthropic");

            // 多 Key 场景：并发计数按 SiteKey 维度隔离（route.SiteKeyId），用真实站点 Id 作为调试展示身份。
            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteKeyId ?? route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken, displaySiteId: route.SiteId);

            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            // 更新统一上下文中的本次尝试级字段
            callContext.AttemptIndex = attemptIndex;
            callContext.AttemptedModel = route.UpstreamModelName;
            callContext.UpstreamProtocolType = actualProtocolType;
            callContext.ForwardingMode = ResolveForwardingMode("Anthropic", actualProtocolType);
            callContext.TargetSiteId = route.SiteId;
            callContext.TargetSiteName = route.SiteName;
            callContext.RouteId = route.RouteId;

            var traceAttemptId = _proxyCallRecorder.BeginTraceAttempt(traceId, callContext);
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Anthropic",
                actualProtocolType,
                requestBody,
                route.SiteModelName,
                enableStreaming,
                route.OverrideReasoningEffort,
                route.BaseUrl,
                route.CompatibilityRules,
                isPassthrough: string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase));

            // 如果模型配置了强制思考等级，PrepareRequestBody 已内联覆盖，同步更新日志变量
            if (!string.IsNullOrWhiteSpace(route.OverrideReasoningEffort))
            {
                reasoningEffort = route.OverrideReasoningEffort;
            }

            var effectiveProtocolType = string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                ? "OpenAI"
                : actualProtocolType;
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
                ForwardHeaders = forwardHeaders,
                TargetPath = string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                    ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses")
                    : null
            };

            // 记录预处理后的请求体到上下文
            callContext.PreparedRequestBody = preparedRequestBody;

            if (enableStreaming)
            {
                var streamOutcome = string.Equals(effectiveProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase)
                    ? await ForwardOpenAiStreamAsAnthropicAsync(
                        forwardRequest,
                        modelName,
                        callContext,
                        traceId,
                        traceAttemptId,
                        cancellationToken)
                    : await ForwardAnthropicStreamPassthroughAsync(
                        forwardRequest,
                        callContext,
                        traceId,
                        traceAttemptId,
                        cancellationToken);
                var streamResult = streamOutcome.Result;
                if (streamResult.IsCanceled)
                {
                    return new EmptyResult();
                }

                SafeWriteConsoleProxyLog("Anthropic", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);

                // 将流式结果写入统一上下文后，一次性派发到各存储
                callContext.Success = streamResult.Success;
                callContext.StatusCode = streamResult.StatusCode;
                callContext.ErrorMessage = streamResult.Success ? string.Empty : (streamResult.ErrorMessage ?? string.Empty);
                callContext.ResponseBody = streamResult.ResponseBody;
                callContext.IsStreaming = streamResult.IsStreaming;
                callContext.IsStreamInterrupted = streamResult.IsStreamInterrupted;
                callContext.InputTokens = streamResult.InputTokens;
                callContext.CachedTokens = streamResult.CachedTokens;
                callContext.OutputTokens = streamResult.OutputTokens;
                callContext.FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs;
                callContext.StreamDurationMs = streamResult.StreamDurationMs;
                callContext.TotalDurationMs = streamResult.TotalDurationMs;
                callContext.HasStartedStreaming = streamResult.HasStartedStreaming;
                callContext.RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex;
                callContext.IsFinalResult = streamResult.Success;
                callContext.FallbackTriggered = !streamResult.Success;
                await _proxyCallRecorder.RecordUsageAsync(callContext, CancellationToken.None);

                if (streamResult.Success)
                {
                    SafeSucceedRoute(route.CircuitKey);
                    return new EmptyResult();
                }

                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, streamResult);

                // 仅当未开始流式写入时才熔断该路由；若已开始写入（客户端已收到部分内容甚至终止事件），
                // 视为部分成功，不触发熔断，避免健康路由因上游偶发流中断被错误拉黑。
                if (!streamResult.HasStartedStreaming)
                {
                    SafeBlockRoute(route.CircuitKey);
                }
                lastResult = streamResult;
                lastFailedRoute = (route.RouteId, route.SiteId, route.SiteModelName, streamResult.ErrorMessage);
                if (!streamOutcome.CanFallback)
                {
                    return new EmptyResult();
                }

                continue;
            }

            // 上游请求仍使用独立超时控制；但若客户端已经主动取消，则立即结束，不再继续回退后续候选。
            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
            if (result.IsCanceled)
            {
                return new EmptyResult();
            }

            SafeWriteConsoleProxyLog("Anthropic", requestSource, modelName, actualProtocolType, preparedRequestBody, result, requestBody.Length);

            // 将非流式结果写入统一上下文后，一次性派发到各存储
            callContext.Success = result.Success;
            callContext.StatusCode = result.StatusCode;
            callContext.ErrorMessage = result.Success ? string.Empty : (result.ErrorMessage ?? string.Empty);
            callContext.ResponseBody = result.ResponseBody;
            callContext.IsStreaming = result.IsStreaming;
            callContext.IsStreamInterrupted = result.IsStreamInterrupted;
            callContext.InputTokens = result.InputTokens;
            callContext.CachedTokens = result.CachedTokens;
            callContext.OutputTokens = result.OutputTokens;
            callContext.FirstTokenLatencyMs = result.FirstTokenLatencyMs;
            callContext.StreamDurationMs = result.StreamDurationMs;
            callContext.TotalDurationMs = result.TotalDurationMs;
            callContext.HasStartedStreaming = result.HasStartedStreaming;
            callContext.RetryCount = result.Success ? attemptIndex - 1 : attemptIndex;
            callContext.IsFinalResult = result.Success;
            callContext.FallbackTriggered = !result.Success;
            await _proxyCallRecorder.RecordUsageAsync(callContext, cancellationToken);

            if (result.Success)
            {
                // 成功时清除该路由的连续失败计数
                SafeSucceedRoute(route.CircuitKey);
                if (result.IsStreaming &&
                    string.Equals(effectiveProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
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
                if (result.IsStreaming && result.HasStartedStreaming && result.IsStreamInterrupted &&
                    string.Equals(effectiveProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    responseBody = ProxyProtocolBridge.EnsureAnthropicStreamClosed(responseBody, modelName, result.InputTokens, result.CachedTokens, result.OutputTokens);
                }

                // 将适配后的响应体和内容类型写入上下文，供开发者追踪使用
                callContext.AdaptedResponseBody = responseBody;
                callContext.ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json";
                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);

                // 流式响应以 SSE 格式返回，使用 text/event-stream 内容类型
                var contentType = result.IsStreaming ? "text/event-stream" : "application/json";
                return Content(responseBody, contentType);
            }

            callContext.ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json";
            _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
            SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);

            // 转发失败，通知熔断器（达到阈值才会真正触发熔断）
            SafeBlockRoute(route.CircuitKey);
            lastResult = result;
            lastFailedRoute = (route.RouteId, route.SiteId, route.SiteModelName, result.ErrorMessage);
        }

        // 所有路由均失败
        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { type = "api_error", message = lastResult?.ErrorMessage ?? "全部上游路由均失败，请检查站点配置或联系管理员" } });
        }
        catch (OperationCanceledException)
        {
            _proxyCallRecorder.CancelTrace(traceId, "客户端已断开连接");
            throw;
        }
    }

    /// <summary>
    /// 透传 Anthropic 原生流式响应，并在透传过程中提取用量信息。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardAnthropicStreamPassthroughAsync(
        ProxyForwardRequest forwardRequest,
        ProxyCallContext callContext,
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
            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars)
            {
                responseBuilder.Append(chunk);
            }
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

        // 流中断但已开始写入时，向客户端补发 Anthropic 终止事件避免挂起
        if (result.IsStreamInterrupted && startedWriting)
        {
            try
            {
                await Response.WriteAsync("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch { /* 客户端可能已断开，忽略 */ }
        }

        if (result.Success && startedWriting)
        {
            callContext.Success = true;
            callContext.StatusCode = result.StatusCode;
            callContext.ResponseBody = result.ResponseBody;
            callContext.ResponseContentType = "text/event-stream";
            callContext.IsStreaming = true;
            callContext.InputTokens = result.InputTokens;
            callContext.CachedTokens = result.CachedTokens;
            callContext.OutputTokens = result.OutputTokens;
            callContext.TotalDurationMs = result.TotalDurationMs;
            _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
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
        ProxyCallContext callContext,
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

            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars)
            {
                responseBuilder.Append(chunk);
            }
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task FlushOpenAiSseBlockAsync(CancellationToken token)
        {
            if (string.Equals(forwardRequest.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryExtractSseEventPayload(pendingSseLines, out var responsesEventName, out var responsesPayload))
                {
                    pendingSseLines.Clear();
                    return;
                }

                pendingSseLines.Clear();
                if (string.Equals(responsesPayload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    state.ReceivedDoneEvent = true;
                    return;
                }

                var openAiSse = ProxyProtocolBridge.ConvertResponsesStreamingToChat(
                    $"event: {responsesEventName}\ndata: {responsesPayload}\n\n",
                    modelName,
                    state.InputTokens,
                    state.CachedTokens,
                    state.OutputTokens);
                if (string.IsNullOrEmpty(openAiSse))
                {
                    return;
                }

                if (!startedWriting)
                {
                    await WriteChunkAsync(ProxyProtocolBridge.BuildAnthropicStreamStart(modelName, state), token);
                }

                using var reader = new StringReader(openAiSse);
                string? line;
                var openAiSseLines = new List<string>();
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        if (TryExtractSseDataPayload(openAiSseLines, out var openAiJsonText))
                        {
                            openAiSseLines.Clear();
                            if (string.Equals(openAiJsonText, "[DONE]", StringComparison.OrdinalIgnoreCase))
                            {
                                state.ReceivedDoneEvent = true;
                                continue;
                            }

                            var convertedResponsesChunk = ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(openAiJsonText, state);
                            await WriteChunkAsync(convertedResponsesChunk, token);
                        }
                        else
                        {
                            openAiSseLines.Clear();
                        }

                        continue;
                    }

                    openAiSseLines.Add(line);
                }

                return;
            }

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
                callContext.Success = true;
                callContext.StatusCode = result.StatusCode;
                callContext.ResponseBody = result.ResponseBody;
                callContext.ResponseContentType = "text/event-stream";
                callContext.IsStreaming = true;
                callContext.InputTokens = result.InputTokens;
                callContext.CachedTokens = result.CachedTokens;
                callContext.OutputTokens = result.OutputTokens;
                callContext.TotalDurationMs = result.TotalDurationMs;
                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
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

        if (normalizedUserAgent.Contains("zcode"))
        {
            return "zcode";
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
    /// 安全地发布路由回退事件。
    /// 事件发布失败不应影响代理请求的正常回退流程。
    /// </summary>
    private async Task SafePublishRouteFallbackAsync(
        Guid requestId,
        string requestModel,
        Guid fromRouteId,
        Guid fromSiteId,
        string fromSiteModelName,
        Guid toRouteId,
        Guid toSiteId,
        string toSiteModelName,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _routeFallbackPublisher.PublishAsync(
                requestId, requestModel,
                fromRouteId, fromSiteId, fromSiteModelName,
                toRouteId, toSiteId, toSiteModelName,
                reason, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "发布路由回退事件失败，但不影响请求回退流程。RequestId={RequestId}, FromRouteId={FromRouteId}, ToRouteId={ToRouteId}",
                requestId, fromRouteId, toRouteId);
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
        // 只有异常（失败/中断）才输出到控制台，正常请求不再刷屏
        if (result.Success && !result.IsStreamInterrupted) return;

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

        if (rootElement.TryGetProperty("output_config", out var outputConfigElement) &&
            outputConfigElement.ValueKind == JsonValueKind.Object &&
            TryGetNormalizedString(outputConfigElement, "effort", out var outputConfigEffort))
        {
            return outputConfigEffort;
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

    /// <summary>
}
