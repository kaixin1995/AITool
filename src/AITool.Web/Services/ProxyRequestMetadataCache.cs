using System.Security.Cryptography;
using System.Text;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Pages.Admin.ClientSimulator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

/// <summary>
/// 代理请求元数据缓存。
/// </summary>
public sealed class ProxyRequestMetadataCache
{
    /// <summary>
    /// 缓存有效时长。
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    /// <summary>
    /// 访问密钥缓存键。
    /// </summary>
    private const string AccessKeyCacheKey = "proxy-access-keys";
    /// <summary>
    /// 运行时设置缓存键。
    /// </summary>
    private const string RuntimeSettingsCacheKey = "proxy-runtime-settings";
    /// <summary>
    /// 路由目标缓存键前缀。
    /// </summary>
    private const string RouteTargetsCacheKeyPrefix = "proxy-route-targets:";
    /// <summary>
    /// 聊天模型缓存键。
    /// </summary>
    private const string ChatModelsCacheKey = "chat-models";
    /// <summary>
    /// 模型并发限制缓存键。
    /// </summary>
    private const string ModelConcurrencyLimitsCacheKey = "model-concurrency-limits";
    /// <summary>
    /// 路由主入口列表缓存键。
    /// </summary>
    private const string RouteEntriesCacheKey = "admin-route-entries";
    /// <summary>
    /// 路由候选站点实例缓存键。
    /// </summary>
    private const string RouteSiteInstancesCacheKey = "admin-route-site-instances";
    /// <summary>
    /// 路由可配置模型缓存键。
    /// </summary>
    private const string RouteModelsCacheKey = "admin-route-models";
    /// <summary>
    /// 路由模型发现结果缓存键。
    /// </summary>
    private const string RouteDiscoveredSitesCacheKey = "admin-route-discovered-sites";
    /// <summary>
    /// 路由规则列表缓存键。
    /// </summary>
    private const string RouteRulesByEntryCacheKey = "admin-route-rules-by-entry";
    /// <summary>
    /// 开发者调试页默认访问密钥缓存键。
    /// </summary>
    private const string DeveloperDefaultAccessKeyCacheKey = "admin-developer-default-access-key";
    /// <summary>
    /// 开发者调试页可用模型缓存键。
    /// </summary>
    private const string DeveloperDebugModelsCacheKey = "admin-developer-debug-models";
    /// <summary>
    /// 启用模型缓存键。
    /// </summary>
    private const string EnabledModelsCacheKey = "enabled-models";
    /// <summary>
    /// 兜底映射缓存键。
    /// </summary>
    private const string FallbackMappingsCacheKey = "fallback-mappings";
    /// <summary>
    /// 内存缓存。
    /// </summary>
    private readonly IMemoryCache _memoryCache;
    /// <summary>
    /// 服务作用域工厂。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// 初始化代理请求元数据缓存。
    /// </summary>
    public ProxyRequestMetadataCache(IMemoryCache memoryCache, IServiceScopeFactory scopeFactory)
    {
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 校验访问密钥。
    /// </summary>
    public async Task<CachedProxyAccessKey?> ValidateAccessKeyAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
        var accessKeys = await GetAccessKeysAsync(cancellationToken);
        return accessKeys.TryGetValue(hash, out var accessKey)
            ? accessKey
            : null;
    }

    /// <summary>
    /// 获取运行时设置缓存。
    /// </summary>
    public async Task<CachedProxyRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                RuntimeSettingsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var settings = await dbContext.SystemRuntimeSettings
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);

                    return settings is null
                        ? new CachedProxyRuntimeSettings()
                        : new CachedProxyRuntimeSettings
                        {
                            ProxyRequestTimeoutSeconds = settings.ProxyRequestTimeoutSeconds,
                            ProxyRetryCount = settings.ProxyRetryCount,
                            DetectionRequestTimeoutSeconds = settings.DetectionRequestTimeoutSeconds,
                            DetectionRetryCount = settings.DetectionRetryCount,
                            DetectionConcurrency = settings.DetectionConcurrency,
                            CircuitBreakerFailureThreshold = settings.CircuitBreakerFailureThreshold,
                            CircuitBreakerRecoveryMinutes = settings.CircuitBreakerRecoveryMinutes,
                            UsageLogAutoCleanupEnabled = settings.UsageLogAutoCleanupEnabled,
                            DeveloperFeaturesEnabled = settings.DeveloperFeaturesEnabled,
                            ConcurrencyMode = settings.ConcurrencyMode,
                            ConcurrencyQueueTimeoutSeconds = settings.ConcurrencyQueueTimeoutSeconds
                        };
                })
            ?? new CachedProxyRuntimeSettings();
    }

    /// <summary>
    /// 获取已启用模型名称列表。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEnabledModelNamesAsync(string protocolType, CancellationToken cancellationToken)
    {
        var routes = await GetRouteTargetsAsync(cancellationToken);
        return routes
            .Where(x => x.SupportsProtocol(protocolType))
            .Select(x => x.ExternalModelName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 获取已启用模型名称列表。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEnabledModelNamesAsync(CancellationToken cancellationToken)
    {
        var routes = await GetRouteTargetsAsync(cancellationToken);
        return routes
            .Select(x => x.ExternalModelName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 获取模型对应的路由目标。
    /// </summary>
    public async Task<IReadOnlyList<CachedProxyRouteTarget>> GetRouteTargetsForModelAsync(
        string protocolType,
        string externalModelName,
        CancellationToken cancellationToken)
    {
        var routes = await GetRouteTargetsAsync(cancellationToken);
        return routes
            .Where(x => string.Equals(x.ExternalModelName, externalModelName, StringComparison.Ordinal))
            // 路由优先级应始终以后台配置顺序为准；协议不匹配时走兼容转发，而不是提前跳过前面的候选站点。
            .OrderBy(x => x.ModelPriority)
            .ThenBy(x => x.InstancePriority)
            .ThenBy(x => x.Priority)
            .ToList();
    }

    /// <summary>
    /// 获取模型对应的路由目标。
    /// </summary>
    public async Task<IReadOnlyList<CachedProxyRouteTarget>> GetRouteTargetsForModelAsync(
        string externalModelName,
        CancellationToken cancellationToken)
    {
        var routes = await GetRouteTargetsAsync(cancellationToken);
        return routes
            .Where(x => string.Equals(x.ExternalModelName, externalModelName, StringComparison.Ordinal))
            .OrderBy(x => x.ModelPriority)
            .ThenBy(x => x.InstancePriority)
            .ThenBy(x => x.Priority)
            .ToList();
    }

    /// <summary>
    /// 获取聊天模型列表。
    /// </summary>
    public async Task<IReadOnlyList<CachedChatModel>> GetChatModelsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                ChatModelsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var models = await (
                            from model in dbContext.ModelLibraryItems.AsNoTracking()
                            join mapping in dbContext.SiteModelMappings.AsNoTracking() on model.Id equals mapping.ModelLibraryItemId
                            join site in dbContext.Sites.AsNoTracking() on mapping.SiteId equals site.Id
                            where model.IsEnabled && mapping.IsEnabled && site.IsEnabled
                            group site by new { model.Id, model.DisplayName } into grouped
                            orderby grouped.Key.DisplayName
                            select new CachedChatModel
                            {
                                ModelId = grouped.Key.Id,
                                DisplayName = grouped.Key.DisplayName,
                                AvailableSiteCount = grouped.Count()
                            })
                        .ToListAsync(cancellationToken);

                    return models;
                })
            ?? [];
    }

    /// <summary>
    /// 获取模型并发限制缓存。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetModelConcurrencyLimitsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                ModelConcurrencyLimitsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var mappings = await dbContext.SiteModelMappings
                        .AsNoTracking()
                        .Where(x => x.IsEnabled && x.MaxConcurrency > 0)
                        .Select(x => new
                        {
                            x.SiteId,
                            x.RemoteModelName,
                            x.MaxConcurrency
                        })
                        .ToListAsync(cancellationToken);

                    var limits = new Dictionary<string, int>(mappings.Count, StringComparer.Ordinal);
                    foreach (var mapping in mappings)
                    {
                        limits[$"{mapping.SiteId:N}:{mapping.RemoteModelName}"] = mapping.MaxConcurrency;
                    }

                    return limits;
                })
            ?? new Dictionary<string, int>(StringComparer.Ordinal);
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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var candidateCounts = await dbContext.ProxyRouteRules
                        .AsNoTracking()
                        .GroupBy(x => x.ExternalModelName)
                        .Select(g => new { EntryName = g.Key, CandidateCount = g.Count() })
                        .ToListAsync(cancellationToken);

                    var storedEntries = await dbContext.ProxyRouteEntries
                        .AsNoTracking()
                        .OrderBy(x => x.EntryName)
                        .Select(x => x.EntryName)
                        .ToListAsync(cancellationToken);

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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await (
                            from mapping in dbContext.SiteModelMappings.AsNoTracking()
                            join site in dbContext.Sites.AsNoTracking() on mapping.SiteId equals site.Id
                            join model in dbContext.ModelLibraryItems.AsNoTracking() on mapping.ModelLibraryItemId equals model.Id
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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var enabledMappings = await dbContext.SiteModelMappings
                        .AsNoTracking()
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
                        .AsNoTracking()
                        .Where(m => modelIds.Contains(m.Id) && m.IsEnabled)
                        .OrderBy(m => m.DisplayName)
                        .Select(m => new RouteModelItem
                        {
                            ModelName = m.ModelName,
                            DisplayName = m.DisplayName
                        })
                        .ToListAsync(cancellationToken);

                    var modelNameById = await dbContext.ModelLibraryItems
                        .AsNoTracking()
                        .Where(m => modelIds.Contains(m.Id))
                        .ToDictionaryAsync(m => m.Id, m => m.ModelName, cancellationToken);
                    var routedModels = (await dbContext.ProxyRouteRules
                        .AsNoTracking()
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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var sites = await dbContext.Sites
                        .AsNoTracking()
                        .Where(s => s.IsEnabled)
                        .Select(s => new CachedSiteSnapshot
                        {
                            Id = s.Id,
                            Name = s.Name
                        })
                        .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);
                    var modelNamesById = await dbContext.ModelLibraryItems
                        .AsNoTracking()
                        .Where(m => m.IsEnabled)
                        .ToDictionaryAsync(m => m.Id, m => m.ModelName, cancellationToken);
                    var mappings = await dbContext.SiteModelMappings
                        .AsNoTracking()
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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var sites = await dbContext.Sites
                        .AsNoTracking()
                        .Where(s => s.IsEnabled)
                        .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
                    var rules = await dbContext.ProxyRouteRules
                        .AsNoTracking()
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
                            r.IsEnabled
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
                                SiteName = sites.TryGetValue(r.SiteId, out var siteName) ? siteName : "(未知站点)",
                                UpstreamModelName = r.UpstreamModelName,
                                SiteModelName = r.SiteModelName,
                                Priority = r.Priority,
                                ModelPriority = r.ModelPriority,
                                InstancePriority = r.InstancePriority,
                                IsEnabled = r.IsEnabled
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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await dbContext.ProxyAccessKeys
                        .AsNoTracking()
                        .Where(k => k.IsEnabled && !string.IsNullOrWhiteSpace(k.PlainKey))
                        .OrderBy(k => k.KeyName)
                        .Select(k => k.PlainKey)
                        .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await (
                            from rule in dbContext.ProxyRouteRules.AsNoTracking()
                            join site in dbContext.Sites.AsNoTracking() on rule.SiteId equals site.Id
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
                        .ToListAsync(cancellationToken);
                })
            ?? [];
    }

    /// <summary>
    /// 获取已启用模型信息。
    /// </summary>
    public async Task<CachedEnabledModel?> GetEnabledModelAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var models = await GetEnabledModelsAsync(cancellationToken);
        return models.TryGetValue(modelId, out var model)
            ? model
            : null;
    }

    /// <summary>
    /// 获取模型的兜底目标。
    /// </summary>
    public async Task<CachedFallbackTarget?> GetFallbackTargetAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var mappings = await GetFallbackMappingsAsync(cancellationToken);
        return mappings.TryGetValue(modelId, out var mapping)
            ? mapping
            : null;
    }

    /// <summary>
    /// 清除访问密钥缓存。
    /// </summary>
    public void InvalidateAccessKeys()
    {
        _memoryCache.Remove(AccessKeyCacheKey);
        _memoryCache.Remove(DeveloperDefaultAccessKeyCacheKey);
    }

    /// <summary>
    /// 清除运行时设置缓存。
    /// </summary>
    public void InvalidateRuntimeSettings()
    {
        _memoryCache.Remove(RuntimeSettingsCacheKey);
    }

    /// <summary>
    /// 清除路由相关缓存。
    /// </summary>
    public void InvalidateRouteTargets()
    {
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "OpenAI");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "Anthropic");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "all");
        _memoryCache.Remove(ChatModelsCacheKey);
        _memoryCache.Remove(ModelConcurrencyLimitsCacheKey);
        _memoryCache.Remove(RouteEntriesCacheKey);
        _memoryCache.Remove(RouteSiteInstancesCacheKey);
        _memoryCache.Remove(RouteModelsCacheKey);
        _memoryCache.Remove(RouteDiscoveredSitesCacheKey);
        _memoryCache.Remove(RouteRulesByEntryCacheKey);
        _memoryCache.Remove(DeveloperDebugModelsCacheKey);
        _memoryCache.Remove(FallbackMappingsCacheKey);
        _memoryCache.Remove(EnabledModelsCacheKey);
    }

    /// <summary>
    /// 清除模型元数据缓存。
    /// </summary>
    public void InvalidateModelMetadata()
    {
        _memoryCache.Remove(ChatModelsCacheKey);
        _memoryCache.Remove(FallbackMappingsCacheKey);
        _memoryCache.Remove(EnabledModelsCacheKey);
    }

    /// <summary>
    /// 加载访问密钥缓存。
    /// </summary>
    private async Task<Dictionary<string, CachedProxyAccessKey>> GetAccessKeysAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                AccessKeyCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var accessKeys = await dbContext.ProxyAccessKeys
                        .AsNoTracking()
                        .Where(x => x.IsEnabled)
                        .Select(x => new CachedProxyAccessKey
                        {
                            Id = x.Id,
                            AccessKeyHash = x.AccessKeyHash
                        })
                        .ToListAsync(cancellationToken);

                    return accessKeys.ToDictionary(x => x.AccessKeyHash, x => x, StringComparer.Ordinal);
                })
            ?? [];
    }

    /// <summary>
    /// 加载路由目标缓存。
    /// </summary>
    private async Task<IReadOnlyList<CachedProxyRouteTarget>> GetRouteTargetsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                RouteTargetsCacheKeyPrefix + "all",
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    return await (
                            from route in dbContext.ProxyRouteRules.AsNoTracking()
                            join site in dbContext.Sites.AsNoTracking() on route.SiteId equals site.Id
                            where route.IsEnabled && site.IsEnabled
                            select new CachedProxyRouteTarget
                            {
                                RouteId = route.Id,
                                SiteId = site.Id,
                                SiteName = site.Name,
                                ProtocolType = ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic),
                                SupportsOpenAi = site.SupportsOpenAi,
                                SupportsAnthropic = site.SupportsAnthropic,
                                ExternalModelName = route.ExternalModelName,
                                UpstreamModelName = route.UpstreamModelName,
                                SiteModelName = route.SiteModelName,
                                BaseUrl = site.BaseUrl,
                                ApiKey = site.ApiKey,
                                ModelPriority = route.ModelPriority,
                                InstancePriority = route.InstancePriority,
                                Priority = route.Priority
                            })
                        .ToListAsync(cancellationToken);
                })
            ?? [];
    }

    /// <summary>
    /// 加载已启用模型缓存。
    /// </summary>
    private async Task<Dictionary<Guid, CachedEnabledModel>> GetEnabledModelsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                EnabledModelsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var models = await dbContext.ModelLibraryItems
                        .AsNoTracking()
                        .Where(x => x.IsEnabled)
                        .Select(x => new CachedEnabledModel
                        {
                            ModelId = x.Id,
                            ModelName = x.ModelName,
                            DisplayName = x.DisplayName
                        })
                        .ToListAsync(cancellationToken);

                    return models.ToDictionary(x => x.ModelId, x => x);
                })
            ?? [];
    }

    /// <summary>
    /// 加载兜底映射缓存。
    /// </summary>
    private async Task<Dictionary<Guid, CachedFallbackTarget>> GetFallbackMappingsAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                FallbackMappingsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var rawMappings = await (
                            from mapping in dbContext.SiteModelMappings.AsNoTracking()
                            join site in dbContext.Sites.AsNoTracking() on mapping.SiteId equals site.Id
                            join model in dbContext.ModelLibraryItems.AsNoTracking() on mapping.ModelLibraryItemId equals model.Id
                            where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                            select new
                            {
                                ModelId = model.Id,
                                model.ModelName,
                                SiteId = site.Id,
                                SiteName = site.Name,
                                site.SupportsOpenAi,
                                site.SupportsAnthropic,
                                site.BaseUrl,
                                site.ApiKey,
                                SiteModelName = mapping.RemoteModelName
                            })
                        .ToListAsync(cancellationToken);

                    var mappings = rawMappings
                        .GroupBy(x => x.ModelId)
                        .Select(grouped =>
                        {
                            var first = grouped
                                .OrderBy(x => x.SiteName, StringComparer.OrdinalIgnoreCase)
                                .First();

                            return new CachedFallbackTarget
                            {
                                ModelId = grouped.Key,
                                ModelName = first.ModelName,
                                SiteId = first.SiteId,
                                SiteName = first.SiteName,
                                ProtocolType = ResolveSiteProtocolType(first.SupportsOpenAi, first.SupportsAnthropic),
                                BaseUrl = first.BaseUrl,
                                ApiKey = first.ApiKey,
                                SiteModelName = first.SiteModelName
                            };
                        })
                        .ToList();

                    return mappings.ToDictionary(x => x.ModelId, x => x);
                })
            ?? [];
    }

    /// <summary>
    /// 将一个站点模型结果追加到按模型名聚合的发现缓存中，并自动去重。
    /// </summary>
    private static void AddDiscoveredSite(
        IDictionary<string, List<DiscoveredSiteItem>> results,
        string modelName,
        CachedSiteSnapshot site,
        string remoteModelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        if (!results.TryGetValue(modelName, out var items))
        {
            items = [];
            results[modelName] = items;
        }

        if (items.Any(x => x.SiteId == site.Id && string.Equals(x.RemoteModelName, remoteModelName, StringComparison.Ordinal)))
        {
            return;
        }

        items.Add(new DiscoveredSiteItem
        {
            SiteId = site.Id,
            SiteName = site.Name,
            RemoteModelName = remoteModelName,
            SiteEnabled = true
        });
    }

    /// <summary>
    /// 根据站点能力推导协议类型。
    /// </summary>
    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        return supportsOpenAi || !supportsAnthropic ? "OpenAI" : "Anthropic";
    }
}

/// <summary>
/// 缓存中的站点快照。
/// </summary>
internal sealed class CachedSiteSnapshot
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid Id { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 缓存中的代理访问密钥。
/// </summary>
public sealed class CachedProxyAccessKey
{
    /// <summary>
    /// 标识。
    /// </summary>
    public Guid Id { get; set; }
    /// <summary>
    /// 访问密钥哈希值。
    /// </summary>
    public string AccessKeyHash { get; set; } = string.Empty;
}

/// <summary>
/// 缓存中的代理运行时设置。
/// </summary>
public sealed class CachedProxyRuntimeSettings
{
    /// <summary>
    /// 代理请求超时时间（秒）。
    /// </summary>
    public int ProxyRequestTimeoutSeconds { get; set; } = 60;
    /// <summary>
    /// 代理重试次数。
    /// </summary>
    public int ProxyRetryCount { get; set; } = 1;
    /// <summary>
    /// 检测请求超时时间（秒）。
    /// </summary>
    public int DetectionRequestTimeoutSeconds { get; set; } = 60;
    /// <summary>
    /// 检测重试次数。
    /// </summary>
    public int DetectionRetryCount { get; set; } = 0;
    /// <summary>
    /// 检测并发数。
    /// </summary>
    public int DetectionConcurrency { get; set; } = 1;
    /// <summary>
    /// 熔断失败阈值。
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    /// <summary>
    /// 熔断恢复时间（分钟）。
    /// </summary>
    public int CircuitBreakerRecoveryMinutes { get; set; } = 2;
    /// <summary>
    /// 是否自动清理 UsageLogs。
    /// </summary>
    public bool UsageLogAutoCleanupEnabled { get; set; } = true;
    /// <summary>
    /// 是否启用开发者功能。
    /// </summary>
    public bool DeveloperFeaturesEnabled { get; set; }
    /// <summary>
    /// 并发打满时的处理策略：0 = 跳到下一顺位，1 = 排队等待。
    /// </summary>
    public int ConcurrencyMode { get; set; }
    /// <summary>
    /// 并发排队等待的最大时间（秒）。
    /// </summary>
    public int ConcurrencyQueueTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// 缓存中的代理路由目标。
/// </summary>
public sealed class CachedProxyRouteTarget
{
    /// <summary>
    /// 路由标识。
    /// </summary>
    public Guid RouteId { get; set; }
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 是否支持 OpenAI 协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; }
    /// <summary>
    /// 是否支持 Anthropic 协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }
    /// <summary>
    /// 对外模型名称。
    /// </summary>
    public string ExternalModelName { get; set; } = string.Empty;
    /// <summary>
    /// 上游模型名称。
    /// </summary>
    public string UpstreamModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 基础地址。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// 接口密钥。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// 模型优先级。
    /// </summary>
    public int ModelPriority { get; set; }
    /// <summary>
    /// 实例优先级。
    /// </summary>
    public int InstancePriority { get; set; }
    /// <summary>
    /// 优先级。
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 判断是否支持指定协议。
    /// </summary>
    public bool SupportsProtocol(string protocolType)
    {
        if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return SupportsAnthropic;
        }

        return SupportsOpenAi;
    }

    /// <summary>
    /// 返回协议匹配优先级。
    /// </summary>
    public int GetProtocolPriority(string protocolType)
    {
        return SupportsProtocol(protocolType) ? 0 : 1;
    }

    /// <summary>
    /// 为客户端选择可用协议。
    /// </summary>
    public string ResolveProtocolForClient(string clientProtocol)
    {
        if (SupportsProtocol(clientProtocol))
        {
            return clientProtocol;
        }

        return string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            ? "OpenAI"
            : "Anthropic";
    }
}

/// <summary>
/// 缓存中的聊天模型信息。
/// </summary>
public sealed class CachedChatModel
{
    /// <summary>
    /// 模型标识。
    /// </summary>
    public Guid ModelId { get; set; }
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 可用站点数量。
    /// </summary>
    public int AvailableSiteCount { get; set; }
}

/// <summary>
/// 缓存中的已启用模型信息。
/// </summary>
public sealed class CachedEnabledModel
{
    /// <summary>
    /// 模型标识。
    /// </summary>
    public Guid ModelId { get; set; }
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// 缓存中的兜底目标站点。
/// </summary>
public sealed class CachedFallbackTarget
{
    /// <summary>
    /// 模型标识。
    /// </summary>
    public Guid ModelId { get; set; }
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 基础地址。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// 接口密钥。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
}
