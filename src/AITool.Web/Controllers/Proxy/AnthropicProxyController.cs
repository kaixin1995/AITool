using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Application.Routing;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Proxy;

// Anthropic 协议兼容代理控制器，转发 messages 请求并集成熔断机制
[ApiController]
public sealed class AnthropicProxyController : ControllerBase
{
    private readonly IRouteSelectionService _routeService;
    private readonly IProxyForwardService _forwardService;
    private readonly IUsageLogService _usageLogService;
    private readonly AppDbContext _dbContext;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;

    public AnthropicProxyController(
        IRouteSelectionService routeService,
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        AppDbContext dbContext,
        RouteCircuitStateStore circuitStore,
        ISystemRuntimeSettingsService systemRuntimeSettingsService)
    {
        _routeService = routeService;
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _dbContext = dbContext;
        _circuitStore = circuitStore;
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
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
        var accessToken = Request.Headers.TryGetValue("x-api-key", out var keyHeader)
            ? keyHeader.ToString()
            : string.Empty;

        var accessKey = await ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        // 读取当前持久化运行时设置，确保后台修改可立即影响代理执行
        var runtimeSettings = await _systemRuntimeSettingsService.GetOrCreateAsync(cancellationToken);

        // 获取所有启用的路由规则，逐个尝试直到成功
        var allRoutes = await _routeService.SelectAllRoutesAsync(modelName, cancellationToken);

        if (allRoutes.Count == 0)
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });

        // 按优先级逐个尝试路由，失败则通知熔断器并继续下一个
        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;

        foreach (var routeResult in allRoutes)
        {
            var route = routeResult.Route!;
            // 跳过已被熔断器屏蔽的路由
            if (_circuitStore.IsBlocked(route.Id))
                continue;

            var site = await _dbContext.Sites.FindAsync([route.SiteId], cancellationToken);
            if (site is null || !site.IsEnabled)
                continue;

            attemptIndex++;
            var forwardRequest = new ProxyForwardRequest
            {
                TargetBaseUrl = site.BaseUrl,
                TargetApiKey = site.ApiKey,
                ProtocolType = "Anthropic",
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
                ProtocolType = "Anthropic",
                RequestModel = modelName,
                AttemptedModel = route.UpstreamModelName,
                TargetSiteId = site.Id,
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
                FirstTokenLatencyMs = result.FirstTokenLatencyMs,
                StreamDurationMs = result.StreamDurationMs,
                TotalDurationMs = result.TotalDurationMs
            }, cancellationToken);

            if (result.Success)
            {
                // 成功时清除该路由的连续失败计数
                _circuitStore.Succeed(route.Id);
                // 流式响应以 SSE 格式返回，使用 text/event-stream 内容类型
                var contentType = result.IsStreaming ? "text/event-stream" : "application/json";
                return Content(result.ResponseBody, contentType);
            }

            // 转发失败，通知熔断器（达到阈值才会真正触发熔断）
            _circuitStore.Block(route.Id);
            lastResult = result;
        }

        // 所有路由均失败
        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }

    // 验证访问密钥，比对 SHA256 哈希值
    private async Task<Domain.Proxy.ProxyAccessKey?> ValidateAccessKeyAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(accessToken)) return null;

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(accessToken));
        var hash = Convert.ToHexString(hashBytes);

        return await _dbContext.ProxyAccessKeys
            .FirstOrDefaultAsync(k => k.AccessKeyHash == hash && k.IsEnabled, cancellationToken);
    }
}
