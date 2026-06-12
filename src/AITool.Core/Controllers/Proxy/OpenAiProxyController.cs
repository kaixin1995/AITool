using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AITool.Core.Services;

namespace AITool.Core.Controllers.Proxy;

/// <summary>
/// 处理 OpenAI 协议代理请求，并在需要时完成与 Anthropic 协议之间的兼容转换。
/// </summary>
[ApiController]
public sealed partial class OpenAiProxyController : ControllerBase
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
        /// <summary>
        /// 保存本轮流式响应在 response.completed 中返回的 output 数组，供 Responses WebSocket 会话续传时合并上下文。
        /// </summary>
        public string CompletedOutputJson { get; init; } = "[]";
    }

    /// <summary>
    /// 保存 Anthropic 转 OpenAI 流式转换过程中需要持续累积的状态。
    /// </summary>
    private sealed class ResponsesWebSocketSessionState
    {
        /// <summary>
        /// 保存上一轮归一化后的 Responses 请求，供 response.append 合并上下文时复用。
        /// </summary>
        public string LastRequestJson { get; set; } = string.Empty;
        /// <summary>
        /// 保存上一轮 response.completed 里的 output 数组 JSON。
        /// </summary>
        public string LastResponseOutputJson { get; set; } = "[]";
    }

    private sealed class AnthropicToOpenAiStreamState
    {
        /// <summary>
        /// 标记是否已经向客户端发出 assistant 角色的首个增量块。
        /// </summary>
        public bool RoleChunkSent { get; set; }
        /// <summary>
        /// 标记是否已经收到 Anthropic 的 message_stop 事件。
        /// </summary>
        public bool ReceivedMessageStop { get; set; }
        /// <summary>
        /// 保存 Anthropic 返回的结束原因。
        /// </summary>
        public string StopReason { get; set; } = "stop";
        /// <summary>
        /// 保存输入 token 数。
        /// </summary>
        public int InputTokens { get; set; }
        /// <summary>
        /// 保存命中缓存的输入 token 数。
        /// </summary>
        public int CachedTokens { get; set; }
        /// <summary>
        /// 保存新建缓存占用的输入 token 数。
        /// </summary>
        public int CacheCreationTokens { get; set; }
        /// <summary>
        /// 保存输出 token 数。
        /// </summary>
        public int OutputTokens { get; set; }
        /// <summary>
        /// 按内容块索引保存正在拼装的工具调用状态。
        /// </summary>
        public Dictionary<int, AnthropicToolCallState> ToolCalls { get; } = [];
    }

    /// <summary>
    /// 保存单个 Anthropic 工具调用块转换成 OpenAI tool_call 所需的状态。
    /// </summary>
    private sealed class AnthropicToolCallState
    {
        /// <summary>
        /// 保存当前工具调用在 OpenAI 响应中的索引。
        /// </summary>
        public int Index { get; init; }
        /// <summary>
        /// 保存工具调用标识。
        /// </summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>
        /// 保存工具名称。
        /// </summary>
        public string Name { get; set; } = string.Empty;
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
    /// 负责在路由回退时发布 route-fallback 事件。
    /// </summary>
    private readonly CoreRouteFallbackEventPublisher _routeFallbackPublisher;
    /// <summary>
    /// 模型并发限制器，按站点+模型粒度控制最大并发请求数。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    /// <summary>
    /// 记录代理过程中的诊断日志。
    /// </summary>
    private readonly ILogger<OpenAiProxyController> _logger;

    /// <summary>
    /// 初始化 OpenAI 代理控制器依赖。
    /// </summary>
    public OpenAiProxyController(
        IProxyForwardService forwardService,
        IProxyCallRecorder proxyCallRecorder,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache,
        ModelConcurrencyLimiter concurrencyLimiter,
        CoreRouteFallbackEventPublisher routeFallbackPublisher,
        ILogger<OpenAiProxyController> logger)
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
    /// 返回当前代理可用的模型列表，并兼容 OpenAI 与 Anthropic 客户端的展示格式。
    /// </summary>
    [HttpGet("/v1/models")]
    public async Task<IActionResult> Models(CancellationToken cancellationToken)
    {
        var isAnthropicClient = Request.Headers.ContainsKey("x-api-key")
            || Request.Headers.ContainsKey("anthropic-version");

        string accessToken;
        if (isAnthropicClient)
        {
            accessToken = Request.Headers.TryGetValue("x-api-key", out var keyHeader)
                ? keyHeader.ToString()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                var anthropicAuthHeader = Request.Headers.Authorization.ToString();
                accessToken = anthropicAuthHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? anthropicAuthHeader[7..]
                    : string.Empty;
            }
        }
        else
        {
            var authHeader = Request.Headers.Authorization.ToString();
            accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader[7..]
                : string.Empty;
        }

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        var modelIds = await _metadataCache.GetEnabledModelNamesAsync(cancellationToken);

        if (isAnthropicClient)
        {
            return Ok(new
            {
                data = modelIds.Select(modelId => new
                {
                    type = "model",
                    id = modelId,
                    display_name = modelId,
                    created_at = DateTimeOffset.UtcNow.ToString("O")
                }),
                has_more = false,
                first_id = modelIds.FirstOrDefault(),
                last_id = modelIds.LastOrDefault()
            });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Ok(new
        {
            @object = "list",
            data = modelIds.Select(modelId => new
            {
                id = modelId,
                @object = "model",
                created = now,
                owned_by = "aitool"
            })
        });
    }

    /// <summary>
    /// 返回指定模型详情，并兼容 OpenAI 与 Anthropic 客户端的返回格式。
    /// </summary>
    [HttpGet("/v1/models/{modelId}")]
    public async Task<IActionResult> ModelDetail(string modelId, CancellationToken cancellationToken)
    {
        var isAnthropicClient = Request.Headers.ContainsKey("x-api-key")
            || Request.Headers.ContainsKey("anthropic-version");

        string accessToken;
        if (isAnthropicClient)
        {
            accessToken = Request.Headers.TryGetValue("x-api-key", out var keyHeader)
                ? keyHeader.ToString()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                var anthropicAuthHeader = Request.Headers.Authorization.ToString();
                accessToken = anthropicAuthHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? anthropicAuthHeader[7..]
                    : string.Empty;
            }
        }
        else
        {
            var authHeader = Request.Headers.Authorization.ToString();
            accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader[7..]
                : string.Empty;
        }

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        var modelIds = await _metadataCache.GetEnabledModelNamesAsync(cancellationToken);
        if (!modelIds.Contains(modelId, StringComparer.Ordinal))
        {
            return NotFound(new { error = new { message = $"The model '{modelId}' does not exist", type = "invalid_request_error", param = "model", code = "model_not_found" } });
        }

        if (isAnthropicClient)
        {
            return Ok(new
            {
                type = "model",
                id = modelId,
                display_name = modelId,
                created_at = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        return Ok(new
        {
            id = modelId,
            @object = "model",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            owned_by = "aitool"
        });
    }

    /// <summary>
    /// 处理 OpenAI Completions 请求，并复用 Chat Completions 代理链路。
    /// </summary>
    [HttpPost("/v1/completions")]
    public async Task<IActionResult> Completions(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            var root = document.RootElement;
            if (!root.TryGetProperty("model", out var modelElement) || string.IsNullOrWhiteSpace(modelElement.GetString()))
            {
                return BadRequest(new { error = new { message = "Invalid request body: model is required" } });
            }
        }
        catch
        {
            return BadRequest(new { error = new { message = "Invalid request body" } });
        }

        var chatRequestBody = ProxyProtocolBridge.ConvertCompletionsRequestToChat(requestBody);
        return await ProcessOpenAiLikeRequestAsync(
            routeLabel: "Completions",
            requestBody: requestBody,
            preparedClientRequestBody: chatRequestBody,
            requestPath: "/v1/completions",
            responseFactory: static (result, actualProtocolType, modelName) =>
            {
                var chatBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens);
                return ProxyProtocolBridge.ConvertChatResponseToCompletions(chatBody);
            },
            streamingBridgeFactory: static (controller, forwardRequest, modelName, _) =>
                string.Equals(forwardRequest.ProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                    ? controller.ForwardAnthropicStreamAsCompletionsAsync(forwardRequest, modelName, CancellationToken.None)
                    : controller.ForwardOpenAiStreamAsCompletionsAsync(forwardRequest, modelName, CancellationToken.None),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 处理 OpenAI Chat Completions 请求，并按路由配置转发到可用上游。
    /// </summary>
    [HttpPost("/v1/chat/completions")]
    public async Task<IActionResult> ChatCompletions(CancellationToken cancellationToken)
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
            return BadRequest(new { error = new { message = "Invalid request body" } });
        }

        // 验证访问密钥
        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        return await ProcessOpenAiLikeRequestAsync(
            routeLabel: "ChatCompletions",
            requestBody: requestBody,
            preparedClientRequestBody: requestBody,
            requestPath: "/v1/chat/completions",
            responseFactory: static (result, actualProtocolType, modelName) =>
                ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens),
            streamingBridgeFactory: static (controller, forwardRequest, modelName, _) =>
                string.Equals(forwardRequest.ProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                    ? controller.ForwardAnthropicStreamAsOpenAiAsync(forwardRequest, modelName, CancellationToken.None)
                    : string.Equals(forwardRequest.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                        ? controller.ForwardResponsesStreamAsOpenAiAsync(forwardRequest, modelName, CancellationToken.None)
                        : controller.ForwardOpenAiStreamPassthroughAsync(forwardRequest, CancellationToken.None),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 处理 OpenAI Embeddings 请求，并复用通用 OpenAI 代理链路。
    /// </summary>
    [HttpPost("/v1/embeddings")]
    public async Task<IActionResult> Embeddings(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (!document.RootElement.TryGetProperty("model", out var modelElement) || string.IsNullOrWhiteSpace(modelElement.GetString()))
            {
                return BadRequest(new { error = new { message = "Invalid request body: model is required" } });
            }
        }
        catch
        {
            return BadRequest(new { error = new { message = "Invalid request body" } });
        }

        return await ProcessOpenAiLikeRequestAsync(
            routeLabel: "Embeddings",
            requestBody: requestBody,
            preparedClientRequestBody: requestBody,
            requestPath: "/v1/embeddings",
            responseFactory: static (result, actualProtocolType, modelName) =>
                ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens),
            streamingBridgeFactory: null,
            cancellationToken: cancellationToken,
            allowStreaming: false,
            defaultTargetPathFactory: static route => SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "embeddings"),
            routeEligibility: static (_, actualProtocolType) => string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 处理 OpenAI Responses Compact 请求，并复用 Responses 代理链路。
    /// </summary>
    [HttpPost("/v1/responses/compact")]
    public async Task<IActionResult> ResponsesCompact(CancellationToken cancellationToken)
    {
        return await Responses(cancellationToken);
    }

    /// <summary>
    /// 统一处理 OpenAI 风格请求，并根据路由能力自动选择直连或 Anthropic 兼容中转。
    /// </summary>
    private async Task<IActionResult> ProcessOpenAiLikeRequestAsync(
        string routeLabel,
        string requestBody,
        string preparedClientRequestBody,
        string requestPath,
        Func<ProxyForwardResult, string, string, string> responseFactory,
        Func<OpenAiProxyController, ProxyForwardRequest, string, CancellationToken, Task<StreamForwardOutcome>>? streamingBridgeFactory,
        CancellationToken cancellationToken,
        bool allowStreaming = true,
        Func<CachedProxyRouteTarget, string>? defaultTargetPathFactory = null,
        Func<CachedProxyRouteTarget, string, bool>? routeEligibility = null)
    {
        string modelName;
        var enableStreaming = false;
        var reasoningEffort = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(preparedClientRequestBody);
            modelName = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
            enableStreaming = allowStreaming
                && doc.RootElement.TryGetProperty("stream", out var streamValue)
                && streamValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                && streamValue.GetBoolean();
            reasoningEffort = ResolveReasoningEffort(doc.RootElement);
        }
        catch
        {
            return BadRequest(new { error = new { message = "Invalid request body" } });
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
            RequestPath = requestPath,
            RequestedAt = DateTimeOffset.UtcNow,
            UserAgent = Request.Headers.UserAgent.ToString(),
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            RequestHeaders = DeveloperInvocationTraceStore.CaptureHeaders(Request.Headers)
        };

        // 通过统一记录服务创建开发者追踪（仅在开发者功能启用时生效）
        var traceId = runtimeSettings.DeveloperFeaturesEnabled
            ? _proxyCallRecorder.BeginTrace(callContext)
            : null;

        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        if (allRoutes.Count == 0)
        {
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });
        }

        ProxyForwardResult? lastResult = null;
        var attemptIndex = 0;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        // 记录上一轮失败的路由信息，在回退到下一条路由时发布 route-fallback 事件
        (Guid RouteId, Guid SiteId, string SiteModelName, string? ErrorMessage)? lastFailedRoute = null;

        foreach (var route in allRoutes)
        {
            if (IsRouteBlockedSafely(route.RouteId))
                continue;

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("OpenAI");
            if (routeEligibility is not null && !routeEligibility(route, actualProtocolType))
            {
                continue;
            }

            // 如果前一条路由失败且当前有可用的候选路由，发布回退事件
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

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken);

            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            // 更新统一上下文中的本次尝试级字段
            callContext.AttemptIndex = attemptIndex;
            callContext.AttemptedModel = route.UpstreamModelName;
            callContext.UpstreamProtocolType = actualProtocolType;
            callContext.ForwardingMode = ResolveForwardingMode("OpenAI", actualProtocolType);
            callContext.TargetSiteId = route.SiteId;
            callContext.TargetSiteName = route.SiteName;
            callContext.RouteId = route.RouteId;

            var traceAttemptId = _proxyCallRecorder.BeginTraceAttempt(traceId, callContext);
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "OpenAI",
                actualProtocolType,
                preparedClientRequestBody,
                route.SiteModelName,
                enableStreaming);

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
                TargetPath = defaultTargetPathFactory is null
                    ? (string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                        ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses")
                        : null)
                    : defaultTargetPathFactory(route)
            };

            // 记录预处理后的请求体到上下文
            callContext.PreparedRequestBody = preparedRequestBody;

            if (enableStreaming)
            {
                if (streamingBridgeFactory is null)
                {
                    return BadRequest(new { error = new { message = "Streaming is not supported for this endpoint" } });
                }

                var streamOutcome = await streamingBridgeFactory(this, forwardRequest, modelName, cancellationToken);
                var streamResult = streamOutcome.Result;
                if (streamResult.IsCanceled)
                {
                    return new EmptyResult();
                }

                SafeWriteConsoleProxyLog(routeLabel, requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);

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
                    await _proxyCallRecorder.RecordConversationAsync(callContext, CancellationToken.None);
                    SafeSucceedRoute(route.RouteId);
                    _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                    return new EmptyResult();
                }

                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, streamResult);
                SafeBlockRoute(route.RouteId);
                lastFailedRoute = (route.RouteId, route.SiteId, route.SiteModelName, streamResult.ErrorMessage);
                lastResult = streamResult;
                if (!streamOutcome.CanFallback)
                {
                    return new EmptyResult();
                }

                continue;
            }

            // 上游请求仍按单路由超时控制；但若客户端主动取消，则直接结束，不再继续回退后续候选。
            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
            if (result.IsCanceled)
            {
                return new EmptyResult();
            }

            SafeWriteConsoleProxyLog(routeLabel, requestSource, modelName, actualProtocolType, preparedRequestBody, result, requestBody.Length);

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
                SafeSucceedRoute(route.RouteId);
                var responseBody = responseFactory(result, actualProtocolType, modelName);
                await _proxyCallRecorder.RecordConversationAsync(callContext, cancellationToken);

                // 将适配后的响应体和内容类型写入上下文，供开发者追踪使用
                callContext.AdaptedResponseBody = responseBody;
                callContext.ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json";
                _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
                return Content(responseBody, result.IsStreaming ? "text/event-stream" : "application/json");
            }

            callContext.ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json";
            _proxyCallRecorder.CompleteTraceAttempt(traceId, traceAttemptId, callContext);
            SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);
            SafeBlockRoute(route.RouteId);
            lastFailedRoute = (route.RouteId, route.SiteId, route.SiteModelName, result.ErrorMessage);
            lastResult = result;
        }

        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }

}
