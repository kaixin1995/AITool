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

    public OpenAiProxyController(
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache)
    {
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
    }

    // 返回当前代理对外暴露的模型列表，供客户端按真实 OpenAI URL 拉取模型
    [HttpGet("/v1/models")]
    public async Task<IActionResult> Models(CancellationToken cancellationToken)
    {
        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        var modelIds = await _metadataCache.GetEnabledModelNamesAsync("OpenAI", cancellationToken);

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
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            modelName = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
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

        // 读取运行时设置缓存，后台修改后会在短时间内刷新。
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);

        // 获取已经和站点信息合并后的候选路由，避免 N+1 查询站点。
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        if (allRoutes.Count == 0)
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });

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
            var forwardRequest = new ProxyForwardRequest
            {
                TargetBaseUrl = route.BaseUrl,
                TargetApiKey = route.ApiKey,
                ProtocolType = "OpenAI",
                TargetModelName = route.SiteModelName,
                RequestBody = requestBody,
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
                Source = "proxy",
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
                // 流式响应以 SSE 格式返回，使用 text/event-stream 内容类型
                var contentType = result.IsStreaming ? "text/event-stream" : "application/json";
                return Content(result.ResponseBody, contentType);
            }

            // 转发失败，通知熔断器（达到阈值才会真正触发熔断）
            _circuitStore.Block(route.RouteId);
            lastResult = result;
        }

        // 所有路由均失败
        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }
}
