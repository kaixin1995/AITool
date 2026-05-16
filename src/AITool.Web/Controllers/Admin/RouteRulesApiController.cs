using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// RouteModelItem。
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
/// RouteEntryListItem。
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
/// CreateRouteEntryRequest。
/// </summary>
public sealed class CreateRouteEntryRequest
{
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
}

/// <summary>
/// DeleteRouteEntryRequest。
/// </summary>
public sealed class DeleteRouteEntryRequest
{
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
}

/// <summary>
/// SiteInstanceItem。
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
}

/// <summary>
/// DiscoveredSiteItem。
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
/// RouteRuleListItem。
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
}

/// <summary>
/// SaveRouteRulesRequest。
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
/// SaveRouteRuleEntry。
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
}

/// <summary>
/// RouteRulesApiController。
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
    /// 创建路由规则控制器。
    /// </summary>
    public RouteRulesApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 获取主入口列表。
    /// </summary>
    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(CancellationToken cancellationToken)
    {
        var candidateCounts = await _dbContext.ProxyRouteRules
            .GroupBy(x => x.ExternalModelName)
            .Select(g => new { EntryName = g.Key, CandidateCount = g.Count() })
            .ToListAsync(cancellationToken);

        var storedEntries = await _dbContext.ProxyRouteEntries
            .OrderBy(x => x.EntryName)
            .Select(x => x.EntryName)
            .ToListAsync(cancellationToken);

        var countsByName = candidateCounts.ToDictionary(x => x.EntryName, x => x.CandidateCount, StringComparer.Ordinal);
        var mergedNames = storedEntries
            .Concat(candidateCounts.Select(x => x.EntryName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var result = mergedNames.Select(entryName => new RouteEntryListItem
        {
            EntryName = entryName,
            CandidateCount = countsByName.GetValueOrDefault(entryName, 0)
        }).ToList();

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
        // 候选实例池只保留仍然挂在启用模型上的有效站点映射，避免显示孤儿映射。
        var result = await (
                from mapping in _dbContext.SiteModelMappings
                join site in _dbContext.Sites on mapping.SiteId equals site.Id
                join model in _dbContext.ModelLibraryItems on mapping.ModelLibraryItemId equals model.Id
                where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                orderby site.Name, mapping.RemoteModelName
                select new SiteInstanceItem
                {
                    SiteId = site.Id,
                    SiteName = site.Name,
                    SiteModelName = mapping.RemoteModelName,
                    ProtocolType = site.ProtocolType
                })
            .ToListAsync(cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// 获取可配置路由的模型。
    /// </summary>
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        // 查询有启用的站点映射的模型
        var enabledMappings = await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled)
            .ToListAsync(cancellationToken);

        var modelIds = enabledMappings.Select(m => m.ModelLibraryItemId).Distinct().ToList();

        var models = await _dbContext.ModelLibraryItems
            .Where(m => modelIds.Contains(m.Id) && m.IsEnabled)
            .OrderBy(m => m.DisplayName)
            .Select(m => new RouteModelItem
            {
                ModelName = m.ModelName,
                DisplayName = m.DisplayName
            })
            .ToListAsync(cancellationToken);

        // 统计每个模型的站点数量
        var siteCounts = enabledMappings
            .GroupBy(m => m.ModelLibraryItemId)
            .ToDictionary(g => g.Key, g => g.Count());

        // 查询已配置路由规则的模型名称
        var routedModels = (await _dbContext.ProxyRouteRules
            .Select(r => r.ExternalModelName)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        // 通过 ModelName 关联
        var modelNames = models.Select(m => m.ModelName).ToHashSet();

        foreach (var model in models)
        {
            var modelId = enabledMappings
                .FirstOrDefault(m => m.ModelLibraryItemId != Guid.Empty)?
                .ModelLibraryItemId ?? Guid.Empty;

            model.SiteCount = enabledMappings
                .Count(m => modelNames.Contains(model.ModelName));

            // 检查该模型名是否有路由规则
            model.HasRouteRules = routedModels.Contains(model.ModelName);
        }

        // 填充站点数量
        var modelItems = await _dbContext.ModelLibraryItems
            .Where(m => modelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);

        foreach (var model in models)
        {
            var matchingIds = enabledMappings
                .Where(em => modelItems.TryGetValue(em.ModelLibraryItemId, out var item)
                    && item.ModelName == model.ModelName)
                .Select(em => em.ModelLibraryItemId)
                .Distinct()
                .Count();

            model.SiteCount = enabledMappings
                .Where(em => modelItems.TryGetValue(em.ModelLibraryItemId, out var item)
                    && item.ModelName == model.ModelName)
                .Count();
        }

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
        if (string.IsNullOrWhiteSpace(modelName))
            return Ok(new List<DiscoveredSiteItem>());

        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);
        var normalizedModelName = modelName.Trim();
        List<DiscoveredSiteItem> results = [];

        var libraryModelIds = await _dbContext.ModelLibraryItems
            .Where(m => m.ModelName == normalizedModelName && m.IsEnabled)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        if (libraryModelIds.Count > 0)
        {
            var mappings = await _dbContext.SiteModelMappings
                .Where(m => libraryModelIds.Contains(m.ModelLibraryItemId) && m.IsEnabled)
                .ToListAsync(cancellationToken);

            foreach (var mapping in mappings)
            {
                if (sites.TryGetValue(mapping.SiteId, out var site))
                {
                    results.Add(new DiscoveredSiteItem
                    {
                        SiteId = site.Id,
                        SiteName = site.Name,
                        RemoteModelName = mapping.RemoteModelName,
                        SiteEnabled = site.IsEnabled
                    });
                }
            }
        }

        var directMappings = await _dbContext.SiteModelMappings
            .Where(m => m.RemoteModelName == normalizedModelName && m.IsEnabled)
            .ToListAsync(cancellationToken);

        foreach (var mapping in directMappings)
        {
            var exists = results.Any(x => x.SiteId == mapping.SiteId && x.RemoteModelName == mapping.RemoteModelName);
            if (!exists && sites.TryGetValue(mapping.SiteId, out var site))
            {
                results.Add(new DiscoveredSiteItem
                {
                    SiteId = site.Id,
                    SiteName = site.Name,
                    RemoteModelName = mapping.RemoteModelName,
                    SiteEnabled = site.IsEnabled
                });
            }
        }

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
        if (string.IsNullOrWhiteSpace(modelName))
            return Ok(new List<RouteRuleListItem>());

        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        var rules = await _dbContext.ProxyRouteRules
            .Where(r => r.ExternalModelName == modelName)
            .OrderBy(r => r.Priority)
            .ToListAsync(cancellationToken);

        var result = rules.Select(r => new RouteRuleListItem
        {
            RuleId = r.Id,
            SiteId = r.SiteId,
            SiteName = sites.TryGetValue(r.SiteId, out var s) ? s.Name : "(未知站点)",
            UpstreamModelName = r.UpstreamModelName,
            SiteModelName = r.SiteModelName,
            Priority = r.Priority,
            ModelPriority = r.ModelPriority,
            InstancePriority = r.InstancePriority,
            IsEnabled = r.IsEnabled
        }).ToList();

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

            _dbContext.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = entryName,
                UpstreamModelName = normalizedUpstreamModelName,
                SiteId = entry.SiteId,
                SiteModelName = entry.SiteModelName,
                Priority = i,
                ModelPriority = modelPriority,
                InstancePriority = sameModelEarlierCount,
                IsEnabled = true
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateRouteTargets();

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
