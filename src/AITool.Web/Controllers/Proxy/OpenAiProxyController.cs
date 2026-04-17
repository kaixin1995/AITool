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

        // 收集被熔断的站点，选择路由时跳过
        var excludedSiteIds = GetBlockedSiteIds();
        var routeResult = await _routeService.SelectRouteAsync(modelName, excludedSiteIds, cancellationToken);
        if (!routeResult.Found || routeResult.Route is null)
        {
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });
        }

        var route = routeResult.Route;

        // 获取目标站点信息
        var site = await _dbContext.Sites.FindAsync([route.SiteId], cancellationToken);
        if (site is null || !site.IsEnabled)
        {
            return NotFound(new { error = new { message = "Target site not available" } });
        }

        // 转发请求
        var forwardRequest = new ProxyForwardRequest
        {
            TargetBaseUrl = site.BaseUrl,
            TargetApiKey = site.ApiKey,
            ProtocolType = "OpenAI",
            TargetModelName = route.SiteModelName,
            RequestBody = requestBody
        };

        var result = await _forwardService.ForwardAsync(forwardRequest, cancellationToken);

        // 转发失败时触发熔断
        if (!result.Success)
        {
            _circuitStore.Block(site.Id);
        }

        // 记录使用日志
        await _usageLogService.LogAsync(new UsageLogEntry
        {
            AccessKeyId = accessKey.Id,
            ProtocolType = "OpenAI",
            RequestModel = modelName,
            TargetSiteId = site.Id,
            Status = result.Success ? "success" : "fail",
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens
        }, cancellationToken);

        if (!result.Success)
        {
            return StatusCode(result.StatusCode > 0 ? result.StatusCode : 502,
                new { error = new { message = result.ErrorMessage ?? "Upstream request failed" } });
        }

        return Content(result.ResponseBody, "application/json");
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
