using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// ProxyRequestMetadataCache 中偏 Admin 查询侧的只读元数据访问入口。
/// 当前先通过 partial 方式把后台页面相关查询从运行时主文件中拆出来，后续再继续向独立 Admin 查询缓存收口。
/// </summary>
public sealed partial class ProxyRequestMetadataCache
{
    /// <summary>
    /// Admin 查询元数据入口。
    /// 这里聚合的都是管理页、配置页和调试页的辅助只读查询，后续应优先迁给 Admin 宿主，避免继续与代理主链路缓存混在一起。
    /// </summary>
    public async Task<IReadOnlyList<CachedChatModel>> GetChatModelsAsync(CancellationToken cancellationToken)
    {
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    ChatModelsCacheKey,
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        if (snapshot is null)
                        {
                            return Task.FromResult<IReadOnlyList<CachedChatModel>>([]);
                        }

                        var models = (
                                from model in snapshot.Models
                                join mapping in snapshot.SiteModelMappings on model.Id equals mapping.ModelLibraryItemId
                                join site in snapshot.Sites on mapping.SiteId equals site.Id
                                where model.IsEnabled && mapping.IsEnabled && site.IsEnabled
                                group site by new { model.Id, model.DisplayName } into grouped
                                orderby grouped.Key.DisplayName
                                select new CachedChatModel
                                {
                                    ModelId = grouped.Key.Id,
                                    DisplayName = grouped.Key.DisplayName,
                                    AvailableSiteCount = grouped.Count()
                                })
                            .ToList();

                        return Task.FromResult<IReadOnlyList<CachedChatModel>>(models);
                    })
                ?? [];
        }

        return await _memoryCache.GetOrCreateAsync(
                ChatModelsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // SqlSugar 不支持 LINQ query syntax 的多表 join + group by，
                    // 改为先各自读出再在内存连接（结果有缓存，非每请求执行，性能可接受）。
                    var modelRows = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
                    var mappingRows = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var siteRows = await dbContext.Sites.ToListAsync(cancellationToken);
                    var models = (
                            from model in modelRows
                            join mapping in mappingRows on model.Id equals mapping.ModelLibraryItemId
                            join site in siteRows on mapping.SiteId equals site.Id
                            where model.IsEnabled && mapping.IsEnabled && site.IsEnabled
                            group site by new { model.Id, model.DisplayName } into grouped
                            orderby grouped.Key.DisplayName
                            select new CachedChatModel
                            {
                                ModelId = grouped.Key.Id,
                                DisplayName = grouped.Key.DisplayName,
                                AvailableSiteCount = grouped.Count()
                            })
                        .ToList();

                    return models;
                })
            ?? [];
    }

    /// <summary>
    /// 获取聊天页全部站点模型候选。
    /// </summary>
    public async Task<IReadOnlyList<CachedChatTarget>> GetChatTargetsAsync(CancellationToken cancellationToken)
    {
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    ChatTargetsCacheKey,
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        if (snapshot is null)
                        {
                            return Task.FromResult<IReadOnlyList<CachedChatTarget>>([]);
                        }

                        // 站点密钥（多 Key）按 SiteId 分组，用于把候选按 Key 展开。
                        var siteKeysBySite = (snapshot.SiteKeys ?? [])
                            .GroupBy(k => k.SiteId)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        var baseTargets = (
                                from mapping in snapshot.SiteModelMappings
                                join site in snapshot.Sites on mapping.SiteId equals site.Id
                                join model in snapshot.Models on mapping.ModelLibraryItemId equals model.Id
                                where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                                select new { mapping, site, model })
                            .ToList();

                        // 按 SiteKey 展开：每个启用 Key 产出一条候选，使聊天/调试页也享受多 Key 调度。
                        var expanded = new List<CachedChatTarget>(baseTargets.Count);
                        foreach (var item in baseTargets)
                        {
                            var keysForSite = siteKeysBySite.TryGetValue(item.site.Id, out var skList) && skList.Count > 0
                                ? skList.Select(k => new Domain.Sites.SiteKey { Id = k.Id, SiteId = k.SiteId, KeyValue = k.KeyValue, Priority = k.Priority, CreatedAt = k.CreatedAt, IsEnabled = k.IsEnabled }).ToList()
                                : null;
                            var keysBySiteTyped = keysForSite is null
                                ? new Dictionary<Guid, List<Domain.Sites.SiteKey>>()
                                : new Dictionary<Guid, List<Domain.Sites.SiteKey>> { [item.site.Id] = keysForSite };
                            var candidates = ResolveSiteKeyCandidates(item.site.Id, item.site.ApiKey, keysBySiteTyped);

                            foreach (var candidate in candidates)
                            {
                                expanded.Add(new CachedChatTarget
                                {
                                    MappingId = item.mapping.Id,
                                    ModelId = item.model.Id,
                                    ModelDisplayName = item.model.DisplayName,
                                    SiteId = item.site.Id,
                                    SiteKeyId = candidate.SiteKeyId,
                                    CircuitKey = BuildCircuitKey(item.mapping.Id, candidate.SiteKeyId),
                                    SiteName = item.site.Name,
                                    ProtocolType = item.site.ProtocolType,
                                    BaseUrl = item.site.BaseUrl,
                                    EndpointPathMode = item.site.EndpointPathMode,
                                    ApiKey = candidate.ApiKey,
                                    SiteModelName = item.mapping.RemoteModelName
                                });
                            }
                        }

                        // 保持原有的展示排序：按模型显示名、站点名、模型远程名稳定排序
                        var targets = expanded
                            .OrderBy(x => x.ModelDisplayName, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(x => x.SiteName, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(x => x.SiteModelName, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        return Task.FromResult<IReadOnlyList<CachedChatTarget>>(targets);
                    })
                ?? [];
        }

        return await _memoryCache.GetOrCreateAsync(
                ChatTargetsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // SqlSugar 不支持 LINQ query syntax 的多表 join，改为先各自读出再在内存连接。
                    var siteModelMappings = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var allSites = await dbContext.Sites.ToListAsync(cancellationToken);
                    var allModels = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
                    var allSiteKeys = await dbContext.SiteKeys
                        .Where(k => k.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var siteKeysBySite = allSiteKeys
                        .GroupBy(k => k.SiteId)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var baseTargets = (
                            from mapping in siteModelMappings
                            join site in allSites on mapping.SiteId equals site.Id
                            join model in allModels on mapping.ModelLibraryItemId equals model.Id
                            where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                            select new { mapping, site, model })
                        .ToList();

                    // 按 SiteKey 展开：每个启用 Key 产出一条候选，使聊天/调试页也享受多 Key 调度。
                    var expanded = new List<CachedChatTarget>(baseTargets.Count);
                    foreach (var item in baseTargets)
                    {
                        var candidates = ResolveSiteKeyCandidates(item.site.Id, item.site.ApiKey, siteKeysBySite);
                        foreach (var candidate in candidates)
                        {
                            expanded.Add(new CachedChatTarget
                            {
                                MappingId = item.mapping.Id,
                                ModelId = item.model.Id,
                                ModelDisplayName = item.model.DisplayName,
                                SiteId = item.site.Id,
                                SiteKeyId = candidate.SiteKeyId,
                                CircuitKey = BuildCircuitKey(item.mapping.Id, candidate.SiteKeyId),
                                SiteName = item.site.Name,
                                ProtocolType = ResolveSiteProtocolType(item.site.SupportsOpenAi, item.site.SupportsAnthropic),
                                BaseUrl = item.site.BaseUrl,
                                EndpointPathMode = item.site.EndpointPathMode,
                                ApiKey = candidate.ApiKey,
                                SiteModelName = item.mapping.RemoteModelName
                            });
                        }
                    }

                    return expanded
                        .OrderBy(x => x.ModelDisplayName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.SiteName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.SiteModelName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                })
            ?? [];
    }

    /// <summary>
    /// 获取聊天页按模型筛选后的站点模型候选。
    /// </summary>
    public async Task<IReadOnlyList<CachedChatTarget>> GetChatTargetsAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var targets = await GetChatTargetsAsync(cancellationToken);
        return targets.Where(x => x.ModelId == modelId).ToList();
    }

    /// <summary>
    /// 获取模型并发限制缓存。
    /// Core 宿主中从配置快照的 SiteModelMappings 读取，Web/Admin 宿主中从数据库查询。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetModelConcurrencyLimitsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主：从配置快照读取并发限制
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    ModelConcurrencyLimitsCacheKey,
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        if (snapshot?.SiteModelMappings is null)
                        {
                            return Task.FromResult<IReadOnlyDictionary<string, int>>(
                                new Dictionary<string, int>(StringComparer.Ordinal));
                        }

                        var limits = new Dictionary<string, int>(StringComparer.Ordinal);
                        // 加载所有启用的站点密钥，按 SiteId 分组，用于把站点级并发上限展开到每个 Key。
                        var siteKeysBySite = (snapshot.SiteKeys ?? [])
                            .GroupBy(k => k.SiteId)
                            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
                        foreach (var mapping in snapshot.SiteModelMappings)
                        {
                            if (mapping.IsEnabled && mapping.MaxConcurrency > 0)
                            {
                                // 该站点有启用的 SiteKey 时，每个 Key 各享一份独立额度；
                                // 没有时（Codex 托管 / 未迁移）回退用 SiteId，行为与原逻辑一致。
                                if (siteKeysBySite.TryGetValue(mapping.SiteId, out var keyIds) && keyIds.Count > 0)
                                {
                                    foreach (var keyId in keyIds)
                                    {
                                        limits[$"{keyId:N}:{mapping.RemoteModelName}"] = mapping.MaxConcurrency;
                                    }
                                }
                                else
                                {
                                    limits[$"{mapping.SiteId:N}:{mapping.RemoteModelName}"] = mapping.MaxConcurrency;
                                }
                            }
                        }

                        return Task.FromResult<IReadOnlyDictionary<string, int>>(limits);
                    })
                ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }

        // Web/Admin 宿主：从数据库查询
        return await _memoryCache.GetOrCreateAsync(
                ModelConcurrencyLimitsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var mappings = await dbContext.SiteModelMappings

                        .Where(x => x.IsEnabled && x.MaxConcurrency > 0)
                        .Select(x => new
                        {
                            x.SiteId,
                            x.RemoteModelName,
                            x.MaxConcurrency
                        })
                        .ToListAsync(cancellationToken);
                    // 加载所有启用的站点密钥，按 SiteId 分组，用于把站点级并发上限展开到每个 Key。
                    var siteKeys = await dbContext.SiteKeys
                        .Where(k => k.IsEnabled)
                        .Select(k => new { k.SiteId, k.Id })
                        .ToListAsync(cancellationToken);
                    var siteKeysBySite = siteKeys
                        .GroupBy(k => k.SiteId)
                        .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

                    var limits = new Dictionary<string, int>(mappings.Count, StringComparer.Ordinal);
                    foreach (var mapping in mappings)
                    {
                        // 该站点有启用的 SiteKey 时，每个 Key 各享一份独立额度；
                        // 没有时（Codex 托管 / 未迁移）回退用 SiteId，行为与原逻辑一致。
                        if (siteKeysBySite.TryGetValue(mapping.SiteId, out var keyIds) && keyIds.Count > 0)
                        {
                            foreach (var keyId in keyIds)
                            {
                                limits[$"{keyId:N}:{mapping.RemoteModelName}"] = mapping.MaxConcurrency;
                            }
                        }
                        else
                        {
                            limits[$"{mapping.SiteId:N}:{mapping.RemoteModelName}"] = mapping.MaxConcurrency;
                        }
                    }

                    return limits;
                })
            ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 获取启用站点名称缓存。
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetEnabledSiteNamesAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                EnabledSiteNamesCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var sites = await dbContext.Sites
                        
                        .Where(x => x.IsEnabled)
                        .Select(x => new
                        {
                            x.Id,
                            x.Name
                        })
                        .ToListAsync(cancellationToken);

                    return sites.ToDictionary(x => x.Id, x => x.Name);
                })
            ?? new Dictionary<Guid, string>();
    }

    /// <summary>
    /// 获取路由主入口列表缓存。
    /// </summary>
    public async Task<IReadOnlyList<RouteEntryListItem>> GetRouteEntriesAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                RouteEntriesCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    // SqlSugar 不支持 EF 风格的 GroupBy+聚合下推，改为先读出再在内存聚合。
                    var allRules = await dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
                    var candidateCounts = allRules
                        .GroupBy(x => x.ExternalModelName, StringComparer.Ordinal)
                        .Select(g => new { EntryName = g.Key, CandidateCount = g.Count() })
                        .ToList();

                    var storedEntries = (await dbContext.ProxyRouteEntries
                            .OrderBy(x => x.EntryName)
                            .ToListAsync(cancellationToken))
                        .Select(x => x.EntryName)
                        .ToList();

                    var countsByName = candidateCounts.ToDictionary(x => x.EntryName, x => x.CandidateCount, StringComparer.Ordinal);
                    return storedEntries
                        .Concat(candidateCounts.Select(x => x.EntryName))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .Select(entryName => new RouteEntryListItem
                        {
                            EntryName = entryName,
                            CandidateCount = countsByName.GetValueOrDefault(entryName, 0)
                        })
                        .ToList();
                })
            ?? [];
    }

    /// <summary>
    /// 获取可选站点实例缓存。
    /// </summary>
    public async Task<IReadOnlyList<SiteInstanceItem>> GetRouteSiteInstancesAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                RouteSiteInstancesCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    // SqlSugar 不支持 LINQ query syntax 的多表 join，改为先各自读出再在内存连接。
                    var mappingRows3 = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var siteRows3 = await dbContext.Sites.ToListAsync(cancellationToken);
                    var modelRows3 = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
                    return (
                            from mapping in mappingRows3
                            join site in siteRows3 on mapping.SiteId equals site.Id
                            join model in modelRows3 on mapping.ModelLibraryItemId equals model.Id
                            where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                            orderby site.Name, mapping.RemoteModelName
                            select new SiteInstanceItem
                            {
                                SiteId = site.Id,
                                SiteName = site.Name,
                                SiteModelName = mapping.RemoteModelName,
                                ProtocolType = site.ProtocolType
                            })
                        .ToList();
                })
            ?? [];
    }

    /// <summary>
    /// 获取可配置路由模型缓存。
    /// </summary>
    public async Task<IReadOnlyList<RouteModelItem>> GetRouteModelsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                RouteModelsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var enabledMappings = await dbContext.SiteModelMappings
                        
                        .Where(m => m.IsEnabled)
                        .Select(m => new
                        {
                            m.ModelLibraryItemId
                        })
                        .ToListAsync(cancellationToken);

                    var modelIds = enabledMappings
                        .Select(m => m.ModelLibraryItemId)
                        .Distinct()
                        .ToList();
                    if (modelIds.Count == 0)
                    {
                        return [];
                    }

                    var models = await dbContext.ModelLibraryItems
                        
                        .Where(m => modelIds.Contains(m.Id) && m.IsEnabled)
                        .OrderBy(m => m.DisplayName)
                        .Select(m => new RouteModelItem
                        {
                            ModelName = m.ModelName,
                            DisplayName = m.DisplayName
                        })
                        .ToListAsync(cancellationToken);

                    var modelNameById = await dbContext.ModelLibraryItems
                        
                        .Where(m => modelIds.Contains(m.Id))
                        .ToDictionaryAsync(m => m.Id, m => m.ModelName, cancellationToken);
                    var routedModels = (await dbContext.ProxyRouteRules
                        
                        .Select(r => r.ExternalModelName)
                        .Distinct()
                        .ToListAsync(cancellationToken))
                        .ToHashSet(StringComparer.Ordinal);

                    foreach (var model in models)
                    {
                        model.SiteCount = enabledMappings.Count(em => modelNameById.TryGetValue(em.ModelLibraryItemId, out var modelName)
                            && string.Equals(modelName, model.ModelName, StringComparison.Ordinal));
                        model.HasRouteRules = routedModels.Contains(model.ModelName);
                    }

                    return models;
                })
            ?? [];
    }

    /// <summary>
    /// 获取按模型名发现的可用站点缓存。
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredSiteItem>> GetDiscoveredSitesAsync(string modelName, CancellationToken cancellationToken)
    {
        var normalizedModelName = modelName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedModelName))
        {
            return [];
        }

        var allDiscoveredSites = await _memoryCache.GetOrCreateAsync(
                RouteDiscoveredSitesCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var sites = await dbContext.Sites
                        
                        .Where(s => s.IsEnabled)
                        .Select(s => new CachedSiteSnapshot
                        {
                            Id = s.Id,
                            Name = s.Name
                        })
                        .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);
                    var modelNamesById = await dbContext.ModelLibraryItems
                        
                        .Where(m => m.IsEnabled)
                        .ToDictionaryAsync(m => m.Id, m => m.ModelName, cancellationToken);
                    var mappings = await dbContext.SiteModelMappings
                        
                        .Where(m => m.IsEnabled)
                        .Select(m => new
                        {
                            m.SiteId,
                            m.ModelLibraryItemId,
                            m.RemoteModelName
                        })
                        .ToListAsync(cancellationToken);

                    var results = new Dictionary<string, List<DiscoveredSiteItem>>(StringComparer.Ordinal);
                    foreach (var mapping in mappings)
                    {
                        if (!sites.TryGetValue(mapping.SiteId, out var site))
                        {
                            continue;
                        }

                        AddDiscoveredSite(results, mapping.RemoteModelName, site, mapping.RemoteModelName);
                        if (modelNamesById.TryGetValue(mapping.ModelLibraryItemId, out var libraryModelName))
                        {
                            AddDiscoveredSite(results, libraryModelName, site, mapping.RemoteModelName);
                        }
                    }

                    return results;
                })
            ?? new Dictionary<string, List<DiscoveredSiteItem>>(StringComparer.Ordinal);

        return allDiscoveredSites.TryGetValue(normalizedModelName, out var items)
            ? items
            : [];
    }

    /// <summary>
    /// 获取按主入口聚合的路由规则缓存。
    /// </summary>
    public async Task<IReadOnlyList<RouteRuleListItem>> GetRouteRulesAsync(string modelName, CancellationToken cancellationToken)
    {
        var normalizedModelName = modelName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedModelName))
        {
            return [];
        }

        var rulesByEntry = await _memoryCache.GetOrCreateAsync(
                RouteRulesByEntryCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    // 查询全部站点（含禁用），以便在管理页面展示真实站点名和启用状态。
                    var siteRows = await dbContext.Sites
                        
                        .Select(s => new { s.Id, s.Name, s.IsEnabled })
                        .ToListAsync(cancellationToken);
                    var sites = siteRows.ToDictionary(s => s.Id, s => s.Name);
                    var siteEnabledMap = siteRows.ToDictionary(s => s.Id, s => s.IsEnabled);
                    var rules = await dbContext.ProxyRouteRules
                        
                        .OrderBy(r => r.Priority)
                        .Select(r => new
                        {
                            r.ExternalModelName,
                            r.Id,
                            r.SiteId,
                            r.UpstreamModelName,
                            r.SiteModelName,
                            r.Priority,
                            r.ModelPriority,
                            r.InstancePriority,
                            r.IsEnabled,
                            r.AvailabilityMode,
                            r.TimeRangesJson
                        })
                        .ToListAsync(cancellationToken);

                    return rules
                        .GroupBy(r => r.ExternalModelName, StringComparer.Ordinal)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(r => new RouteRuleListItem
                            {
                                RuleId = r.Id,
                                SiteId = r.SiteId,
                                SiteName = sites.TryGetValue(r.SiteId, out var siteName) ? siteName : "(已删除站点)",
                                SiteEnabled = siteEnabledMap.TryGetValue(r.SiteId, out var enabled) && enabled,
                                UpstreamModelName = r.UpstreamModelName,
                                SiteModelName = r.SiteModelName,
                                Priority = r.Priority,
                                ModelPriority = r.ModelPriority,
                                InstancePriority = r.InstancePriority,
                                IsEnabled = r.IsEnabled,
                                AvailabilityMode = NormalizeAvailabilityMode(r.AvailabilityMode),
                                TimeRangesJson = NormalizeTimeRangesJson(r.AvailabilityMode, r.TimeRangesJson)
                            }).ToList(),
                            StringComparer.Ordinal);
                })
            ?? new Dictionary<string, List<RouteRuleListItem>>(StringComparer.Ordinal);

        return rulesByEntry.TryGetValue(normalizedModelName, out var items)
            ? items
            : [];
    }

    /// <summary>
    /// 获取调试页默认访问密钥缓存（按 KeyName 字典序选首个启用项）。
    /// </summary>
    public async Task<string> GetDeveloperDefaultAccessKeyAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                DeveloperDefaultAccessKeyCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var defaultKey = await dbContext.ProxyAccessKeys
                        .Where(k => k.IsEnabled && !string.IsNullOrWhiteSpace(k.PlainKey))
                        .OrderBy(k => k.KeyName)
                        .FirstAsync(cancellationToken);
                    return defaultKey?.PlainKey ?? string.Empty;
                })
            ?? string.Empty;
    }

    /// <summary>
    /// 获取调试页可用模型缓存。
    /// </summary>
    public async Task<IReadOnlyList<ClientSimulatorModelItemViewModel>> GetDeveloperDebugModelsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                DeveloperDebugModelsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    // SqlSugar 不支持 LINQ query syntax 的多表 join + group by，改为先各自读出再在内存连接。
                    var ruleRows = await dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
                    var debugSiteRows = await dbContext.Sites.ToListAsync(cancellationToken);
                    return (
                            from rule in ruleRows
                            join site in debugSiteRows on rule.SiteId equals site.Id
                            where rule.IsEnabled && site.IsEnabled
                            group site by rule.ExternalModelName into g
                            orderby g.Key
                            select new ClientSimulatorModelItemViewModel
                            {
                                ModelName = g.Key,
                                RouteCount = g.Count(),
                                SupportsOpenAi = g.Any(x => x.SupportsOpenAi),
                                SupportsAnthropic = g.Any(x => x.SupportsAnthropic),
                                CanUseOpenAi = g.Any(),
                                CanUseAnthropic = g.Any()
                            })
                        .ToList();
                })
            ?? [];
    }
}
