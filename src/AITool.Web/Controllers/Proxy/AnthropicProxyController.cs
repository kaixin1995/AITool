using System.Text;
using System.Text.Json;
using AITool.Application.Google;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AITool.Protocol;
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
    /// 负责在 Codex 上游凭证失效时即时刷新 access token。
    /// </summary>
    private readonly CodexCredentialRefreshService _codexCredentialRefreshService;
    /// <summary>
    /// 负责在 Google 上游（Antigravity）凭证失效时即时刷新 access token。
    /// </summary>
    private readonly GoogleCredentialRefreshService _googleCredentialRefreshService;
    /// <summary>
    /// 负责在 Kimi 上游凭证失效时即时刷新 access token。
    /// </summary>
    private readonly KimiCredentialRefreshService _kimiCredentialRefreshService;
    /// <summary>
    /// 记录代理请求诊断转储与对比样本。
    /// </summary>
    private readonly IProxyDiagnosticService _diagnosticService;
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
        CodexCredentialRefreshService codexCredentialRefreshService,
        GoogleCredentialRefreshService googleCredentialRefreshService,
        KimiCredentialRefreshService kimiCredentialRefreshService,
        IProxyDiagnosticService diagnosticService,
        ILogger<AnthropicProxyController> logger)
    {
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
        _traceStore = traceStore;
        _concurrencyLimiter = concurrencyLimiter;
        _codexCredentialRefreshService = codexCredentialRefreshService;
        _googleCredentialRefreshService = googleCredentialRefreshService;
        _kimiCredentialRefreshService = kimiCredentialRefreshService;
        _diagnosticService = diagnosticService;
        _logger = logger;
    }

    /// <summary>
    /// 仅为托管隐藏站点绑定实时凭证刷新回调（Codex / Google / Kimi），普通站点的 401 不触发 OAuth 刷新。
    /// </summary>
    private Func<string, CancellationToken, Task<string?>>? CreateCredentialRefreshCallback(
        CachedProxyRouteTarget route)
    {
        if (string.Equals(route.ManagedSource, "Codex", StringComparison.OrdinalIgnoreCase))
        {
            return (staleToken, cancellationToken) => _codexCredentialRefreshService.RefreshAsync(
                route.SiteId,
                staleToken,
                cancellationToken);
        }

        if (string.Equals(route.ManagedSource, "Google", StringComparison.OrdinalIgnoreCase))
        {
            return (staleToken, cancellationToken) => _googleCredentialRefreshService.RefreshAsync(
                route.SiteId,
                staleToken,
                cancellationToken);
        }

        if (string.Equals(route.ManagedSource, "kimi_oauth", StringComparison.OrdinalIgnoreCase))
        {
            return (staleToken, cancellationToken) => _kimiCredentialRefreshService.RefreshAsync(
                route.SiteId,
                staleToken,
                cancellationToken);
        }

        return null;
    }

    private Func<CancellationToken, Task>? CreateCredentialDisableCallback(
        CachedProxyRouteTarget route)
    {
        if (string.Equals(route.ManagedSource, "Google", StringComparison.OrdinalIgnoreCase))
        {
            return cancellationToken => _googleCredentialRefreshService.DisableAsync(
                route.SiteId,
                "proxy-403",
                cancellationToken);
        }

        return null;
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
        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "Anthropic", modelName, requestBody);

        // 获取已经和站点信息合并后的候选路由，优先尝试支持 Anthropic 原协议的站点。
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("Anthropic", modelName, cancellationToken);

        // AccessKey 路由限定：AllowedRouteNames 为空=允许全部，非空=只允许配置的路由入口。
        var allowedRoutes = ProxyRequestMetadataCache.GetAllowedRouteNames(accessKey);
        if (allowedRoutes is not null && allRoutes.Count > 0)
        {
            allRoutes = allRoutes.Where(r => allowedRoutes.Contains(r.ExternalModelName)).ToList();
            if (allRoutes.Count == 0)
            {
                return StatusCode(403, new { error = new { type = "permission_error", message = $"当前访问密钥无权访问路由: {modelName}" } });
            }
        }

        if (allRoutes.Count == 0)
        {
            return StatusCode(403, new { error = new { type = "invalid_request_error", message = $"模型 '{modelName}' 没有可用的路由，请检查路由配置或联系管理员", code = "no_available_route" } });
        }

        // 按优先级逐个尝试路由，失败则通知熔断器并继续下一个
        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var routeIndex = -1;
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        foreach (var route in allRoutes)
        {
            routeIndex++;
            // 客户端已断开则不再尝试任何后续路由（无意义，响应已无法写回）。
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // 跳过已被熔断器屏蔽的路由
            if (IsRouteBlockedSafely(route.CircuitKey))
                continue;

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("Anthropic");

            // 按站点+模型粒度获取并发许可，根据配置决定跳过或排队。
            using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
                HttpContext.RequestServices, route.SiteKeyId ?? route.SiteId, route.SiteModelName,
                concurrencyMode, concurrencyQueueTimeout, cancellationToken, displaySiteId: route.SiteId);

            if (!concurrencyHandle.Acquired)
            {
                continue;
            }

            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Anthropic",
                actualProtocolType,
                requestBody,
                route.SiteModelName,
                enableStreaming,
                route.OverrideReasoningEffort,
                route.BaseUrl,
                route.CompatibilityRules,
                isPassthrough: string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase),
                geminiProjectId: route.GoogleProjectId);

            var effectiveProtocolType = actualProtocolType switch
            {
                var protocol when string.Equals(protocol, "Responses", StringComparison.OrdinalIgnoreCase) => "OpenAI",
                var protocol when string.Equals(protocol, "Gemini", StringComparison.OrdinalIgnoreCase) => "Gemini",
                var protocol => protocol
            };
            var effectiveForwardHeaders = forwardHeaders;
            var isAntigravity = ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl);
            var effectiveEmulation = !string.IsNullOrWhiteSpace(route.ClientEmulation) && !string.Equals(route.ClientEmulation, Domain.Sites.ClientEmulationConstants.None, StringComparison.OrdinalIgnoreCase)
                ? route.ClientEmulation
                : (string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                    ? Domain.Sites.ClientEmulationConstants.Antigravity
                    : Domain.Sites.ClientEmulationConstants.None);

            if (!string.Equals(effectiveEmulation, Domain.Sites.ClientEmulationConstants.None, StringComparison.OrdinalIgnoreCase) ||
                (route.ExtraHeaders != null && route.ExtraHeaders.Count > 0) ||
                string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                var resolvedHeaders = ClientEmulationEngine.ResolveHeaders(
                    effectiveEmulation,
                    route.ExtraHeaders,
                    route.SiteModelName,
                    route.GoogleProjectId,
                    isAntigravity);

                if (string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveForwardHeaders = resolvedHeaders;
                }
                else
                {
                    effectiveForwardHeaders = new Dictionary<string, string>(forwardHeaders, StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in resolvedHeaders)
                    {
                        effectiveForwardHeaders[k] = v;
                    }
                }
            }

            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType, preparedRequestBody, effectiveForwardHeaders);

            // 如果模型配置了强制思考等级，PrepareRequestBody 已内联覆盖，同步更新日志变量
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
                    RateLimitRetryCount = runtimeSettings.RateLimitRetryCount,
                ForwardHeaders = effectiveForwardHeaders,
                EgressProxyUrl = route.EgressProxyUrl,
                RefreshTargetApiKeyAsync = CreateCredentialRefreshCallback(route),
                DisableTargetCredentialAsync = CreateCredentialDisableCallback(route),
                TargetPath = string.Equals(actualProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                    ? (enableStreaming ? "/v1internal:streamGenerateContent?alt=sse" : "/v1internal:generateContent")
                    : string.Equals(actualProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                        ? SiteEndpointPathResolver.ResolvePath(route.EndpointPathMode, "responses")
                        : null
            };

            if (enableStreaming)
            {
                var streamOutcome = string.Equals(effectiveProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase)
                    ? await ForwardOpenAiStreamAsAnthropicAsync(
                        forwardRequest,
                        modelName,
                        traceId,
                        traceAttemptId,
                        cancellationToken)
                    : string.Equals(effectiveProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                        ? await ForwardGeminiStreamAsAnthropicAsync(
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
                if (streamResult.IsCanceled)
                {
                    return new EmptyResult();
                }

                SafeRecordProxyDiagnostic("Anthropic", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, streamResult, requestId, traceId);
                var streamCanFallback = !streamResult.Success
                    && streamOutcome.CanFallback
                    && allRoutes.Skip(routeIndex + 1).Any(candidate => !IsRouteBlockedSafely(candidate.CircuitKey));

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
                    IsFinalResult = streamResult.Success || !streamCanFallback,
                    FallbackTriggered = streamCanFallback,
                    ErrorMessage = streamResult.Success ? string.Empty : (streamResult.ErrorMessage ?? string.Empty),
                    HttpStatusCode = streamResult.StatusCode > 0 ? streamResult.StatusCode : null,
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    IsStreaming = streamResult.IsStreaming,
                    IsStreamInterrupted = streamResult.IsStreamInterrupted,
                    FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                    StreamDurationMs = streamResult.StreamDurationMs,
                    TotalDurationMs = streamResult.TotalDurationMs,
                    ReasoningEffort = reasoningEffort,
                    RequestedAt = DateTimeOffset.UtcNow
                }, CancellationToken.None);

                if (streamResult.Success)
                {
                    SafeSucceedRoute(route.CircuitKey);
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

            // 上游请求仍使用独立超时控制；但若客户端已经主动取消，则立即结束，不再继续回退后续候选。
            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);
            if (result.IsCanceled)
            {
                return new EmptyResult();
            }

            SafeRecordProxyDiagnostic("Anthropic", requestSource, modelName, route, actualProtocolType, requestBody, preparedRequestBody, result, requestId, traceId);
            var canFallback = allRoutes.Skip(routeIndex + 1).Any(candidate => !IsRouteBlockedSafely(candidate.CircuitKey));

            // 协议转换先行：转换失败必须在写日志之前置为失败，保证该次尝试按 fail 入账。
            // 否则会出现"日志已记成功、客户端却收到 502"，以及 fallback 成功后第一次尝试的真实
            // token 消耗在 Analytics 按 RequestId 取最终行时被漏掉的口径错位。
            string? responseBody = null;
            if (result.Success)
            {
                responseBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "Anthropic",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens);

                if (string.IsNullOrEmpty(responseBody))
                {
                    // 转换失败不能伪装成成功响应，也不能把空消息返回给客户端。
                    result.Success = false;
                    result.ErrorMessage ??= "upstream response protocol conversion failed";
                }
            }

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
                // 成功时清除该路由的连续失败计数
                SafeSucceedRoute(route.CircuitKey);
                if (result.IsStreaming &&
                    string.Equals(effectiveProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                    HttpContext.Response.HasStarted)
                {
                    return new EmptyResult();
                }

                if (result.IsStreaming && result.HasStartedStreaming && result.IsStreamInterrupted &&
                    string.Equals(effectiveProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    responseBody = ProxyProtocolBridge.EnsureAnthropicStreamClosed(responseBody!, modelName, result.InputTokens, result.CachedTokens, result.OutputTokens);
                }
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "success",
                    StatusCode = result.StatusCode,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(responseBody!),
                    ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json",
                    IsStreaming = result.IsStreaming,
                    InputTokens = result.InputTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.OutputTokens,
                    TotalDurationMs = result.TotalDurationMs
                });
                // 流式响应以 SSE 格式返回，使用 text/event-stream 内容类型
                var contentType = result.IsStreaming ? "text/event-stream" : "application/json";
                return Content(responseBody!, contentType);
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

            // 转发或转换失败，通知熔断器（达到阈值才会真正触发熔断）
            SafeBlockRoute(route.CircuitKey, new CircuitRouteMeta(route.SiteName, route.SiteModelName));
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
            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.Append(chunk); }
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
                    // 累积原始 Anthropic 正文，不受 64KB 诊断副本限制。
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

        // Responses 上游的逐事件直转状态（不经 Chat 中转，保住 reasoning/function_call/document 语义）。
        // state 必须取直转状态的 Core：块管理、usage 与收尾事件共享同一实例。
        var responsesToAnthropicState = new ProxyProtocolBridge.ResponsesToAnthropicStreamState
        {
            Model = modelName
        };
        var state = responsesToAnthropicState.Core;
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;

        async Task WriteChunkAsync(string chunk, CancellationToken token)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.Append(chunk); }
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

                // Responses → Anthropic 逐事件直转；只有真正产生 Anthropic 事件时才发送 message_start，
                // 保留尚未写出时的 fallback 能力。收尾事件由流结束后的 CompleteAnthropicStream 统一补齐。
                var responsesConvertedChunk = ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic(responsesEventName, responsesPayload, responsesToAnthropicState);
                if (!string.IsNullOrEmpty(responsesConvertedChunk))
                {
                    if (!startedWriting)
                    {
                        await WriteChunkAsync(ProxyProtocolBridge.BuildAnthropicStreamStart(modelName, state), token);
                    }

                    await WriteChunkAsync(responsesConvertedChunk, token);
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

            // 只有转换器实际产生 Anthropic 事件时才发送 message_start，避免 role-only、usage-only
            // 或无法识别的 chunk 被包装成空响应并阻断 fallback。
            var convertedChunk = ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(jsonText, state);
            if (!string.IsNullOrEmpty(convertedChunk))
            {
                if (!startedWriting)
                {
                    await WriteChunkAsync(ProxyProtocolBridge.BuildAnthropicStreamStart(modelName, state), token);
                }

                await WriteChunkAsync(convertedChunk, token);
            }
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

        if (result.Success && responsesToAnthropicState.Failed)
        {
            // Responses 上游以 response.failed/error 终态收尾：已写出按中断处理，未写出按失败允许回退。
            result.Success = false;
            result.ErrorMessage ??= startedWriting ? "stream interrupted by response.failed" : "upstream responses stream failed";
            result.IsStreamInterrupted = startedWriting;
        }

        if (result.Success && state.ConversionFailed && !startedWriting)
        {
            result.Success = false;
            result.ErrorMessage ??= "upstream response protocol conversion failed";
        }

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
            // 日志的 CachedTokens 是"读+写"合并口径（与 ExtractUsageFromElement 一致）；
            // 客户端出口的 message_delta 由 CompleteAnthropicStream 按读/写分桶，互不影响。
            result.CachedTokens = state.CachedTokens + state.CacheCreationTokens;
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
            // 同成功路径：日志缓存列按"读+写"合并口径。
            result.CachedTokens = state.CachedTokens + state.CacheCreationTokens;
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
            // 统一显式来源的大小写，确保写入、展示和筛选使用同一口径。
            return explicitSource.ToLowerInvariant();
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

        if (normalizedUserAgent.Contains("deepseek-harness"))
        {
            return "deepseek-harness";
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

                // Anthropic 的 input_tokens 已含缓存（cache_read + cache_creation 是其子集），
                // 必须减去缓存才是"新输入"，否则缓存会在输入列与缓存列重复统计，总 token 虚高。
                if (startUsage.TryGetProperty("cache_read_input_tokens", out var startCached) && startCached.ValueKind == JsonValueKind.Number)
                {
                    cachedTokens = startCached.GetInt32();
                }

                if (startUsage.TryGetProperty("cache_creation_input_tokens", out var startCreated) && startCreated.ValueKind == JsonValueKind.Number)
                {
                    cachedTokens += startCreated.GetInt32();
                }

                inputTokens = Math.Max(0, inputTokens - cachedTokens);

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

                    // usage 字段是累计值（不是增量）：delta 重复携带累计 usage 时用覆盖语义，
                    // 否则缓存会被重复累加（与 OpenAiProxyController.UpdateAnthropicUsageFromElement 保持一致）。
                    if (deltaUsage.TryGetProperty("cache_read_input_tokens", out var deltaCached) && deltaCached.ValueKind == JsonValueKind.Number)
                    {
                        cachedTokens = deltaCached.GetInt32();
                    }

                    if (deltaUsage.TryGetProperty("cache_creation_input_tokens", out var deltaCreated) && deltaCreated.ValueKind == JsonValueKind.Number)
                    {
                        cachedTokens += deltaCreated.GetInt32();
                    }

                    // 减法只在该事件自带 input_tokens 时执行（官方 message_delta 通常只有 output_tokens），
                    // 否则会对已减去缓存的新输入再次减法，导致输入被重复扣减。
                    if (deltaUsage.TryGetProperty("input_tokens", out var _))
                    {
                        inputTokens = Math.Max(0, inputTokens - cachedTokens);
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
    /// <param name="traceId">调用追踪 Id（未启用追踪时为 null）。</param>
    /// <param name="route">本次尝试命中的路由目标。</param>
    /// <param name="actualProtocolType">实际发给上游的协议类型（可能因桥接与入口不同）。</param>
    /// <param name="preparedRequestBody">转换后实际发给上游的请求体，用于在调用追踪页排查上游参数错误。</param>
    /// <param name="preparedRequestHeaders">重写/模拟后实际发给上游的请求头。</param>
    private Guid AddDeveloperTraceAttempt(
        Guid? traceId,
        CachedProxyRouteTarget route,
        string actualProtocolType,
        string preparedRequestBody,
        IReadOnlyDictionary<string, string>? preparedRequestHeaders = null)
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
            TargetSiteName = route.SiteName,
            PreparedRequestBody = preparedRequestBody,
            PreparedRequestHeaders = preparedRequestHeaders
        });
    }

    /// <summary>
    /// 安全地记录一次路由尝试，避免追踪异常中断主流程。
    /// </summary>
    private Guid AddDeveloperTraceAttemptSafely(
        Guid? traceId,
        CachedProxyRouteTarget route,
        string actualProtocolType,
        string preparedRequestBody,
        IReadOnlyDictionary<string, string>? preparedRequestHeaders = null)
    {
        try
        {
            return AddDeveloperTraceAttempt(traceId, route, actualProtocolType, preparedRequestBody, preparedRequestHeaders);
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
    private void SafeBlockRoute(Guid routeId, CircuitRouteMeta? meta = null)
    {
        try
        {
            _circuitStore.Block(routeId, meta);
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
    /// 统一记录代理请求诊断信息（失败自动落盘独立复现文件与 error.log，成功采样对比样本）。
    /// </summary>
    private void SafeRecordProxyDiagnostic(
        string clientProtocol,
        string requestSource,
        string modelName,
        CachedProxyRouteTarget route,
        string actualProtocolType,
        string rawRequestBody,
        string preparedRequestBody,
        ProxyForwardResult result,
        Guid requestId,
        Guid? traceId)
    {
        try
        {
            var forwardingMode = ResolveForwardingMode(clientProtocol, actualProtocolType);
            var context = new ProxyDiagnosticContext
            {
                RequestId = requestId,
                TraceId = traceId,
                ClientProtocol = clientProtocol,
                RequestSource = requestSource,
                ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                UserAgent = Request.Headers.UserAgent.ToString(),
                RequestPath = Request.Path,
                RouteName = modelName,
                TargetSiteId = route.SiteId,
                TargetSiteName = route.SiteName,
                TargetBaseUrl = route.BaseUrl,
                RequestModel = modelName,
                AttemptedModel = route.UpstreamModelName,
                UpstreamProtocol = actualProtocolType,
                ForwardingMode = forwardingMode,
                ClientHeaders = ProxyDiagnosticContext.SnapshotHeaders(Request.Headers),
                RawClientRequestBody = rawRequestBody,
                PreparedRequestBody = preparedRequestBody,
                Result = result
            };

            _diagnosticService.RecordDiagnostic(context);

            SafeWriteConsoleProxyLog(
                clientProtocol,
                requestSource,
                modelName,
                actualProtocolType,
                preparedRequestBody,
                result,
                rawRequestBody.Length,
                route.SiteName,
                forwardingMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行代理请求诊断记录异常: Route={Route}, Site={Site}", modelName, route.SiteName);
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
        int requestBodyLength,
        string? siteName = null,
        string? forwardingMode = null)
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
                result.ResponseBody?.Length ?? 0,
                siteName,
                forwardingMode));
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
        var modeLabel = string.Equals(ResolveForwardingMode("Anthropic", actualProtocolType), "direct", StringComparison.OrdinalIgnoreCase)
            ? "直接透传 (direct)"
            : "兼容转换 (bridge)";

        _logger.LogError(
            "代理请求失败\nSource={Source}\nClientProtocol={ClientProtocol}\nUpstreamProtocol={UpstreamProtocol}\nForwardingMode={ForwardingMode}\nRequestModel={RequestModel}\nAttemptedModel={AttemptedModel}\nSiteName={SiteName}\nSiteId={SiteId}\nBaseUrl={BaseUrl}\nStatusCode={StatusCode}\nIsStreaming={IsStreaming}\nIsStreamInterrupted={IsStreamInterrupted}\nErrorMessage={ErrorMessage}\nRequestBody={RequestBody}\nResponseBody={ResponseBody}",
            requestSource,
            "Anthropic",
            actualProtocolType,
            modeLabel,
            modelName,
            route.UpstreamModelName,
            route.SiteName,
            route.SiteId,
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
    /// 把 Gemini 上游（Antigravity）流式响应转换为 Anthropic SSE 返回给客户端。
    /// 转换核心在 AITool.Protocol 的 Gemini→Anthropic 状态机，本方法负责流读取、写入与结果判定。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardGeminiStreamAsAnthropicAsync(
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

        var state = new ProxyProtocolBridge.GeminiToAnthropicStreamState();
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;

        async Task WriteChunkAsync(string chunk, CancellationToken token)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.Append(chunk); }
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task FlushGeminiBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseDataPayload(pendingSseLines, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var convertedChunk = ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(payload, modelName, state);
            await WriteChunkAsync(convertedChunk, token);
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushGeminiBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushGeminiBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = state.InputTokens;
        result.CachedTokens = state.CachedTokens;
        result.OutputTokens = state.OutputTokens;

        // Gemini 流没有 [DONE] 标记：finishReason 出现即视为正常完成，收尾事件由状态机统一补齐。
        if (result.Success && state.FinishReason is null)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before finishReason"
                : "stream ended before any gemini candidate";
        }

        if (result.Success)
        {
            await WriteChunkAsync(ProxyProtocolBridge.CompleteGeminiToAnthropicStream(state), cancellationToken);
            result.ResponseBody = responseBuilder.ToString();
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;

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
            // 已写出部分内容：补发收尾事件，客户端不至于挂起；不能再 fallback。
            await WriteChunkAsync(ProxyProtocolBridge.CompleteGeminiToAnthropicStream(state), CancellationToken.None);
            result.ResponseBody = responseBuilder.ToString();
            result.IsStreamInterrupted = true;
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }
}
