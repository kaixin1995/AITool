using System.Security.Cryptography;
using System.Text;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

// 代理请求元数据缓存，尽量把热路径上的小表查询前移到内存快照。
public sealed class ProxyRequestMetadataCache
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private const string AccessKeyCacheKey = "proxy-access-keys";
    private const string RuntimeSettingsCacheKey = "proxy-runtime-settings";
    private const string RouteTargetsCacheKeyPrefix = "proxy-route-targets:";
    private const string ChatModelsCacheKey = "chat-models";
    private const string EnabledModelsCacheKey = "enabled-models";
    private const string FallbackMappingsCacheKey = "fallback-mappings";
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;

    public ProxyRequestMetadataCache(IMemoryCache memoryCache, IServiceScopeFactory scopeFactory)
    {
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
    }

    // 从缓存中验证访问密钥，避免每次代理请求都查数据库。
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

    // 读取当前运行时设置快照，后台改配置后最多几秒内生效。
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
                            DeveloperFeaturesEnabled = settings.DeveloperFeaturesEnabled
                        };
                })
            ?? new CachedProxyRuntimeSettings();
    }

    // 读取指定协议下可用的模型列表，供 /v1/models 等接口复用。
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

    // 协议中转场景下需要暴露全部外部模型，避免模型列表只显示单一协议候选。
    public async Task<IReadOnlyList<string>> GetEnabledModelNamesAsync(CancellationToken cancellationToken)
    {
        var routes = await GetRouteTargetsAsync(cancellationToken);
        return routes
            .Select(x => x.ExternalModelName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    // 读取指定模型的候选路由快照，避免控制器再做 N+1 站点查询。
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

    // 聊天测试页需要跨协议候选列表，因此单独提供不按协议过滤的入口。
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

    // 聊天页模型列表走缓存，减少站点映射和模型表的组合查询。
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

    // 按模型读取启用状态和主信息，供聊天页快速校验选择项。
    public async Task<CachedEnabledModel?> GetEnabledModelAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var models = await GetEnabledModelsAsync(cancellationToken);
        return models.TryGetValue(modelId, out var model)
            ? model
            : null;
    }

    // 读取无路由规则时的 fallback 站点映射快照。
    public async Task<CachedFallbackTarget?> GetFallbackTargetAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var mappings = await GetFallbackMappingsAsync(cancellationToken);
        return mappings.TryGetValue(modelId, out var mapping)
            ? mapping
            : null;
    }

    // 后台修改密钥后主动清理缓存，避免继续使用旧快照。
    public void InvalidateAccessKeys()
    {
        _memoryCache.Remove(AccessKeyCacheKey);
    }

    // 后台修改运行时设置后主动清理缓存，缩短配置生效时间。
    public void InvalidateRuntimeSettings()
    {
        _memoryCache.Remove(RuntimeSettingsCacheKey);
    }

    // 后台修改路由或站点后主动清理路由快照。
    public void InvalidateRouteTargets()
    {
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "OpenAI");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "Anthropic");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "all");
        _memoryCache.Remove(ChatModelsCacheKey);
        _memoryCache.Remove(FallbackMappingsCacheKey);
        _memoryCache.Remove(EnabledModelsCacheKey);
    }

    // 后台变更模型库或映射后主动清理聊天相关缓存。
    public void InvalidateModelMetadata()
    {
        _memoryCache.Remove(ChatModelsCacheKey);
        _memoryCache.Remove(FallbackMappingsCacheKey);
        _memoryCache.Remove(EnabledModelsCacheKey);
    }

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

    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        return supportsOpenAi || !supportsAnthropic ? "OpenAI" : "Anthropic";
    }
}

public sealed class CachedProxyAccessKey
{
    public Guid Id { get; set; }
    public string AccessKeyHash { get; set; } = string.Empty;
}

public sealed class CachedProxyRuntimeSettings
{
    public int ProxyRequestTimeoutSeconds { get; set; } = 60;
    public int ProxyRetryCount { get; set; } = 1;
    public int DetectionRequestTimeoutSeconds { get; set; } = 60;
    public int DetectionRetryCount { get; set; } = 0;
    public int DetectionConcurrency { get; set; } = 1;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public int CircuitBreakerRecoveryMinutes { get; set; } = 2;
    public bool UsageLogAutoCleanupEnabled { get; set; } = true;
    public bool DeveloperFeaturesEnabled { get; set; }
}

public sealed class CachedProxyRouteTarget
{
    public Guid RouteId { get; set; }
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public bool SupportsOpenAi { get; set; }
    public bool SupportsAnthropic { get; set; }
    public string ExternalModelName { get; set; } = string.Empty;
    public string UpstreamModelName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int ModelPriority { get; set; }
    public int InstancePriority { get; set; }
    public int Priority { get; set; }

    public bool SupportsProtocol(string protocolType)
    {
        if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return SupportsAnthropic;
        }

        return SupportsOpenAi;
    }

    public int GetProtocolPriority(string protocolType)
    {
        return SupportsProtocol(protocolType) ? 0 : 1;
    }

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

public sealed class CachedChatModel
{
    public Guid ModelId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int AvailableSiteCount { get; set; }
}

public sealed class CachedEnabledModel
{
    public Guid ModelId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class CachedFallbackTarget
{
    public Guid ModelId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
}
