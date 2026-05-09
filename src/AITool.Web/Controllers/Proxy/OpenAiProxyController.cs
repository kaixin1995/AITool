using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

// OpenAI 协议兼容代理控制器，转发 chat completions 请求并集成熔断机制
[ApiController]
public sealed class OpenAiProxyController : ControllerBase
{
    private readonly IProxyForwardService _forwardService;
    private readonly IUsageLogService _usageLogService;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly DeveloperInvocationTraceStore _traceStore;

    public OpenAiProxyController(
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache,
        DeveloperInvocationTraceStore traceStore)
    {
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
        _traceStore = traceStore;
    }

    // 返回当前代理对外暴露的模型列表，兼容 OpenAI 和 Anthropic 客户端拉取模型。
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

    // 代理 OpenAI chat completions 请求，自动跳过熔断站点
    [HttpPost("/v1/chat/completions")]
    public async Task<IActionResult> ChatCompletions(CancellationToken cancellationToken)
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
        var traceId = TryCreateDeveloperTrace(runtimeSettings, requestSource, "OpenAI", modelName, requestBody);

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

        foreach (var route in allRoutes)
        {
            // 跳过已被熔断器屏蔽的路由
            if (_circuitStore.IsBlocked(route.RouteId))
                continue;

            attemptIndex++;
            var traceAttemptId = AddDeveloperTraceAttempt(traceId, route);
            var actualProtocolType = route.ResolveProtocolForClient("OpenAI");
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

            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);

            await _usageLogService.LogAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKey.Id,
                ProtocolType = "OpenAI",
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
                var responseBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens);
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

            // 转发失败，通知熔断器（达到阈值才会真正触发熔断）
            _circuitStore.Block(route.RouteId);
            lastResult = result;
        }

        // 所有路由均失败
        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
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

    private Guid AddDeveloperTraceAttempt(Guid? traceId, CachedProxyRouteTarget route)
    {
        if (!traceId.HasValue)
        {
            return Guid.Empty;
        }

        return _traceStore.AddAttempt(traceId.Value, new DeveloperInvocationAttempt
        {
            AttemptedModel = route.UpstreamModelName,
            UpstreamProtocolType = route.ProtocolType,
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
}
