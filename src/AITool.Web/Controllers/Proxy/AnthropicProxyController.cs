using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

// Anthropic 协议兼容代理控制器，转发 messages 请求并集成熔断机制
[ApiController]
public sealed class AnthropicProxyController : ControllerBase
{
    private sealed class StreamForwardOutcome
    {
        public ProxyForwardResult Result { get; init; } = new();
        public bool CanFallback { get; init; }
    }

    private readonly IProxyForwardService _forwardService;
    private readonly IUsageLogService _usageLogService;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly DeveloperInvocationTraceStore _traceStore;
    private readonly ILogger<AnthropicProxyController> _logger;

    public AnthropicProxyController(
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache,
        DeveloperInvocationTraceStore traceStore,
        ILogger<AnthropicProxyController> logger)
    {
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
        _traceStore = traceStore;
        _logger = logger;
    }

    // 兼容 Anthropic count_tokens 接口，按当前路由可解析请求格式估算 token。
    [HttpPost("/v1/messages/count_tokens")]
    public async Task<IActionResult> CountTokens(CancellationToken cancellationToken)
    {
        var accessKey = await ValidateAccessKeyAsync(cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { type = "authentication_error", message = "Invalid or missing access key" } });
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
            return BadRequest(new { error = new { type = "invalid_request_error", message = "Invalid request body" } });
        }
    }

    // 代理 Anthropic messages 请求
    [HttpPost("/v1/messages")]
    public async Task<IActionResult> Messages(CancellationToken cancellationToken)
    {
        // 读取原始请求体
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        // 解析请求中的模型名称
        string modelName;
        var enableStreaming = false;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            modelName = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
            enableStreaming = doc.RootElement.TryGetProperty("stream", out var streamValue)
                && streamValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                && streamValue.GetBoolean();
        }
        catch
        {
            return BadRequest(new { error = new { type = "invalid_request_error", message = "Invalid request body" } });
        }

        // 验证访问密钥
        var accessKey = await ValidateAccessKeyAsync(cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { type = "authentication_error", message = "Invalid or missing access key" } });
        }

        // 优先读取显式来源标记，其次退回到 User-Agent 识别常见客户端工具。
        var requestSource = ResolveRequestSource(Request);
        var forwardHeaders = CollectAnthropicForwardHeaders(Request);

        // 读取运行时设置缓存，后台修改后会在短时间内刷新。
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var traceId = TryCreateDeveloperTrace(runtimeSettings, requestSource, "Anthropic", modelName, requestBody);

        // 获取已经和站点信息合并后的候选路由，优先尝试支持 Anthropic 原协议的站点。
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("Anthropic", modelName, cancellationToken);

        if (allRoutes.Count == 0)
        {
            return NotFound(new { error = new { type = "not_found_error", message = $"No available route for model: {modelName}" } });
        }

        // 按优先级逐个尝试路由，失败则通知熔断器并继续下一个
        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;

        foreach (var route in allRoutes)
        {
            // 跳过已被熔断器屏蔽的路由
            if (_circuitStore.IsBlocked(route.RouteId))
                continue;

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("Anthropic");
            var traceAttemptId = AddDeveloperTraceAttempt(traceId, route, actualProtocolType);
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "Anthropic",
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
                RetryCount = runtimeSettings.ProxyRetryCount,
                ForwardHeaders = forwardHeaders
            };

            if (enableStreaming && string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                var streamOutcome = await ForwardOpenAiStreamAsAnthropicAsync(
                    forwardRequest,
                    modelName,
                    traceId,
                    traceAttemptId,
                    cancellationToken);
                var streamResult = streamOutcome.Result;

                await _usageLogService.LogAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "Anthropic",
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
                    IsStreaming = streamResult.IsStreaming,
                    IsStreamInterrupted = streamResult.IsStreamInterrupted,
                    FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                    StreamDurationMs = streamResult.StreamDurationMs,
                    TotalDurationMs = streamResult.TotalDurationMs
                }, cancellationToken);

                if (streamResult.Success)
                {
                    _circuitStore.Succeed(route.RouteId);
                    return new EmptyResult();
                }

                CompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
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
                LogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, streamResult);

                _circuitStore.Block(route.RouteId);
                lastResult = streamResult;
                if (!streamOutcome.CanFallback)
                {
                    return new EmptyResult();
                }

                continue;
            }

            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);

            await _usageLogService.LogAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKey.Id,
                ProtocolType = "Anthropic",
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
                TotalDurationMs = result.TotalDurationMs
            }, cancellationToken);

            if (result.Success)
            {
                // 成功时清除该路由的连续失败计数
                _circuitStore.Succeed(route.RouteId);
                if (result.IsStreaming &&
                    string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
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
                if (result.IsStreaming && result.HasStartedStreaming && result.IsStreamInterrupted && string.Equals(actualProtocolType, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    responseBody = ProxyProtocolBridge.EnsureAnthropicStreamClosed(responseBody, modelName, result.InputTokens, result.CachedTokens, result.OutputTokens);
                }
                CompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
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

            CompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
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
            LogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);

            // 转发失败，通知熔断器（达到阈值才会真正触发熔断）
            _circuitStore.Block(route.RouteId);
            lastResult = result;
        }

        // 所有路由均失败
        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { type = "api_error", message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }

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

        var state = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
        var responseBuilder = new StringBuilder();
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

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (!startedWriting)
                {
                    await WriteChunkAsync(ProxyProtocolBridge.BuildAnthropicStreamStart(modelName, state), token);
                }

                if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var jsonText = line["data: ".Length..];
                if (string.Equals(jsonText, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    state.ReceivedDoneEvent = true;
                    return;
                }

                var convertedChunk = ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(jsonText, state);
                await WriteChunkAsync(convertedChunk, token);
            },
            cancellationToken);

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
                CompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
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

    // 优先使用自定义来源头，无法识别时再退回通用 proxy。
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
            ForwardingMode = ResolveForwardingMode("Anthropic", actualProtocolType),
            TargetSiteId = route.SiteId,
            TargetSiteName = route.SiteName
        });
    }

    private void CompleteDeveloperTraceAttempt(Guid? traceId, Guid traceAttemptId, DeveloperInvocationResult result)
    {
        if (!traceId.HasValue || traceAttemptId == Guid.Empty)
        {
            return;
        }

        _traceStore.CompleteAttempt(traceId.Value, traceAttemptId, result);
    }

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

    private static string ResolveForwardingMode(string clientProtocolType, string upstreamProtocolType)
    {
        return string.Equals(clientProtocolType, upstreamProtocolType, StringComparison.OrdinalIgnoreCase)
            ? "direct"
            : "bridge";
    }

    // 兼容更多 Anthropic 客户端的鉴权写法，优先读取 x-api-key，再回退 bearer。
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

    // 透传 Anthropic 客户端特有请求头，避免能力协商信息在代理层丢失。
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

    // 这里先做近似估算，满足客户端前置探测需要，避免缺失接口直接报错。
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
}
