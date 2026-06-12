using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 路由规则配置页中展示的模型项，包含该模型的站点数量和路由配置状态。
/// </summary>
public sealed class RouteModelItem
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 站点数量。
    /// </summary>
    public int SiteCount { get; set; }
    /// <summary>
    /// 是否已配置路由规则。
    /// </summary>
    public bool HasRouteRules { get; set; }
}

/// <summary>
/// 路由主入口列表项，展示入口名称和其下的候选实例数量。
/// </summary>
public sealed class RouteEntryListItem
{
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
    /// <summary>
    /// 候选实例数量。
    /// </summary>
    public int CandidateCount { get; set; }
}

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
/// 可选站点实例项，用于路由规则配置页中展示可绑定的站点映射。
/// </summary>
public sealed class SiteInstanceItem
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 站点是否启用，用于在管理页面区分已禁用站点。
    /// </summary>
    public bool SiteEnabled { get; set; } = true;
}

/// <summary>
/// 按模型名称发现的可用站点项，展示站点信息和远端模型名称。
/// </summary>
public sealed class DiscoveredSiteItem
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 远端模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点是否启用。
    /// </summary>
    public bool SiteEnabled { get; set; }
}

/// <summary>
/// 路由规则列表项，展示单条规则的详细信息，包括站点、模型、优先级和启用状态。
/// </summary>
public sealed class RouteRuleListItem
{
    /// <summary>
    /// 规则标识。
    /// </summary>
    public Guid RuleId { get; set; }
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 上游模型名称。
    /// </summary>
    public string UpstreamModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 全局优先级。
    /// </summary>
    public int Priority { get; set; }
    /// <summary>
    /// 模型优先级。
    /// </summary>
    public int ModelPriority { get; set; }
    /// <summary>
    /// 实例优先级。
    /// </summary>
    public int InstancePriority { get; set; }
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
    /// <summary>
    /// 站点是否启用，用于在管理页面区分已禁用站点。
    /// </summary>
    public bool SiteEnabled { get; set; } = true;
    /// <summary>
    /// 时间可用性模式，旧规则为空时按全天可用处理。
    /// </summary>
    public string AvailabilityMode { get; set; } = "AllDay";
    /// <summary>
    /// 每日时间范围 JSON，空值表示不限制。
    /// </summary>
    public string TimeRangesJson { get; set; } = string.Empty;
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
/// 路由规则管理控制器，提供主入口、站点实例和规则条目的增删改查。
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
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>
    /// 模型并发限制器，用于判断路由保存时是否存在正在调用的模型实例。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;

    /// <summary>
    /// 创建路由规则控制器。
    /// </summary>
    public RouteRulesApiController(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        ModelConcurrencyLimiter concurrencyLimiter)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _concurrencyLimiter = concurrencyLimiter;
    }

    /// <summary>
    /// 获取主入口列表。
    /// </summary>
    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(CancellationToken cancellationToken)
    {
        var result = await _metadataCache.GetRouteEntriesAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 创建主入口。
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
        _metadataCache.InvalidateRouteTargets();

        return Ok(new { message = "创建成功" });
    }

    /// <summary>
    /// 删除主入口。
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
        _metadataCache.InvalidateRouteTargets();

        return Ok(new { message = "删除成功" });
    }

    /// <summary>
    /// 获取可选站点实例。
    /// </summary>
    [HttpGet("site-instances")]
    public async Task<IActionResult> GetSiteInstances(CancellationToken cancellationToken)
    {
        var result = await _metadataCache.GetRouteSiteInstancesAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 获取可配置路由的模型。
    /// </summary>
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        var models = await _metadataCache.GetRouteModelsAsync(cancellationToken);
        return Ok(models);
    }

    /// <summary>
    /// 按模型发现可用站点。
    /// </summary>
    [HttpGet("discover-sites")]
    public async Task<IActionResult> DiscoverSites(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        var results = await _metadataCache.GetDiscoveredSitesAsync(modelName, cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// 获取路由规则列表。
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> ListRules(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        var result = await _metadataCache.GetRouteRulesAsync(modelName, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 保存路由规则。
    /// </summary>
    [HttpPost("save")]
    public async Task<IActionResult> SaveRules(
        [FromBody] SaveRouteRulesRequest request,
        CancellationToken cancellationToken)
    {
        var entryName = (request.ExternalModelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entryName))
            return BadRequest(new { message = "模型名称不能为空" });

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
        var previousRouteTargets = await LoadPreviousRouteTargetsAsync(existingRules, cancellationToken);
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

        var affectedRouteTargets = BuildAffectedRouteTargets(existingRules, request.Rules);

        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateAdminRouteMetadata();
        var routeRefreshDeferred = _concurrencyLimiter.TryDeferRuntimeRouteTargetsRefresh(entryName, affectedRouteTargets, previousRouteTargets);
        if (!routeRefreshDeferred)
        {
            _metadataCache.InvalidateRuntimeRouteTargets();
        }

        return Ok(new { message = routeRefreshDeferred ? "保存成功，调用中的模型会在当前请求结束后生效" : "保存成功" });
    }

    /// <summary>
    /// 将旧规则转换成运行时路由快照，活跃调用期间继续沿用该快照。
    /// </summary>
    private async Task<IReadOnlyList<CachedProxyRouteTarget>> LoadPreviousRouteTargetsAsync(
        IReadOnlyCollection<ProxyRouteRule> existingRules,
        CancellationToken cancellationToken)
    {
        if (existingRules.Count == 0)
        {
            return [];
        }

        var siteIds = existingRules.Select(x => x.SiteId).Distinct().ToList();
        var sites = await _dbContext.Sites
            .AsNoTracking()
            .Where(x => siteIds.Contains(x.Id) && x.IsEnabled)
            .ToDictionaryAsync(x => x.Id, x => x, cancellationToken);

        return existingRules
            .Where(x => x.IsEnabled && sites.ContainsKey(x.SiteId))
            .Select(x =>
            {
                var site = sites[x.SiteId];
                return new CachedProxyRouteTarget
                {
                    RouteId = x.Id,
                    SiteId = site.Id,
                    SiteName = site.Name,
                    ProtocolType = ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic),
                    EndpointPathMode = site.EndpointPathMode,
                    SupportsOpenAi = site.SupportsOpenAi,
                    SupportsAnthropic = site.SupportsAnthropic,
                    ExternalModelName = x.ExternalModelName,
                    UpstreamModelName = x.UpstreamModelName,
                    SiteModelName = x.SiteModelName,
                    BaseUrl = site.BaseUrl,
                    ApiKey = site.ApiKey,
                    ModelPriority = x.ModelPriority,
                    InstancePriority = x.InstancePriority,
                    Priority = x.Priority,
                    AvailabilityMode = x.AvailabilityMode,
                    TimeRangesJson = x.TimeRangesJson
                };
            })
            .OrderBy(x => x.ModelPriority)
            .ThenBy(x => x.InstancePriority)
            .ThenBy(x => x.Priority)
            .ToList();
    }

    /// <summary>
    /// 根据站点能力推导协议类型。
    /// </summary>
    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        if (!supportsOpenAi && !supportsAnthropic)
        {
            return "Responses";
        }

        return supportsOpenAi || !supportsAnthropic ? "OpenAI" : "Anthropic";
    }

    /// <summary>
    /// 规范化时间可用性配置，空配置和无效配置都回落为全天可用以兼容旧规则。
    /// </summary>
    private static (string Mode, string TimeRangesJson) NormalizeAvailability(string? mode, string? timeRangesJson)
    {
        var normalizedMode = string.Equals(mode, "AvailableOnly", StringComparison.Ordinal)
            ? "AvailableOnly"
            : string.Equals(mode, "Unavailable", StringComparison.Ordinal)
                ? "Unavailable"
                : "AllDay";
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
    /// 校验 HH:mm 时间文本，避免无效配置影响运行时选路。
    /// </summary>
    private static bool IsValidTimeText(string? value)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", out _);
    }

    /// <summary>
    /// 收集当前保存影响到的站点模型，命中活跃调用时运行时路由顺序需要延迟刷新。
    /// </summary>
    private static IReadOnlyCollection<RouteTargetIdentity> BuildAffectedRouteTargets(
        IReadOnlyCollection<ProxyRouteRule> existingRules,
        IReadOnlyCollection<SaveRouteRuleEntry> newRules)
    {
        return existingRules
            .Select(x => new RouteTargetIdentity(x.SiteId, x.SiteModelName))
            .Concat(newRules.Select(x => new RouteTargetIdentity(x.SiteId, x.SiteModelName)))
            .ToHashSet(RouteTargetIdentityComparer.Instance);
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
        _metadataCache.InvalidateRouteTargets();

        return Ok(new { message = "状态已切换", isEnabled = rule.IsEnabled });
    }

    /// <summary>
    /// 删除路由规则。
    /// </summary>
    [HttpPost("delete/{ruleId}")]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.ProxyRouteRules.FindAsync([ruleId], cancellationToken);
        if (rule is null)
            return NotFound(new { message = "规则不存在" });

        _dbContext.ProxyRouteRules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateRouteTargets();

        return Ok(new { message = "规则已删除" });
    }
}
