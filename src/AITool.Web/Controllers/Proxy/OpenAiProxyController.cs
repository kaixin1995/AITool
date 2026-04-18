using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.Routing;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Proxy;

// OpenAI 协议兼容代理控制器，转发 chat completions 请求并集成熔断机制
[ApiController]
public sealed class OpenAiProxyController : ControllerBase
{
    private readonly IRouteSelectionService _routeService;
    private readonly IProxyForwardService _forwardService;
    private readonly IUsageLogService _usageLogService;
    private readonly AppDbContext _dbContext;
    private readonly RouteCircuitStateStore _circuitStore;

    public OpenAiProxyController(
        IRouteSelectionService routeService,
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        AppDbContext dbContext,
        RouteCircuitStateStore circuitStore)
    {
        _routeService = routeService;
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _dbContext = dbContext;
        _circuitStore = circuitStore;
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

        var accessKey = await ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        // 收集被熔断的站点，获取所有启用的路由规则
        var blockedSiteIds = GetBlockedSiteIds();
        var allRoutes = await _routeService.SelectAllRoutesAsync(modelName, cancellationToken);

        if (allRoutes.Count == 0)
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });

        // 按优先级逐个尝试路由，失败则熔断该站点并继续下一个
        ProxyForwardResult? lastResult = null;
        int retryCount = 0;

        foreach (var routeResult in allRoutes)
        {
            var route = routeResult.Route!;
            if (blockedSiteIds.Contains(route.SiteId))
                continue;

            var site = await _dbContext.Sites.FindAsync([route.SiteId], cancellationToken);
            if (site is null || !site.IsEnabled)
                continue;

            var forwardRequest = new ProxyForwardRequest
            {
                TargetBaseUrl = site.BaseUrl,
                TargetApiKey = site.ApiKey,
                ProtocolType = "OpenAI",
                TargetModelName = route.SiteModelName,
                RequestBody = requestBody
            };

            var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);

            if (result.Success)
            {
                // 记录成功的调用日志（含重试次数）
                await _usageLogService.LogAsync(new UsageLogEntry
                {
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "OpenAI",
                    RequestModel = modelName,
                    TargetSiteId = site.Id,
                    Status = "success",
                    Source = "proxy",
                    RetryCount = retryCount,
                    InputTokens = result.InputTokens,
                    OutputTokens = result.OutputTokens
                }, cancellationToken);
                // 成功时清除该站点的连续失败计数
                _circuitStore.Succeed(site.Id);
                return Content(result.ResponseBody, "application/json");
            }

            // 转发失败，熔断该站点并继续尝试下一个
            _circuitStore.Block(site.Id);
            blockedSiteIds.Add(site.Id);
            lastResult = result;
            retryCount++;
        }

        // 所有路由均失败，记录最终失败的日志
        await _usageLogService.LogAsync(new UsageLogEntry
        {
            AccessKeyId = accessKey.Id,
            ProtocolType = "OpenAI",
            RequestModel = modelName,
            Status = "fail",
            Source = "proxy",
            RetryCount = retryCount
        }, cancellationToken);

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

    // 查询当前所有启用的站点，返回其中被熔断的站点 ID 集合
    private HashSet<Guid> GetBlockedSiteIds()
    {
        return _dbContext.Sites
            .Where(s => s.IsEnabled && _circuitStore.IsBlocked(s.Id))
            .Select(s => s.Id)
            .ToHashSet();
    }
}
