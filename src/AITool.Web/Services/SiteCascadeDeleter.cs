using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// 站点级联删除工具：删除站点时同步清理其模型映射、路由规则，并清理由此变空的路由入口。
/// <para>
/// 抽取自 Pages/Admin/Sites/Index.cshtml.cs 的 RemoveSitesAsync / CleanupEmptyRouteEntriesAsync，
/// 供站点管理页面与 Codex 账号供给工厂（删除托管 Site）共用，避免逻辑重复与漂移。
/// </para>
/// </summary>
public sealed class SiteCascadeDeleter
{
    private readonly AppDbContext _dbContext;

    public SiteCascadeDeleter(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 删除指定站点及其关联映射、路由规则，并清理空壳路由入口。返回删除的站点数量。
    /// </summary>
    public async Task<int> RemoveSitesAsync(IEnumerable<Guid> siteIds, CancellationToken cancellationToken)
    {
        var normalizedSiteIds = siteIds.Distinct().ToList();
        if (normalizedSiteIds.Count == 0)
        {
            return 0;
        }

        var sites = await _dbContext.Sites
            .Where(x => normalizedSiteIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (sites.Count == 0)
        {
            return 0;
        }

        var mappings = await _dbContext.SiteModelMappings
            .Where(x => normalizedSiteIds.Contains(x.SiteId))
            .ToListAsync(cancellationToken);
        var rules = await _dbContext.ProxyRouteRules
            .Where(x => normalizedSiteIds.Contains(x.SiteId))
            .ToListAsync(cancellationToken);
        var affectedEntryNames = rules
            .Select(x => x.ExternalModelName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (mappings.Count > 0)
        {
            _dbContext.SiteModelMappings.RemoveRange(mappings);
        }

        if (rules.Count > 0)
        {
            _dbContext.ProxyRouteRules.RemoveRange(rules);
        }

        _dbContext.Sites.RemoveRange(sites);
        await CleanupEmptyRouteEntriesAsync(affectedEntryNames, cancellationToken);
        return sites.Count;
    }

    /// <summary>
    /// 删除失去全部候选规则的路由入口，避免路由管理页继续看到空壳入口。
    /// </summary>
    private async Task CleanupEmptyRouteEntriesAsync(IEnumerable<string> entryNames, CancellationToken cancellationToken)
    {
        var normalizedNames = entryNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedNames.Count == 0)
        {
            return;
        }

        var remainingEntryNames = await _dbContext.ProxyRouteRules
            .Where(x => normalizedNames.Contains(x.ExternalModelName))
            .Select(x => x.ExternalModelName)
            .Distinct()
            .ToListAsync(cancellationToken);
        var emptyEntryNames = normalizedNames
            .Except(remainingEntryNames, StringComparer.Ordinal)
            .ToList();
        if (emptyEntryNames.Count == 0)
        {
            return;
        }

        var emptyEntries = await _dbContext.ProxyRouteEntries
            .Where(x => emptyEntryNames.Contains(x.EntryName))
            .ToListAsync(cancellationToken);
        if (emptyEntries.Count > 0)
        {
            _dbContext.ProxyRouteEntries.RemoveRange(emptyEntries);
        }
    }
}
