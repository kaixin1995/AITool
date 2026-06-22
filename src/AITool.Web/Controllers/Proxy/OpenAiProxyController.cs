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
using Microsoft.Extensions.Logging;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

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
    /// 负责提取结构化对话内容并识别会话。
    /// </summary>
    private readonly ConversationExtractionService _conversationExtractionService;
    /// <summary>
    /// 负责异步写入结构化对话记录。
    /// </summary>
    private readonly IConversationLogService _conversationLogService;
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
        IUsageLogService usageLogService,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache,
        DeveloperInvocationTraceStore traceStore,
        ConversationExtractionService conversationExtractionService,
        IConversationLogService conversationLogService,
        ModelConcurrencyLimiter concurrencyLimiter,
        ILogger<OpenAiProxyController> logger)
    {
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
        _traceStore = traceStore;
        _conversationExtractionService = conversationExtractionService;
        _conversationLogService = conversationLogService;
        _concurrencyLimiter = concurrencyLimiter;
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
            return Unauthorized(new { error = new { message = "访问密钥无效或缺失，请在请求头中携带有效的 Authorization Bearer 令牌", type = "authentication_error", code = "invalid_access_key" } });
        }

        var modelIds = await _metadataCache.GetEnabledModelNamesAsync(cancellationToken);

        // AccessKey 路由限定：只返回该密钥有权访问的路由入口。
        var allowedRoutes = ProxyRequestMetadataCache.GetAllowedRouteNames(accessKey);
        if (allowedRoutes is not null)
        {
            modelIds = modelIds.Where(m => allowedRoutes.Contains(m)).ToList();
        }

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
            return Unauthorized(new { error = new { message = "访问密钥无效或缺失，请在请求头中携带有效的 Authorization Bearer 令牌", type = "authentication_error", code = "invalid_access_key" } });
        }

        var modelIds = await _metadataCache.GetEnabledModelNamesAsync(cancellationToken);

        // 模型不存在 → 403 带明确提示（不暴露存在性，统一返回权限/可用性错误）
        if (!modelIds.Contains(modelId, StringComparer.Ordinal))
        {
            return StatusCode(403, new { error = new { message = $"模型 '{modelId}' 不存在或未启用，请检查路由配置", type = "invalid_request_error", code = "model_not_found" } });
        }

        // AccessKey 路由限定：模型存在但该密钥无权访问 → 403 明确提示权限不足。
        var allowedRoutes = ProxyRequestMetadataCache.GetAllowedRouteNames(accessKey);
        if (allowedRoutes is not null && !allowedRoutes.Contains(modelId))
        {
            return StatusCode(403, new { error = new { message = $"当前访问密钥无权访问路由: {modelId}", type = "permission_error", code = "route_forbidden" } });
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
                return BadRequest(new { error = new { message = "请求体缺少 model 字段，请指定要调用的模型名称", type = "invalid_request_error", code = "model_required" } });
            }
        }
        catch
        {
            return BadRequest(new { error = new { message = "请求体格式无效，请检查是否为合法的 JSON", type = "invalid_request_error", code = "invalid_body" } });
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
            return BadRequest(new { error = new { message = "请求体格式无效，请检查是否为合法的 JSON", type = "invalid_request_error", code = "invalid_body" } });
        }

        // 验证访问密钥
        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "访问密钥无效或缺失，请在请求头中携带有效的 Authorization Bearer 令牌", type = "authentication_error", code = "invalid_access_key" } });
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
                return BadRequest(new { error = new { message = "请求体缺少 model 字段，请指定要调用的模型名称", type = "invalid_request_error", code = "model_required" } });
            }
        }
        catch
        {
            return BadRequest(new { error = new { message = "请求体格式无效，请检查是否为合法的 JSON", type = "invalid_request_error", code = "invalid_body" } });
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
            return BadRequest(new { error = new { message = "请求体格式无效，请检查是否为合法的 JSON", type = "invalid_request_error", code = "invalid_body" } });
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
        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, routeLabel, modelName, requestBody);
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        // AccessKey 路由限定：AllowedRouteNames 为空=允许全部，非空=只允许配置的路由入口。
        // 过滤发生在并发获取之前，不影响原有模型并发限制。
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
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

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

            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken);

            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType);
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

            if (enableStreaming)
            {
                if (streamingBridgeFactory is null)
                {
                    return BadRequest(new { error = new { message = "当前接口不支持流式输出（stream=true）", type = "invalid_request_error", code = "streaming_not_supported" } });
                }

                var streamOutcome = await streamingBridgeFactory(this, forwardRequest, modelName, cancellationToken);
                var streamResult = streamOutcome.Result;
                if (streamResult.IsCanceled)
                {
                    return new EmptyResult();
                }

                SafeWriteConsoleProxyLog(routeLabel, requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);

                await SafeLogUsageAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "OpenAI",
                    ForwardingMode = ResolveForwardingMode("OpenAI", actualProtocolType),
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

            // 上游请求仍按单路由超时控制；但若客户端主动取消，则直接结束，不再继续回退后续候选。
            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
            if (result.IsCanceled)
            {
                return new EmptyResult();
            }

            SafeWriteConsoleProxyLog(routeLabel, requestSource, modelName, actualProtocolType, preparedRequestBody, result, requestBody.Length);

            await SafeLogUsageAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKey.Id,
                ProtocolType = "OpenAI",
                ForwardingMode = ResolveForwardingMode("OpenAI", actualProtocolType),
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
                var responseBody = responseFactory(result, actualProtocolType, modelName);
                await SafeLogConversationAsync(requestId, accessKey.Id, "OpenAI", requestSource, requestBody, responseBody, modelName, result.IsStreaming, "success", result.InputTokens, result.CachedTokens, result.OutputTokens, DateTimeOffset.UtcNow.AddMilliseconds(-Math.Max(0, result.TotalDurationMs)), cancellationToken);
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
                return Content(responseBody, result.IsStreaming ? "text/event-stream" : "application/json");
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

}
