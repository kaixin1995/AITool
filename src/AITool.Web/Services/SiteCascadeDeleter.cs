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
    private readonly ProxyRequestMetadataCache? _metadataCache;

    public SiteCascadeDeleter(AppDbContext dbContext, ProxyRequestMetadataCache? metadataCache = null)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 删除指定站点及其关联映射、路由规则，并清理空壳路由入口。返回删除的站点数量。
    /// </summary>
    public Task<int> RemoveSitesAsync(IEnumerable<Guid> siteIds, CancellationToken cancellationToken)
        => ExecuteGraphMutationAsync(db => new SiteCascadeDeleter(db).RemoveSitesCoreAsync(siteIds, cancellationToken), cancellationToken);

    /// <summary>
    /// Own the graph lease and transaction. Callers with an existing transaction must acquire the
    /// graph lease before opening it and use the explicit InGraphMutation cleanup method instead.
    /// </summary>
    public async Task<T> ExecuteGraphMutationAsync<T>(Func<AppDbContext, Task<T>> mutation, CancellationToken cancellationToken,
        ProxyRequestMetadataCache? metadataCache = null)
    {
        // Never wait for the graph gate while holding a DB transaction (inverts the lock order).
        if (_dbContext.Client.Ado.Transaction != null)
            throw new InvalidOperationException("Acquire the route graph gate before starting a cascade transaction.");
        using var graphLease = await RouteGraphMutationGate.AcquireAsync(cancellationToken);
        using var client = _dbContext.Client.CopyNew();
        var db = new AppDbContext(client, new SemaphoreSlim(1, 1));
        await client.Ado.ExecuteCommandAsync("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        T result;
        List<string> destructiveSources;
        try
        {
            var before = await db.ProxyRouteEntries.ToListAsync(cancellationToken);
            var beforeRules = await db.ProxyRouteRules.ToListAsync(cancellationToken);
            var beforeMappings = await db.SiteModelMappings.ToListAsync(cancellationToken);
            result = await mutation(db);
            var remainingIds = (await db.ProxyRouteEntries.Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();
            var afterRules = (await db.ProxyRouteRules.ToListAsync(cancellationToken)).ToDictionary(r => r.Id);
            var afterMappings = (await db.SiteModelMappings.ToListAsync(cancellationToken)).ToDictionary(m => m.Id);
            var removedMappings = beforeMappings.Where(m => !afterMappings.TryGetValue(m.Id, out var after)
                || (m.IsEnabled && !after.IsEnabled) || m.RemoteModelName != after.RemoteModelName).ToList();
            destructiveSources = before.Where(e => !remainingIds.Contains(e.Id)).Select(e => e.EntryName)
                .Concat(beforeRules.Where(r => !afterRules.TryGetValue(r.Id, out var after)
                    || after.TargetKind != r.TargetKind || after.SiteId != r.SiteId || after.SiteModelName != r.SiteModelName
                    || (r.TargetKind == "Site" && removedMappings.Any(m => m.SiteId == r.SiteId && m.RemoteModelName == r.SiteModelName)))
                    .Select(r => r.ExternalModelName))
                .Distinct(StringComparer.Ordinal).ToList();
            await client.Ado.CommitTranAsync();
        }
        catch
        {
            await client.Ado.RollbackTranAsync();
            throw;
        }
        var cache = metadataCache ?? _metadataCache;
        foreach (var entryName in destructiveSources) cache?.InvalidateDeletedRouteEntry(entryName);
        cache?.InvalidateModelMetadata();
        cache?.InvalidateRouteTargets();
        return result;
    }

    /// <summary>Caller must hold RouteGraphMutationGate before opening its current transaction.</summary>
    public Task<int> RemoveSitesInGraphMutationAsync(IEnumerable<Guid> siteIds, CancellationToken cancellationToken)
        => RemoveSitesCoreAsync(siteIds, cancellationToken);

    private async Task<int> RemoveSitesCoreAsync(IEnumerable<Guid> siteIds, CancellationToken cancellationToken)
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
            .Where(x => x.TargetKind == "Site" && normalizedSiteIds.Contains(x.SiteId))
            .ToListAsync(cancellationToken);
        // 级联删除站点的所有密钥，避免遗留孤儿 SiteKey。
        var siteKeys = await _dbContext.SiteKeys
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

        if (siteKeys.Count > 0)
        {
            _dbContext.SiteKeys.RemoveRange(siteKeys);
        }

        _dbContext.Sites.RemoveRange(sites);
        await CleanupEmptyRouteEntriesAsyncCore(affectedEntryNames, cancellationToken);
        return sites.Count;
    }

    /// <summary>
    /// 删除失去全部候选规则的路由入口，避免路由管理页继续看到空壳入口。
    /// <para>该方法已公开，供 Models 模块删除模型/映射后复用同一套清理逻辑，避免重复实现。</para>
    /// </summary>
    public async Task CleanupEmptyRouteEntriesAsync(IEnumerable<string> entryNames, CancellationToken cancellationToken)
        => await ExecuteGraphMutationAsync(async db =>
        {
            await new SiteCascadeDeleter(db).CleanupEmptyRouteEntriesAsyncCore(entryNames, cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>Caller must already hold RouteGraphMutationGate and own the current transaction.</summary>
    public Task CleanupEmptyRouteEntriesInGraphMutationAsync(IEnumerable<string> entryNames, CancellationToken cancellationToken)
        => CleanupEmptyRouteEntriesAsyncCore(entryNames, cancellationToken);

    /// <summary>
    /// 删除失去全部候选规则的路由入口，避免路由管理页继续看到空壳入口（内部实现）。
    /// </summary>
    private async Task CleanupEmptyRouteEntriesAsyncCore(IEnumerable<string> entryNames, CancellationToken cancellationToken)
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
