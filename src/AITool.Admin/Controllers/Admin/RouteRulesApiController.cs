using System.Text.Json;
using AITool.Admin.Services;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 创建路由主入口的请求参数。
/// </summary>
public sealed class CreateRouteEntryRequest
{
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
}

/// <summary>
/// 删除路由主入口的请求参数。
/// </summary>
public sealed class DeleteRouteEntryRequest
{
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
}

/// <summary>
/// 批量保存路由规则的请求参数，按外部模型名称整体覆盖该模型下的所有规则。
/// </summary>
public sealed class SaveRouteRulesRequest
{
    /// <summary>
    /// 外部模型名称。
    /// </summary>
    public string ExternalModelName { get; set; } = string.Empty;
    /// <summary>
    /// 路由规则列表。
    /// </summary>
    public List<SaveRouteRuleEntry> Rules { get; set; } = [];
}

/// <summary>
/// 单条路由规则条目，用于保存规则时指定上游模型与目标站点的映射关系。
/// </summary>
public sealed class SaveRouteRuleEntry
{
    /// <summary>
    /// 上游模型名称。
    /// </summary>
    public string UpstreamModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 时间可用性模式，未传时默认全天可用。
    /// </summary>
    public string AvailabilityMode { get; set; } = "AllDay";
    /// <summary>
    /// 每日时间范围 JSON，未传或无效时默认不限制。
    /// </summary>
    public string TimeRangesJson { get; set; } = string.Empty;
}

/// <summary>
/// 每日时间范围配置项。
/// </summary>
public sealed class RouteTimeRange
{
    /// <summary>
    /// 开始时间，格式为 HH:mm。
    /// </summary>
    public string Start { get; set; } = string.Empty;
    /// <summary>
    /// 结束时间，格式为 HH:mm。
    /// </summary>
    public string End { get; set; } = string.Empty;
}

/// <summary>
/// 路由规则管理控制器，提供主入口和规则条目的增删改查。
/// <para>
/// 从 AITool.Web 迁移而来，适配 Admin 宿主的异步缓存失效机制。
/// Admin 侧不直接操作运行时并发限制器，保存规则后通过
/// <see cref="AdminCacheInvalidationService"/> 向 Core 宿主下发配置快照。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/route-rules")]
public sealed class RouteRulesApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 后台缓存失效服务。
    /// </summary>
    private readonly AdminCacheInvalidationService _cacheInvalidation;
    /// <summary>
    /// 后台查询元数据服务。
    /// </summary>
    private readonly AdminQueryMetadataService _adminQueryMetadataService;

    /// <summary>
    /// 创建路由规则控制器。
    /// </summary>
    public RouteRulesApiController(
        AppDbContext dbContext,
        AdminCacheInvalidationService cacheInvalidation,
        AdminQueryMetadataService adminQueryMetadataService)
    {
        _dbContext = dbContext;
        _cacheInvalidation = cacheInvalidation;
        _adminQueryMetadataService = adminQueryMetadataService;
    }

    /// <summary>
    /// 获取所有路由主入口名称，合并 ProxyRouteEntries 表和 ProxyRouteRules 表中的
    /// ExternalModelName 去重后返回。
    /// </summary>
    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(CancellationToken cancellationToken)
    {
        var result = await _adminQueryMetadataService.GetRouteEntriesAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 创建空的主入口记录。
    /// </summary>
    [HttpPost("entries")]
    public async Task<IActionResult> CreateEntry(
        [FromBody] CreateRouteEntryRequest request,
        CancellationToken cancellationToken)
    {
        var entryName = (request.EntryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entryName))
            return BadRequest(new { message = "主入口名称不能为空" });

        var existsInEntries = await _dbContext.ProxyRouteEntries
            .AnyAsync(x => x.EntryName == entryName, cancellationToken);
        var existsInRules = await _dbContext.ProxyRouteRules
            .AnyAsync(x => x.ExternalModelName == entryName, cancellationToken);
        if (existsInEntries || existsInRules)
            return BadRequest(new { message = "主入口已存在" });

        _dbContext.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = entryName
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

        return Ok(new { message = "创建成功" });
    }

    /// <summary>
    /// 删除主入口及其下所有路由规则。
    /// </summary>
    [HttpPost("entries/delete")]
    public async Task<IActionResult> DeleteEntry(
        [FromBody] DeleteRouteEntryRequest request,
        CancellationToken cancellationToken)
    {
        var entryName = (request.EntryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entryName))
            return BadRequest(new { message = "主入口名称不能为空" });

        var entry = await _dbContext.ProxyRouteEntries
            .FirstOrDefaultAsync(x => x.EntryName == entryName, cancellationToken);
        var rules = await _dbContext.ProxyRouteRules
            .Where(x => x.ExternalModelName == entryName)
            .ToListAsync(cancellationToken);

        if (entry is null && rules.Count == 0)
            return NotFound(new { message = "主入口不存在" });

        if (entry is not null)
        {
            _dbContext.ProxyRouteEntries.Remove(entry);
        }

        if (rules.Count > 0)
        {
            _dbContext.ProxyRouteRules.RemoveRange(rules);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

        return Ok(new { message = "删除成功" });
    }

    /// <summary>
    /// 获取指定模型的路由规则列表。
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> ListRules(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        var result = await _adminQueryMetadataService.GetRouteRulesAsync(modelName, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 批量保存路由规则，按外部模型名称整体覆盖该模型下的所有规则。
    /// </summary>
    [HttpPost("save")]
    public async Task<IActionResult> SaveRules(
        [FromBody] SaveRouteRulesRequest request,
        CancellationToken cancellationToken)
    {
        var entryName = (request.ExternalModelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entryName))
            return BadRequest(new { message = "模型名称不能为空" });

        // 确保主入口存在
        var existingEntry = await _dbContext.ProxyRouteEntries
            .FirstOrDefaultAsync(x => x.EntryName == entryName, cancellationToken);
        if (existingEntry is null)
        {
            _dbContext.ProxyRouteEntries.Add(new ProxyRouteEntry
            {
                EntryName = entryName
            });
        }

        // 删除该模型的所有旧规则
        var existingRules = await _dbContext.ProxyRouteRules
            .Where(r => r.ExternalModelName == entryName)
            .ToListAsync(cancellationToken);
        _dbContext.ProxyRouteRules.RemoveRange(existingRules);

        // 按列表顺序创建新规则，Priority = 全局顺序，ModelPriority/InstancePriority = 分组顺序
        var upstreamOrder = request.Rules
            .Select(r => r.UpstreamModelName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        for (int i = 0; i < request.Rules.Count; i++)
        {
            var entry = request.Rules[i];
            var normalizedUpstreamModelName = string.IsNullOrWhiteSpace(entry.UpstreamModelName)
                ? entry.SiteModelName
                : entry.UpstreamModelName.Trim();
            var sameModelEarlierCount = request.Rules
                .Take(i)
                .Count(x => string.Equals(
                    string.IsNullOrWhiteSpace(x.UpstreamModelName) ? x.SiteModelName : x.UpstreamModelName.Trim(),
                    normalizedUpstreamModelName,
                    StringComparison.Ordinal));
            var modelPriority = upstreamOrder.IndexOf(normalizedUpstreamModelName);
            if (modelPriority < 0)
            {
                modelPriority = upstreamOrder.Count;
                upstreamOrder.Add(normalizedUpstreamModelName);
            }

            var availability = NormalizeAvailability(entry.AvailabilityMode, entry.TimeRangesJson);
            _dbContext.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = entryName,
                UpstreamModelName = normalizedUpstreamModelName,
                SiteId = entry.SiteId,
                SiteModelName = entry.SiteModelName,
                Priority = i,
                ModelPriority = modelPriority,
                InstancePriority = sameModelEarlierCount,
                IsEnabled = true,
                AvailabilityMode = availability.Mode,
                TimeRangesJson = availability.TimeRangesJson
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Admin 侧不直接操作运行时并发限制器，直接刷新路由缓存即可
        await _cacheInvalidation.InvalidateAdminRouteMetadataAsync(cancellationToken);
        await _cacheInvalidation.InvalidateRuntimeRouteTargetsAsync(cancellationToken);

        return Ok(new { message = "保存成功" });
    }

    /// <summary>
    /// 切换规则启用状态。
    /// </summary>
    [HttpPost("toggle/{ruleId}")]
    public async Task<IActionResult> ToggleRule(Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.ProxyRouteRules.FindAsync([ruleId], cancellationToken);
        if (rule is null)
            return NotFound(new { message = "规则不存在" });

        rule.IsEnabled = !rule.IsEnabled;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

        return Ok(new { message = "状态已切换", isEnabled = rule.IsEnabled });
    }

    /// <summary>
    /// 获取所有已启用的站点实例，供前端路由页面的候选实例下拉列表使用。
    /// </summary>
    [HttpGet("site-instances")]
    public async Task<IActionResult> GetSiteInstances(CancellationToken cancellationToken)
    {
        var result = await _adminQueryMetadataService.GetRouteSiteInstancesAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 删除单条路由规则。
    /// </summary>
    [HttpPost("delete/{ruleId}")]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.ProxyRouteRules.FindAsync([ruleId], cancellationToken);
        if (rule is null)
            return NotFound(new { message = "规则不存在" });

        _dbContext.ProxyRouteRules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

        return Ok(new { message = "规则已删除" });
    }

    /// <summary>
    /// 规范化时间可用性模式，旧值和异常值统一按全天可用处理。
    /// </summary>
    private static string NormalizeAvailabilityMode(string? mode)
    {
        return string.Equals(mode, "AvailableOnly", StringComparison.Ordinal)
            ? "AvailableOnly"
            : string.Equals(mode, "Unavailable", StringComparison.Ordinal)
                ? "Unavailable"
                : "AllDay";
    }

    /// <summary>
    /// 规范化每日时间范围 JSON，无有效范围时返回空字符串以表示全天可用。
    /// </summary>
    private static string NormalizeTimeRangesJson(string? mode, string? timeRangesJson)
    {
        if (NormalizeAvailabilityMode(mode) == "AllDay" || string.IsNullOrWhiteSpace(timeRangesJson))
        {
            return string.Empty;
        }

        try
        {
            var ranges = JsonSerializer.Deserialize<List<RouteTimeRange>>(timeRangesJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            ranges = ranges
                .Where(x => IsValidTimeText(x.Start) && IsValidTimeText(x.End))
                .Select(x => new RouteTimeRange { Start = x.Start.Trim(), End = x.End.Trim() })
                .ToList();
            return ranges.Count == 0
                ? string.Empty
                : JsonSerializer.Serialize(ranges, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 规范化时间可用性配置，空配置和无效配置都回落为全天可用以兼容旧规则。
    /// </summary>
    private static (string Mode, string TimeRangesJson) NormalizeAvailability(string? mode, string? timeRangesJson)
    {
        var normalizedMode = NormalizeAvailabilityMode(mode);
        if (normalizedMode == "AllDay" || string.IsNullOrWhiteSpace(timeRangesJson))
        {
            return ("AllDay", string.Empty);
        }

        try
        {
            var ranges = JsonSerializer.Deserialize<List<RouteTimeRange>>(timeRangesJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            ranges = ranges
                .Where(x => IsValidTimeText(x.Start) && IsValidTimeText(x.End))
                .Select(x => new RouteTimeRange { Start = x.Start.Trim(), End = x.End.Trim() })
                .ToList();
            return ranges.Count == 0
                ? ("AllDay", string.Empty)
                : (normalizedMode, JsonSerializer.Serialize(ranges, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        catch (JsonException)
        {
            return ("AllDay", string.Empty);
        }
    }

    /// <summary>
    /// 校验 HH:mm 时间文本。
    /// </summary>
    private static bool IsValidTimeText(string? value)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", out _);
    }
}
