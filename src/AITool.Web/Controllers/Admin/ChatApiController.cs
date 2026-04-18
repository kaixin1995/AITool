using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.Routing;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 对话测试用的模型列表项
public sealed class ChatModelItem
{
    // 模型ID
    public Guid ModelId { get; set; }
    // 模型显示名
    public string DisplayName { get; set; } = string.Empty;
    // 关联可用站点数
    public int AvailableSiteCount { get; set; }
}

// 发送消息的请求体
public sealed class ChatSendRequest
{
    // 选择的模型ID
    public Guid ModelId { get; set; }
    // 用户消息内容
    public string Message { get; set; } = string.Empty;
}

// 发送消息的返回结果
public sealed class ChatSendResult
{
    // 是否成功
    public bool Success { get; set; }
    // AI 回复内容
    public string Content { get; set; } = string.Empty;
    // 错误信息
    public string? Error { get; set; }
    // 请求耗时（毫秒）
    public long DurationMs { get; set; }
}

// 对话测试 API，提供模型选择和消息发送功能，支持路由规则失败重试
[ApiController]
[Route("api/admin/chat")]
public sealed class ChatApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IProxyForwardService _forwardService;
    private readonly IRouteSelectionService _routeService;
    private readonly RouteCircuitStateStore _circuitStore;

    public ChatApiController(
        AppDbContext dbContext,
        IProxyForwardService forwardService,
        IRouteSelectionService routeService,
        RouteCircuitStateStore circuitStore)
    {
        _dbContext = dbContext;
        _forwardService = forwardService;
        _routeService = routeService;
        _circuitStore = circuitStore;
    }

    // 获取可用于对话测试的模型列表
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        // 查询有可用映射的启用模型
        var enabledMappings = await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled)
            .ToListAsync(cancellationToken);

        var modelIds = enabledMappings.Select(m => m.ModelLibraryItemId).Distinct().ToList();

        var models = await _dbContext.ModelLibraryItems
            .Where(m => modelIds.Contains(m.Id) && m.IsEnabled)
            .OrderBy(m => m.DisplayName)
            .Select(m => new ChatModelItem
            {
                ModelId = m.Id,
                DisplayName = m.DisplayName
            })
            .ToListAsync(cancellationToken);

        // 填充每个模型的可用站点数
        var siteCounts = enabledMappings
            .GroupBy(m => m.ModelLibraryItemId)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var model in models)
        {
            model.AvailableSiteCount = siteCounts.GetValueOrDefault(model.ModelId);
        }

        return Ok(models);
    }

    // 发送消息到指定模型，按路由规则优先级逐个尝试，全部失败才返回错误
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] ChatSendRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "消息不能为空" });

        if (request.ModelId == Guid.Empty)
            return BadRequest(new { message = "请选择模型" });

        var sw = Stopwatch.StartNew();

        // 查找模型信息
        var model = await _dbContext.ModelLibraryItems
            .FirstOrDefaultAsync(m => m.Id == request.ModelId, cancellationToken);

        if (model is null || !model.IsEnabled)
            return Ok(new ChatSendResult { Success = false, Error = "模型不存在或已禁用" });

        // 尝试通过路由规则发送（支持失败重试）
        var allRoutes = await _routeService.SelectAllRoutesAsync(model.ModelName, cancellationToken);

        if (allRoutes.Count > 0)
        {
            // 收集被熔断的站点（先加载到内存再过滤）
            var allSiteIds = await _dbContext.Sites
                .Where(s => s.IsEnabled)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
            var blockedSiteIds = new HashSet<Guid>(
                allSiteIds.Where(id => _circuitStore.IsBlocked(id)));

            foreach (var routeResult in allRoutes)
            {
                var route = routeResult.Route!;
                if (blockedSiteIds.Contains(route.SiteId))
                    continue;

                var site = await _dbContext.Sites.FindAsync([route.SiteId], cancellationToken);
                if (site is null || !site.IsEnabled)
                    continue;

                var requestBody = JsonSerializer.Serialize(new
                {
                    model = route.SiteModelName,
                    messages = new[]
                    {
                        new { role = "user", content = request.Message }
                    },
                    stream = false,
                    max_tokens = 4096
                });

                var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
                {
                    TargetBaseUrl = site.BaseUrl,
                    TargetApiKey = site.ApiKey,
                    ProtocolType = site.ProtocolType,
                    TargetModelName = route.SiteModelName,
                    RequestBody = requestBody
                }, cancellationToken);

                if (forwardResult.Success)
                {
                    sw.Stop();
                    var content = ExtractContent(forwardResult.ResponseBody, site.ProtocolType);
                    return Ok(new ChatSendResult
                    {
                        Success = true,
                        Content = content,
                        DurationMs = sw.ElapsedMilliseconds
                    });
                }

                // 转发失败，熔断该站点并继续尝试下一个
                _circuitStore.Block(site.Id);
                blockedSiteIds.Add(site.Id);
            }

            sw.Stop();
            return Ok(new ChatSendResult
            {
                Success = false,
                Error = "所有路由站点均请求失败",
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        // 没有路由规则时，回退到旧的 SiteModelMapping 直接查询逻辑
        return await SendFallback(request, model, sw, cancellationToken);
    }

    // 回退逻辑：没有路由规则时直接通过站点映射发送
    private async Task<IActionResult> SendFallback(
        ChatSendRequest request, Domain.Models.ModelLibraryItem model,
        Stopwatch sw, CancellationToken cancellationToken)
    {
        var mapping = await _dbContext.SiteModelMappings
            .FirstOrDefaultAsync(m => m.ModelLibraryItemId == request.ModelId && m.IsEnabled, cancellationToken);

        if (mapping is null)
            return Ok(new ChatSendResult { Success = false, Error = "该模型没有可用的站点映射" });

        var site = await _dbContext.Sites.FindAsync([mapping.SiteId], cancellationToken);
        if (site is null || !site.IsEnabled)
            return Ok(new ChatSendResult { Success = false, Error = "目标站点不可用" });

        var requestBody = JsonSerializer.Serialize(new
        {
            model = mapping.RemoteModelName,
            messages = new[]
            {
                new { role = "user", content = request.Message }
            },
            stream = false,
            max_tokens = 4096
        });

        var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = site.BaseUrl,
            TargetApiKey = site.ApiKey,
            ProtocolType = site.ProtocolType,
            TargetModelName = mapping.RemoteModelName,
            RequestBody = requestBody
        }, cancellationToken);

        sw.Stop();

        if (!forwardResult.Success)
        {
            return Ok(new ChatSendResult
            {
                Success = false,
                Error = forwardResult.ErrorMessage ?? "上游请求失败",
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var content = ExtractContent(forwardResult.ResponseBody, site.ProtocolType);

        return Ok(new ChatSendResult
        {
            Success = true,
            Content = content,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    // 从上游响应 JSON 中提取 AI 回复文本
    private static string ExtractContent(string responseBody, string protocolType)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (protocolType == "Anthropic")
            {
                // Anthropic 格式：content[0].text
                if (root.TryGetProperty("content", out var contentArr) && contentArr.GetArrayLength() > 0)
                {
                    var first = contentArr[0];
                    if (first.TryGetProperty("text", out var text))
                        return text.GetString() ?? string.Empty;
                }
            }
            else
            {
                // OpenAI 格式：choices[0].message.content
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content))
                        return content.GetString() ?? string.Empty;
                }
            }
        }
        catch
        {
            // JSON 解析失败时返回原始响应
        }

        return responseBody;
    }
}
