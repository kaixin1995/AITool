using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
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

// 可直接追加到候选队列的站点实例
public sealed class SiteInstanceItem
{
    // 站点ID
    public Guid SiteId { get; set; }
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 上游模型组名称
    public string UpstreamModelName { get; set; } = string.Empty;
    // 站点模型名称
    public string SiteModelName { get; set; } = string.Empty;
    // 站点协议类型
    public string ProtocolType { get; set; } = string.Empty;
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

    public RouteRulesApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 获取所有路由入口模型名称列表，用于下拉选择
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

        // 查询已配置路由规则的模型名称
        var routedModels = await _dbContext.ProxyRouteRules
            .Select(r => r.ExternalModelName)
            .Distinct()
            .ToListAsync(cancellationToken);

        // 已存在的逻辑入口名也应出现在管理列表中，即使它不在模型库里
        foreach (var routedModelName in routedModels)
        {
            if (models.All(m => !string.Equals(m.ModelName, routedModelName, StringComparison.Ordinal)))
            {
                models.Add(new RouteModelItem
                {
                    ModelName = routedModelName,
                    DisplayName = routedModelName
                });
            }
        }

        models = models
            .OrderBy(m => m.DisplayName)
            .ToList();

        // 填充站点数量与规则标记
        var modelItems = await _dbContext.ModelLibraryItems
            .Where(m => modelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);

        foreach (var model in models)
        {
            model.SiteCount = enabledMappings
                .Where(em => modelItems.TryGetValue(em.ModelLibraryItemId, out var item)
                    && item.ModelName == model.ModelName)
                .Count();

            model.HasRouteRules = routedModels.Contains(model.ModelName);
        }

        return Ok(models);
    }

    // 获取所有可直接追加的站点实例，支持按站点实例维护候选队列
    [HttpGet("site-instances")]
    public async Task<IActionResult> GetSiteInstances(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        var mappings = await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled)
            .OrderBy(m => m.RemoteModelName)
            .ToListAsync(cancellationToken);

        var results = mappings
            .Where(m => sites.ContainsKey(m.SiteId))
            .Select(m => new SiteInstanceItem
            {
                SiteId = m.SiteId,
                SiteName = sites[m.SiteId].Name,
                UpstreamModelName = m.RemoteModelName,
                SiteModelName = m.RemoteModelName,
                ProtocolType = sites[m.SiteId].ProtocolType
            })
            .ToList();

        return Ok(results);
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
        if (string.IsNullOrWhiteSpace(request.ExternalModelName))
            return BadRequest(new { message = "模型名称不能为空" });

        // 删除该模型的所有旧规则
        var existingRules = await _dbContext.ProxyRouteRules
            .Where(r => r.ExternalModelName == request.ExternalModelName)
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
                ExternalModelName = request.ExternalModelName,
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

        return Ok(new { message = "规则已删除" });
    }
}
