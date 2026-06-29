using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Common;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Pages.Admin.ClientSimulator;
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
    /// 聊天模型候选站点缓存键。
    /// </summary>
    private const string ChatTargetsCacheKey = "chat-targets";
    /// <summary>
    /// 模型并发限制缓存键。
    /// </summary>
    private const string ModelConcurrencyLimitsCacheKey = "model-concurrency-limits";
    /// <summary>
    /// 启用站点名称缓存键。
    /// </summary>
    private const string EnabledSiteNamesCacheKey = "enabled-site-names";
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
    /// 正在等待活跃调用结束的路由快照，确保调用中的模型不会被新顺序影响。
    /// </summary>
    private readonly Dictionary<string, DeferredRouteTargetsRefresh> _deferredRouteTargetsByModel = new(StringComparer.Ordinal);
    /// <summary>
    /// 延迟路由快照状态锁。
    /// </summary>
    private readonly object _deferredRouteTargetsLock = new();
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
    /// 解析 AccessKey 允许访问的路由入口名称集合。
    /// 返回 null 表示允许全部路由（AllowedRouteNames 为空），非 null 表示只能访问集合中的路由。
    /// </summary>
    public static HashSet<string>? GetAllowedRouteNames(CachedProxyAccessKey? accessKey)
    {
        if (accessKey is null || string.IsNullOrWhiteSpace(accessKey.AllowedRouteNames))
        {
            return null;
        }

        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(accessKey.AllowedRouteNames);
            return names is null || names.Count == 0
                ? null
                : new HashSet<string>(names, StringComparer.Ordinal);
        }
        catch
        {
            // JSON 解析失败时降级为允许全部，避免误锁。
            return null;
        }
    }

    /// <summary>
    /// 按密钥 Id 从缓存中查找 AccessKey（用于 WebSocket 等只有 keyId 没有 key 对象的场景）。
    /// </summary>
    public async Task<CachedProxyAccessKey?> GetAccessKeyByIdAsync(Guid accessKeyId, CancellationToken cancellationToken)
    {
        var accessKeys = await GetAccessKeysAsync(cancellationToken);
        return accessKeys.Values.FirstOrDefault(k => k.Id == accessKeyId);
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
                        
                        .FirstAsync(x => x.Id == 1, cancellationToken);

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
                            ConversationLogEnabled = settings.ConversationLogEnabled,
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
        var routes = await GetEffectiveRouteTargetsAsync(externalModelName, cancellationToken);
        return SortRouteTargets(routes)
            .Where(x => x.IsAvailableAt(TimeOnly.FromDateTime(DateTime.Now)))
            .ToList();
    }

    /// <summary>
    /// 获取模型对应的路由目标。
    /// </summary>
    public async Task<IReadOnlyList<CachedProxyRouteTarget>> GetRouteTargetsForModelAsync(
        string externalModelName,
        CancellationToken cancellationToken)
    {
        var routes = await GetEffectiveRouteTargetsAsync(externalModelName, cancellationToken);
        return SortRouteTargets(routes)
            .Where(x => x.IsAvailableAt(TimeOnly.FromDateTime(DateTime.Now)))
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

                    // SqlSugar 不支持 LINQ query syntax 的多表 join + group by，
                    // 改为先各自读出再在内存连接（结果有 5 秒缓存，非每请求执行，性能可接受）。
                    var models = (
                            from model in await dbContext.ModelLibraryItems.ToListAsync(cancellationToken)
                            join mapping in await dbContext.SiteModelMappings.ToListAsync(cancellationToken) on model.Id equals mapping.ModelLibraryItemId
                            join site in await dbContext.Sites.ToListAsync(cancellationToken) on mapping.SiteId equals site.Id
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
        return await _memoryCache.GetOrCreateAsync(
                ChatTargetsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var mappings = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var sites = await dbContext.Sites.ToListAsync(cancellationToken);
                    var modelItems = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);

                    return (
                            from mapping in mappings
                            join site in sites on mapping.SiteId equals site.Id
                            join model in modelItems on mapping.ModelLibraryItemId equals model.Id
                            where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                            orderby model.DisplayName, site.Name, mapping.RemoteModelName
                            select new CachedChatTarget
                            {
                                MappingId = mapping.Id,
                                ModelId = model.Id,
                                ModelDisplayName = model.DisplayName,
                                SiteId = site.Id,
                                SiteName = site.Name,
                                ProtocolType = ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic),
                                BaseUrl = site.BaseUrl,
                                EndpointPathMode = site.EndpointPathMode,
                                ApiKey = site.ApiKey,
                                SiteModelName = mapping.RemoteModelName
                            })
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
    /// 获取启用站点名称缓存。
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetEnabledSiteNamesAsync(CancellationToken cancellationToken)
    {
        return await _memoryCache.GetOrCreateAsync(
                EnabledSiteNamesCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var candidateCounts = (await dbContext.ProxyRouteRules.ToListAsync(cancellationToken))
                        .GroupBy(x => x.ExternalModelName)
                        .Select(g => new { EntryName = g.Key, CandidateCount = g.Count() })
                        .ToList();

                    var storedEntries = await dbContext.ProxyRouteEntries
                        
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
                    var mappings = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var sites = await dbContext.Sites.ToListAsync(cancellationToken);
                    var modelItems = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
                    return (
                            from mapping in mappings
                            join site in sites on mapping.SiteId equals site.Id
                            join model in modelItems on mapping.ModelLibraryItemId equals model.Id
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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

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
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await dbContext.ProxyAccessKeys
                        
                        .Where(k => k.IsEnabled && !string.IsNullOrWhiteSpace(k.PlainKey))
                        .OrderBy(k => k.KeyName)
                        .Select(k => k.PlainKey)
                        .FirstAsync(cancellationToken) ?? string.Empty;
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
                    var rules = await dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
                    var sites = await dbContext.Sites.ToListAsync(cancellationToken);
                    return (
                            from rule in rules
                            join site in sites on rule.SiteId equals site.Id
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
        InvalidateRuntimeRouteTargets();
        InvalidateAdminRouteMetadata();
    }

    /// <summary>
    /// 清除运行时代理使用的路由目标缓存。
    /// </summary>
    public void InvalidateRuntimeRouteTargets()
    {
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "OpenAI");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "Anthropic");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "all");
        _memoryCache.Remove(ChatModelsCacheKey);
        _memoryCache.Remove(ChatTargetsCacheKey);
        _memoryCache.Remove(ModelConcurrencyLimitsCacheKey);
        _memoryCache.Remove(EnabledSiteNamesCacheKey);
        _memoryCache.Remove(DeveloperDebugModelsCacheKey);
        _memoryCache.Remove(FallbackMappingsCacheKey);
        _memoryCache.Remove(EnabledModelsCacheKey);
    }

    /// <summary>
    /// 清除后台路由配置页使用的管理缓存。
    /// </summary>
    public void InvalidateAdminRouteMetadata()
    {
        _memoryCache.Remove(RouteEntriesCacheKey);
        _memoryCache.Remove(RouteSiteInstancesCacheKey);
        _memoryCache.Remove(RouteModelsCacheKey);
        _memoryCache.Remove(RouteDiscoveredSitesCacheKey);
        _memoryCache.Remove(RouteRulesByEntryCacheKey);
    }

    /// <summary>
    /// 在指定模型仍有活跃调用时保留旧路由快照，等调用结束后再让新顺序进入运行时。
    /// </summary>
    public void DeferRuntimeRouteTargetsRefresh(
        string externalModelName,
        IReadOnlyCollection<ActiveRouteTargetSnapshot> activeRouteTargets,
        IReadOnlyList<CachedProxyRouteTarget> previousRoutes)
    {
        var normalizedModelName = (externalModelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedModelName) || activeRouteTargets.Count == 0)
        {
            InvalidateRuntimeRouteTargets();
            return;
        }

        var pendingSlots = BuildPendingActiveSlots(activeRouteTargets);
        if (pendingSlots.Count == 0)
        {
            InvalidateRuntimeRouteTargets();
            return;
        }

        lock (_deferredRouteTargetsLock)
        {
            if (_deferredRouteTargetsByModel.TryGetValue(normalizedModelName, out var existingRefresh))
            {
                foreach (var pendingSlot in pendingSlots)
                {
                    if (!existingRefresh.PendingActiveSlots.TryGetValue(pendingSlot.Key, out var existingSlots))
                    {
                        existingRefresh.PendingActiveSlots[pendingSlot.Key] = pendingSlot.Value;
                        continue;
                    }

                    foreach (var slotId in pendingSlot.Value)
                    {
                        existingSlots.Add(slotId);
                    }
                }
            }
            else
            {
                _deferredRouteTargetsByModel[normalizedModelName] = new DeferredRouteTargetsRefresh
                {
                    PendingActiveSlots = pendingSlots,
                    PreviousRoutes = previousRoutes.ToList()
                };
            }
        }

        InvalidateRuntimeRouteTargets();
    }

    /// <summary>
    /// 活跃调用结束时释放对应的路由快照，全部结束后刷新运行时路由缓存。
    /// </summary>
    public void CompleteDeferredRuntimeRouteTarget(Guid siteId, string siteModelName, long activeSlotId)
    {
        if (string.IsNullOrWhiteSpace(siteModelName))
        {
            return;
        }

        var completedTarget = new RouteTargetIdentity(siteId, siteModelName);
        var shouldInvalidateRuntimeRoutes = false;
        lock (_deferredRouteTargetsLock)
        {
            foreach (var item in _deferredRouteTargetsByModel.ToList())
            {
                if (!item.Value.PendingActiveSlots.TryGetValue(completedTarget, out var pendingSlots) || !pendingSlots.Remove(activeSlotId))
                {
                    continue;
                }

                if (pendingSlots.Count == 0)
                {
                    item.Value.PendingActiveSlots.Remove(completedTarget);
                }

                if (item.Value.PendingActiveSlots.Count == 0)
                {
                    _deferredRouteTargetsByModel.Remove(item.Key);
                    shouldInvalidateRuntimeRoutes = true;
                }
            }
        }

        if (shouldInvalidateRuntimeRoutes)
        {
            InvalidateRuntimeRouteTargets();
        }
    }

    /// <summary>
    /// 将活跃调用快照转换成需要等待释放的槽位集合。
    /// </summary>
    private static Dictionary<RouteTargetIdentity, HashSet<long>> BuildPendingActiveSlots(IReadOnlyCollection<ActiveRouteTargetSnapshot> activeRouteTargets)
    {
        var pendingSlots = new Dictionary<RouteTargetIdentity, HashSet<long>>(RouteTargetIdentityComparer.Instance);
        foreach (var activeRouteTarget in activeRouteTargets)
        {
            if (activeRouteTarget.ActiveSlotIds.Count == 0)
            {
                continue;
            }

            pendingSlots[activeRouteTarget.RouteTarget] = activeRouteTarget.ActiveSlotIds.ToHashSet();
        }

        return pendingSlots;
    }

    /// <summary>
    /// 获取指定模型当前对运行时可见的路由快照。
    /// </summary>
    private async Task<IReadOnlyList<CachedProxyRouteTarget>> GetEffectiveRouteTargetsAsync(string externalModelName, CancellationToken cancellationToken)
    {
        var normalizedModelName = (externalModelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedModelName))
        {
            return [];
        }

        if (TryGetDeferredRouteTargets(normalizedModelName, out var deferredRoutes))
        {
            return deferredRoutes;
        }

        var routes = await GetRouteTargetsAsync(cancellationToken);
        return routes
            .Where(x => string.Equals(x.ExternalModelName, normalizedModelName, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// 尝试读取仍在保护期内的旧路由快照。
    /// </summary>
    private bool TryGetDeferredRouteTargets(string externalModelName, out IReadOnlyList<CachedProxyRouteTarget> routes)
    {
        lock (_deferredRouteTargetsLock)
        {
            if (_deferredRouteTargetsByModel.TryGetValue(externalModelName, out var deferredRefresh))
            {
                routes = deferredRefresh.PreviousRoutes.ToList();
                return true;
            }
        }

        routes = [];
        return false;
    }

    /// <summary>
    /// 按后台配置顺序排序路由候选，协议不匹配时由控制器负责兼容转发。
    /// </summary>
    private static IOrderedEnumerable<CachedProxyRouteTarget> SortRouteTargets(IEnumerable<CachedProxyRouteTarget> routes)
    {
        return routes
            .OrderBy(x => x.ModelPriority)
            .ThenBy(x => x.InstancePriority)
            .ThenBy(x => x.Priority);
    }

    /// <summary>
    /// 清除模型元数据缓存。
    /// </summary>
    public void InvalidateModelMetadata()
    {
        _memoryCache.Remove(ChatModelsCacheKey);
        _memoryCache.Remove(ChatTargetsCacheKey);
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
                        
                        .Where(x => x.IsEnabled)
                        .Select(x => new CachedProxyAccessKey
                        {
                            Id = x.Id,
                            AccessKeyHash = x.AccessKeyHash,
                            AllowedRouteNames = x.AllowedRouteNames
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

                    var routes = await dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
                    var sites = await dbContext.Sites.ToListAsync(cancellationToken);

                    return (
                            from route in routes
                            join site in sites on route.SiteId equals site.Id
                            where route.IsEnabled && site.IsEnabled
                            select new CachedProxyRouteTarget
                            {
                                RouteId = route.Id,
                                SiteId = site.Id,
                                SiteName = site.Name,
                                ProtocolType = ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic),
                                EndpointPathMode = site.EndpointPathMode,
                                SupportsOpenAi = site.SupportsOpenAi,
                                SupportsAnthropic = site.SupportsAnthropic,
                                ExternalModelName = route.ExternalModelName,
                                UpstreamModelName = route.UpstreamModelName,
                                SiteModelName = route.SiteModelName,
                                BaseUrl = site.BaseUrl,
                                ApiKey = site.ApiKey,
                                ModelPriority = route.ModelPriority,
                                InstancePriority = route.InstancePriority,
                                Priority = route.Priority,
                                AvailabilityMode = NormalizeAvailabilityMode(route.AvailabilityMode),
                                TimeRangesJson = NormalizeTimeRangesJson(route.AvailabilityMode, route.TimeRangesJson)
                            })
                        .ToList();
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

                    var mappingsData = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var sitesData = await dbContext.Sites.ToListAsync(cancellationToken);
                    var modelsData = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);

                    var rawMappings = (
                            from mapping in mappingsData
                            join site in sitesData on mapping.SiteId equals site.Id
                            join model in modelsData on mapping.ModelLibraryItemId equals model.Id
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
                                site.EndpointPathMode,
                                site.ApiKey,
                                SiteModelName = mapping.RemoteModelName
                            })
                        .ToList();

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
                                EndpointPathMode = first.EndpointPathMode,
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
        if (!supportsOpenAi && !supportsAnthropic)
        {
            return "Responses";
        }

        return supportsOpenAi || !supportsAnthropic ? "OpenAI" : "Anthropic";
    }

    /// <summary>
    /// 规范化时间可用性模式，旧值和异常值统一按全天可用处理。
    /// </summary>
    internal static string NormalizeAvailabilityMode(string? mode)
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
    internal static string NormalizeTimeRangesJson(string? mode, string? timeRangesJson)
    {
        if (NormalizeAvailabilityMode(mode) == "AllDay" || string.IsNullOrWhiteSpace(timeRangesJson))
        {
            return string.Empty;
        }

        try
        {
            var ranges = JsonSerializer.Deserialize<List<CachedRouteTimeRange>>(timeRangesJson, JsonSerializerPresets.CaseInsensitive) ?? [];
            ranges = ranges
                .Where(x => IsValidTimeText(x.Start) && IsValidTimeText(x.End))
                .Select(x => new CachedRouteTimeRange { Start = x.Start.Trim(), End = x.End.Trim() })
                .ToList();
            return ranges.Count == 0 ? string.Empty : JsonSerializer.Serialize(ranges, JsonSerializerPresets.CamelCase);
        }
        catch (JsonException)
        {
            return string.Empty;
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
/// 延迟刷新路由目标时保留的旧快照。
/// </summary>
internal sealed class DeferredRouteTargetsRefresh
{
    /// <summary>
    /// 需要等待结束的调用槽位集合。
    /// </summary>
    public Dictionary<RouteTargetIdentity, HashSet<long>> PendingActiveSlots { get; init; } = new(RouteTargetIdentityComparer.Instance);
    /// <summary>
    /// 保存排序变更前的运行时路由列表。
    /// </summary>
    public IReadOnlyList<CachedProxyRouteTarget> PreviousRoutes { get; init; } = [];
}

/// <summary>
/// 路由保存瞬间正在执行的站点模型快照。
/// </summary>
public readonly record struct ActiveRouteTargetSnapshot(RouteTargetIdentity RouteTarget, IReadOnlyList<long> ActiveSlotIds);

/// <summary>
/// 用于匹配运行时活跃调用的站点模型标识。
/// </summary>
public readonly record struct RouteTargetIdentity(Guid SiteId, string SiteModelName);

/// <summary>
/// 站点模型标识比较器，模型名保持大小写敏感以匹配站点映射唯一键。
/// </summary>
internal sealed class RouteTargetIdentityComparer : IEqualityComparer<RouteTargetIdentity>
{
    /// <summary>
    /// 单例实例。
    /// </summary>
    public static RouteTargetIdentityComparer Instance { get; } = new();

    /// <summary>
    /// 比较两个站点模型标识是否相同。
    /// </summary>
    public bool Equals(RouteTargetIdentity x, RouteTargetIdentity y)
    {
        return x.SiteId == y.SiteId && string.Equals(x.SiteModelName, y.SiteModelName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 计算站点模型标识的哈希值。
    /// </summary>
    public int GetHashCode(RouteTargetIdentity obj)
    {
        return HashCode.Combine(obj.SiteId, StringComparer.Ordinal.GetHashCode(obj.SiteModelName ?? string.Empty));
    }
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
    /// <summary>
    /// 允许访问的路由入口名称（JSON 数组）。空串表示允许全部路由。
    /// </summary>
    public string AllowedRouteNames { get; set; } = string.Empty;
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
    /// 是否启用对话记录功能。
    /// </summary>
    public bool ConversationLogEnabled { get; set; } = true;
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
/// 缓存中的每日时间范围。
/// </summary>
internal sealed class CachedRouteTimeRange
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
    /// 接口路径模式。
    /// </summary>
    public string EndpointPathMode { get; set; } = AITool.Application.Sites.SiteEndpointPathResolver.StandardRoot;
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
    /// 时间可用性模式，空值兼容为全天可用。
    /// </summary>
    public string AvailabilityMode { get; set; } = "AllDay";
    /// <summary>
    /// 每日时间范围 JSON，空值表示全天可用。
    /// </summary>
    public string TimeRangesJson { get; set; } = string.Empty;

    /// <summary>
    /// 判断当前路由在指定本地时间是否可用。
    /// </summary>
    public bool IsAvailableAt(TimeOnly currentTime)
    {
        var mode = ProxyRequestMetadataCache.NormalizeAvailabilityMode(AvailabilityMode);
        var timeRangesJson = ProxyRequestMetadataCache.NormalizeTimeRangesJson(mode, TimeRangesJson);
        if (mode == "AllDay" || string.IsNullOrWhiteSpace(timeRangesJson))
        {
            return true;
        }

        var ranges = JsonSerializer.Deserialize<List<CachedRouteTimeRange>>(timeRangesJson, JsonSerializerPresets.CaseInsensitive) ?? [];
        var matched = ranges.Any(x => IsTimeInRange(currentTime, TimeOnly.ParseExact(x.Start, "HH:mm"), TimeOnly.ParseExact(x.End, "HH:mm")));
        return mode == "AvailableOnly" ? matched : !matched;
    }

    /// <summary>
    /// 判断当前时间是否命中范围，支持 23:00~02:00 这类跨天配置。
    /// </summary>
    private static bool IsTimeInRange(TimeOnly currentTime, TimeOnly startTime, TimeOnly endTime)
    {
        return startTime <= endTime
            ? currentTime >= startTime && currentTime <= endTime
            : currentTime >= startTime || currentTime <= endTime;
    }

    /// <summary>
    /// 判断是否支持指定协议。
    /// </summary>
    public bool SupportsProtocol(string protocolType)
    {
        if (string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase);
        }

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

        if (string.Equals(clientProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase) && SupportsProtocol("Responses"))
        {
            return "Responses";
        }

        if (string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase) && SupportsProtocol("Responses"))
        {
            return "Responses";
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
/// 缓存中的聊天候选站点模型。
/// </summary>
public sealed class CachedChatTarget
{
    /// <summary>
    /// 站点模型映射标识。
    /// </summary>
    public Guid MappingId { get; set; }
    /// <summary>
    /// 模型标识。
    /// </summary>
    public Guid ModelId { get; set; }
    /// <summary>
    /// 模型显示名称。
    /// </summary>
    public string ModelDisplayName { get; set; } = string.Empty;
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
    /// 接口路径模式。
    /// </summary>
    public string EndpointPathMode { get; set; } = AITool.Application.Sites.SiteEndpointPathResolver.StandardRoot;
    /// <summary>
    /// 接口密钥。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
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
    /// 接口路径模式。
    /// </summary>
    public string EndpointPathMode { get; set; } = AITool.Application.Sites.SiteEndpointPathResolver.StandardRoot;
    /// <summary>
    /// 接口密钥。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
}
