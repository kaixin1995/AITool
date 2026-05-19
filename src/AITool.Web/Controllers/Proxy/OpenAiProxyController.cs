using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

/// <summary>
/// 处理 OpenAI 协议代理请求，并在需要时完成与 Anthropic 协议之间的兼容转换。
/// </summary>
[ApiController]
public sealed class OpenAiProxyController : ControllerBase
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
    /// 保存 Anthropic 转 OpenAI 流式转换过程中需要持续累积的状态。
    /// </summary>
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
        ModelConcurrencyLimiter concurrencyLimiter,
        ILogger<OpenAiProxyController> logger)
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
    /// 处理 OpenAI 聊天补全请求，并按路由配置转发到可用上游。
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

        // 优先读取显式来源标记，其次退回到 User-Agent 识别常见客户端工具。
        var requestSource = ResolveRequestSource(Request);

        // 读取运行时设置缓存，后台修改后会在短时间内刷新。
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "OpenAI", modelName, requestBody);

        // 获取已经和站点信息合并后的候选路由，优先尝试支持 OpenAI 原协议的站点。
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        if (allRoutes.Count == 0)
        {
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });
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
            var actualProtocolType = route.ResolveProtocolForClient("OpenAI");

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
                "OpenAI",
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
                RetryCount = runtimeSettings.ProxyRetryCount
            };

            if (enableStreaming)
            {
                var streamOutcome = string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                    ? await ForwardAnthropicStreamAsOpenAiAsync(forwardRequest, modelName, CancellationToken.None)
                    : await ForwardOpenAiStreamPassthroughAsync(forwardRequest, CancellationToken.None);
                var streamResult = streamOutcome.Result;
                SafeWriteConsoleProxyLog("OpenAI", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);

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

            // 每条路由使用独立超时令牌，不受客户端断连影响，保证后续路由仍能独立尝试。
            var result = await _forwardService.ForwardAsync(forwardRequest, CancellationToken.None);
            SafeWriteConsoleProxyLog("OpenAI", requestSource, modelName, actualProtocolType, preparedRequestBody, result, requestBody.Length);

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
                // 成功时清除该路由的连续失败计数
                SafeSucceedRoute(route.RouteId);
                var responseBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens);
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
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
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
                TargetApiKey = route.ApiKey,
                ProtocolType = actualProtocolType,
                TargetModelName = route.SiteModelName,
                RequestBody = requestBody,
                PreparedRequestBody = preparedRequestBody,
                EnableStreaming = enableStreaming,
                RequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount,
                TargetPath = isPassthrough ? "/v1/responses" : null
            };

            if (enableStreaming)
            {
                StreamForwardOutcome streamOutcome;
                if (isPassthrough)
                {
                    // OpenAI 上游直接透传
                    streamOutcome = await ForwardOpenAiStreamPassthroughAsync(forwardRequest, CancellationToken.None);
                }
                else
                {
                    // Anthropic 上游：流式 Anthropic → Responses
                    streamOutcome = await ForwardAnthropicStreamAsResponsesAsync(forwardRequest, modelName, CancellationToken.None);
                }

                var streamResult = streamOutcome.Result;
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

            // 非流式
            var result = await _forwardService.ForwardAsync(forwardRequest, CancellationToken.None);
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
            SafeBlockRoute(route.RouteId);
            lastResult = result;
        }

        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }

    /// <summary>
    /// 把 Anthropic 流式响应转换为 Responses API 流式事件后返回给客户端。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardAnthropicStreamAsResponsesAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        // 先走 Anthropic → OpenAI 的流式转换，收集完整响应后转为 Responses 事件
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;

        // Responses 流式状态
        var responsesState = new ChatToResponsesStreamState
        {
            Model = forwardRequest.TargetModelName
        };

        async Task FlushAnthropicSseBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseEventPayload(pendingSseLines, out var eventName, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.IsNullOrEmpty(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 直接把 Anthropic SSE 事件转为 Responses 事件
            var responsesChunk = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(eventName, payload, responsesState);
            if (!string.IsNullOrEmpty(responsesChunk))
            {
                responseBuilder.Append(responsesChunk);
                await Response.WriteAsync(responsesChunk, token);
                await Response.Body.FlushAsync(token);
                startedWriting = true;
            }

            // 提取用量
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                UpdateAnthropicUsageFromSseEvent(eventName, root, ref inputTokens, ref cachedTokens, ref outputTokens);
            }
            catch
            {
            }
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

        if (result.Success && !responsesState.Done && startedWriting)
        {
            result.Success = false;
            result.IsStreamInterrupted = true;
            result.ErrorMessage ??= "stream interrupted before response.completed";
        }

        // 控制器层检测到 Responses 转换正常完成时，清除基础设施层可能误设的中断标记
        if (responsesState.Done)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
        }

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 从 Anthropic SSE 事件中提取用量信息。
    /// </summary>
    private static void UpdateAnthropicUsageFromSseEvent(string eventName, JsonElement root, ref int inputTokens, ref int cachedTokens, ref int outputTokens)
    {
        if (string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it))
                {
                    inputTokens = it.GetInt32();
                }

                if (usage.TryGetProperty("cache_read_input_tokens", out var ct))
                {
                    cachedTokens = ct.GetInt32();
                }
            }
        }
        else if (string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("output_tokens", out var ot))
                {
                    outputTokens = ot.GetInt32();
                }
            }
        }
    }

    /// <summary>
    /// 透传 OpenAI 原生流式响应，并在透传过程中提取用量信息。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardOpenAiStreamPassthroughAsync(
        ProxyForwardRequest forwardRequest,
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
        var receivedDoneEvent = false;
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

        async Task FlushOpenAiSseBlockAsync(CancellationToken token)
        {
            if (pendingSseLines.Count == 0)
            {
                return;
            }

            if (TryExtractSseDataPayload(pendingSseLines, out var payload))
            {
                if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    receivedDoneEvent = true;
                }
                else
                {
                    UpdateOpenAiUsageFromPayload(payload, ref inputTokens, ref cachedTokens, ref outputTokens);

                    // 兼容 Responses API：上游可能以 response.completed 事件而非 [DONE] 结束流
                    if (!receivedDoneEvent)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.TryGetProperty("type", out var typeEl)
                                && string.Equals(typeEl.GetString(), "response.completed", StringComparison.OrdinalIgnoreCase))
                            {
                                receivedDoneEvent = true;
                            }
                        }
                        catch
                        {
                        }
                    }
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
        result.InputTokens = inputTokens;
        result.CachedTokens = cachedTokens;
        result.OutputTokens = outputTokens;

        if (result.Success && !receivedDoneEvent)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before [DONE]"
                : "stream ended before any complete SSE event";
        }

        // 控制器层检测到流正常结束时，清除基础设施层可能误设的中断标记
        if (receivedDoneEvent)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
        }

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 把 Anthropic 流式响应转换成 OpenAI 增量事件后返回给客户端。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardAnthropicStreamAsOpenAiAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        var state = new AnthropicToOpenAiStreamState();
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

        async Task EnsureRoleChunkAsync(CancellationToken token)
        {
            if (state.RoleChunkSent)
            {
                return;
            }

            // 先发 role chunk，确保下游按标准 OpenAI SSE 增量消费。
            await WriteChunkAsync(BuildOpenAiDeltaChunk(modelName, new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty
            }), token);
            state.RoleChunkSent = true;
        }

        async Task FlushAnthropicSseBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseEventPayload(pendingSseLines, out var eventName, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("usage", out var startUsage))
                    {
                        UpdateAnthropicUsageFromElement(startUsage, state);
                    }

                    await EnsureRoleChunkAsync(token);
                    return;
                }

                if (string.Equals(eventName, "content_block_start", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("content_block", out var contentBlock))
                {
                    var blockType = contentBlock.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : null;
                    if (string.Equals(blockType, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        var blockIndex = root.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                            ? indexElement.GetInt32()
                            : state.ToolCalls.Count;
                        if (!state.ToolCalls.TryGetValue(blockIndex, out var toolCallState))
                        {
                            toolCallState = new AnthropicToolCallState { Index = state.ToolCalls.Count };
                            state.ToolCalls[blockIndex] = toolCallState;
                        }

                        toolCallState.Id = contentBlock.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString() ?? string.Empty
                            : toolCallState.Id;
                        toolCallState.Name = contentBlock.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                            ? nameElement.GetString() ?? string.Empty
                            : toolCallState.Name;

                        await EnsureRoleChunkAsync(token);
                        await WriteChunkAsync(BuildOpenAiToolCallChunk(modelName, new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = toolCallState.Index,
                                ["id"] = toolCallState.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = toolCallState.Name,
                                    ["arguments"] = string.Empty
                                }
                            }
                        }), token);
                    }

                    return;
                }

                if (string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("delta", out var delta))
                {
                    var deltaType = delta.TryGetProperty("type", out var deltaTypeElement)
                        ? deltaTypeElement.GetString()
                        : null;
                    if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = delta.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                            ? textElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(text))
                        {
                            await EnsureRoleChunkAsync(token);
                            await WriteChunkAsync(BuildOpenAiDeltaChunk(modelName, new JsonObject
                            {
                                ["content"] = text
                            }), token);
                        }

                        return;
                    }

                    if (string.Equals(deltaType, "thinking_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var thinking = delta.TryGetProperty("thinking", out var thinkingElement) && thinkingElement.ValueKind == JsonValueKind.String
                            ? thinkingElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            await EnsureRoleChunkAsync(token);
                            await WriteChunkAsync(BuildOpenAiDeltaChunk(modelName, new JsonObject
                            {
                                ["reasoning_content"] = thinking
                            }), token);
                        }

                        return;
                    }

                    if (string.Equals(deltaType, "signature_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (string.Equals(deltaType, "input_json_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var blockIndex = root.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                            ? indexElement.GetInt32()
                            : -1;
                        var partialJson = delta.TryGetProperty("partial_json", out var partialJsonElement) && partialJsonElement.ValueKind == JsonValueKind.String
                            ? partialJsonElement.GetString()
                            : null;
                        if (blockIndex >= 0 &&
                            !string.IsNullOrEmpty(partialJson) &&
                            state.ToolCalls.TryGetValue(blockIndex, out var toolCallState))
                        {
                            await EnsureRoleChunkAsync(token);
                            await WriteChunkAsync(BuildOpenAiToolCallChunk(modelName, new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = toolCallState.Index,
                                    ["function"] = new JsonObject
                                    {
                                        ["arguments"] = partialJson
                                    }
                                }
                            }), token);
                        }

                        return;
                    }
                }

                if (string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("delta", out var messageDelta) &&
                        messageDelta.TryGetProperty("stop_reason", out var stopReasonElement) &&
                        stopReasonElement.ValueKind == JsonValueKind.String)
                    {
                        state.StopReason = stopReasonElement.GetString() ?? state.StopReason;
                    }

                    if (root.TryGetProperty("usage", out var deltaUsage))
                    {
                        UpdateAnthropicUsageFromElement(deltaUsage, state);
                    }

                    return;
                }

                if (string.Equals(eventName, "message_stop", StringComparison.OrdinalIgnoreCase))
                {
                    state.ReceivedMessageStop = true;
                    await EnsureRoleChunkAsync(token);
                    var totalInputTokens = state.InputTokens + state.CachedTokens + state.CacheCreationTokens;
                    await WriteChunkAsync(BuildOpenAiFinishChunk(
                        modelName,
                        MapAnthropicStopReason(state.StopReason),
                        totalInputTokens,
                        state.CachedTokens,
                        state.CacheCreationTokens,
                        state.OutputTokens), token);
                    await WriteChunkAsync("data: [DONE]\n\n", token);
                }
            }
            catch
            {
            }
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

        var totalPromptTokens = state.InputTokens + state.CachedTokens + state.CacheCreationTokens;
        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = totalPromptTokens;
        result.CachedTokens = state.CachedTokens;
        result.OutputTokens = state.OutputTokens;

        if (result.Success && !state.ReceivedMessageStop)
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

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
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
    /// 从 OpenAI 流式负载中刷新当前流的 token 统计。
    /// </summary>
    private static void UpdateOpenAiUsageFromPayload(string jsonText, ref int inputTokens, ref int cachedTokens, ref int outputTokens)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;
            if (!root.TryGetProperty("usage", out var usage))
            {
                return;
            }

            if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.ValueKind == JsonValueKind.Number)
            {
                inputTokens = promptTokens.GetInt32();
            }

            if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.ValueKind == JsonValueKind.Number)
            {
                outputTokens = completionTokens.GetInt32();
            }

            if (usage.TryGetProperty("prompt_tokens_details", out var promptTokenDetails) &&
                promptTokenDetails.TryGetProperty("cached_tokens", out var cachedTokenElement) &&
                cachedTokenElement.ValueKind == JsonValueKind.Number)
            {
                cachedTokens = cachedTokenElement.GetInt32();
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 从 Anthropic 用量对象中刷新当前转换状态的 token 统计。
    /// </summary>
    private static void UpdateAnthropicUsageFromElement(JsonElement usage, AnthropicToOpenAiStreamState state)
    {
        if (usage.TryGetProperty("input_tokens", out var inputTokens) && inputTokens.ValueKind == JsonValueKind.Number)
        {
            state.InputTokens = inputTokens.GetInt32();
        }

        if (usage.TryGetProperty("cache_read_input_tokens", out var cachedTokens) && cachedTokens.ValueKind == JsonValueKind.Number)
        {
            state.CachedTokens = cachedTokens.GetInt32();
        }

        if (usage.TryGetProperty("cache_creation_input_tokens", out var cacheCreationTokens) && cacheCreationTokens.ValueKind == JsonValueKind.Number)
        {
            state.CacheCreationTokens = cacheCreationTokens.GetInt32();
        }

        if (usage.TryGetProperty("output_tokens", out var outputTokens) && outputTokens.ValueKind == JsonValueKind.Number)
        {
            state.OutputTokens = outputTokens.GetInt32();
        }
    }

    /// <summary>
    /// 构造一个只包含 delta 内容的 OpenAI SSE 块。
    /// </summary>
    private static string BuildOpenAiDeltaChunk(string modelName, JsonObject deltaObject)
    {
        return BuildOpenAiChunkCore(modelName, deltaObject, null, null);
    }

    /// <summary>
    /// 构造一个包含 tool_calls 增量内容的 OpenAI SSE 块。
    /// </summary>
    private static string BuildOpenAiToolCallChunk(string modelName, JsonArray toolCalls)
    {
        return BuildOpenAiChunkCore(modelName, new JsonObject
        {
            ["tool_calls"] = toolCalls
        }, null, null);
    }

    /// <summary>
    /// 构造带有结束原因和用量统计的 OpenAI 收尾 SSE 块。
    /// </summary>
    private static string BuildOpenAiFinishChunk(
        string modelName,
        string finishReason,
        int inputTokens,
        int cachedTokens,
        int cacheCreationTokens,
        int outputTokens)
    {
        return BuildOpenAiChunkCore(
            modelName,
            new JsonObject(),
            finishReason,
            new JsonObject
            {
                ["prompt_tokens"] = inputTokens,
                ["prompt_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = cachedTokens,
                    ["cached_creation_tokens"] = cacheCreationTokens
                },
                ["completion_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens
            });
    }

    /// <summary>
    /// 按 OpenAI chat.completion.chunk 结构拼装通用 SSE 负载。
    /// </summary>
    private static string BuildOpenAiChunkCore(string modelName, JsonObject deltaObject, string? finishReason, JsonObject? usage)
    {
        var payload = new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = modelName,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = deltaObject,
                    ["finish_reason"] = finishReason is null ? null : JsonValue.Create(finishReason)
                }
            }
        };

        if (usage is not null)
        {
            payload["usage"] = usage;
        }

        return $"data: {payload.ToJsonString()}\n\n";
    }

    /// <summary>
    /// 将 Anthropic 的停止原因映射成 OpenAI 的 finish_reason。
    /// </summary>
    private static string MapAnthropicStopReason(string? stopReason)
    {
        return stopReason?.ToLowerInvariant() switch
        {
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            "stop_sequence" => "stop",
            "refusal" => "content_filter",
            _ => "stop"
        };
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
            ForwardingMode = ResolveForwardingMode("OpenAI", actualProtocolType),
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
            "OpenAI",
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
}
