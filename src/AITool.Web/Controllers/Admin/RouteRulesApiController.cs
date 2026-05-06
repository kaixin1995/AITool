using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 可用模型列表项
public sealed class RouteModelItem
{
    // 模型名称（用作 ExternalModelName）
    public string ModelName { get; set; } = string.Empty;
    // 显示名称
    public string DisplayName { get; set; } = string.Empty;
    // 拥有该模型的站点数量
    public int SiteCount { get; set; }
    // 是否已配置路由规则
    public bool HasRouteRules { get; set; }
}

// 主入口列表项
public sealed class RouteEntryListItem
{
    // 主入口名称
    public string EntryName { get; set; } = string.Empty;
    // 当前候选数量
    public int CandidateCount { get; set; }
}

// 创建主入口请求
public sealed class CreateRouteEntryRequest
{
    // 主入口名称
    public string EntryName { get; set; } = string.Empty;
}

// 删除主入口请求
public sealed class DeleteRouteEntryRequest
{
    // 主入口名称
    public string EntryName { get; set; } = string.Empty;
}

// 可添加的站点实例项
public sealed class SiteInstanceItem
{
    // 站点ID
    public Guid SiteId { get; set; }
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 上游模型实例名称
    public string SiteModelName { get; set; } = string.Empty;
    // 站点协议类型
    public string ProtocolType { get; set; } = string.Empty;
}

// 自动发现的站点信息
public sealed class DiscoveredSiteItem
{
    // 站点ID
    public Guid SiteId { get; set; }
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 站点上的远程模型名称
    public string RemoteModelName { get; set; } = string.Empty;
    // 站点是否启用
    public bool SiteEnabled { get; set; }
}

// 路由规则列表项
public sealed class RouteRuleListItem
{
    // 规则ID
    public Guid RuleId { get; set; }
    // 站点ID
    public Guid SiteId { get; set; }
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 上游模型名称
    public string UpstreamModelName { get; set; } = string.Empty;
    // 站点模型名称
    public string SiteModelName { get; set; } = string.Empty;
    // 全局优先级
    public int Priority { get; set; }
    // 上游模型组优先级
    public int ModelPriority { get; set; }
    // 组内实例优先级
    public int InstancePriority { get; set; }
    // 是否启用
    public bool IsEnabled { get; set; }
}

// 保存路由规则的请求体
public sealed class SaveRouteRulesRequest
{
    // 外部模型名称
    public string ExternalModelName { get; set; } = string.Empty;
    // 排好序的规则列表
    public List<SaveRouteRuleEntry> Rules { get; set; } = [];
}

// 单条规则条目
public sealed class SaveRouteRuleEntry
{
    // 上游模型名称
    public string UpstreamModelName { get; set; } = string.Empty;
    // 目标站点ID
    public Guid SiteId { get; set; }
    // 站点上的模型名称
    public string SiteModelName { get; set; } = string.Empty;
}

// 路由规则管理 API，提供模型自动发现、拖拽排序保存等功能
[ApiController]
[Route("api/admin/route-rules")]
public sealed class RouteRulesApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;

    public RouteRulesApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    // 获取主入口列表，包含当前候选数量
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

    // 创建可独立存在的主入口
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

    // 删除主入口及其下所有候选规则
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

    // 返回全部可供追加的站点模型实例
    [HttpGet("site-instances")]
    public async Task<IActionResult> GetSiteInstances(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .ToDictionaryAsync(x => x.Id, x => x, cancellationToken);

        var items = await _dbContext.SiteModelMappings
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.RemoteModelName)
            .ToListAsync(cancellationToken);

        var result = items
            .Where(x => sites.ContainsKey(x.SiteId))
            .Select(x => new SiteInstanceItem
            {
                SiteId = x.SiteId,
                SiteName = sites[x.SiteId].Name,
                SiteModelName = x.RemoteModelName,
                ProtocolType = sites[x.SiteId].ProtocolType
            })
            .ToList();

        return Ok(result);
    }

    // 获取所有有站点映射的模型名称列表，用于下拉选择
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
        var routedModels = await _dbContext.ProxyRouteRules
            .Select(r => r.ExternalModelName)
            .Distinct()
            .ToHashSetAsync(cancellationToken);

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

    // 根据模型名称自动发现拥有该模型的站点
    [HttpGet("discover-sites")]
    public async Task<IActionResult> DiscoverSites(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return Ok(new List<DiscoveredSiteItem>());

        var sites = await _dbContext.Sites.ToDictionaryAsync(s => s.Id, s => s, cancellationToken);
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

    // 获取指定模型已配置的路由规则列表
    [HttpGet("list")]
    public async Task<IActionResult> ListRules(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return Ok(new List<RouteRuleListItem>());

        var sites = await _dbContext.Sites.ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

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

    // 保存路由规则（删除旧的，按新顺序创建）
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

    // 切换路由规则的启用/禁用状态
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

    // 删除单条路由规则
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
