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
                            ProxyRetryCount = settings.ProxyRetryCount
                        };
                })
            ?? new CachedProxyRuntimeSettings();
    }

    // 读取指定协议下可用的模型列表，供 /v1/models 等接口复用。
    public async Task<IReadOnlyList<string>> GetEnabledModelNamesAsync(string protocolType, CancellationToken cancellationToken)
    {
        var routes = await GetRouteTargetsAsync(protocolType, cancellationToken);
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
        var routes = await GetRouteTargetsAsync(protocolType, cancellationToken);
        return routes
            .Where(x => string.Equals(x.ExternalModelName, externalModelName, StringComparison.Ordinal))
            .OrderBy(x => x.ModelPriority)
            .ThenBy(x => x.InstancePriority)
            .ThenBy(x => x.Priority)
            .ToList();
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

    private async Task<IReadOnlyList<CachedProxyRouteTarget>> GetRouteTargetsAsync(string protocolType, CancellationToken cancellationToken)
    {
        var cacheKey = RouteTargetsCacheKeyPrefix + protocolType;
        return await _memoryCache.GetOrCreateAsync(
                cacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    return await (
                            from route in dbContext.ProxyRouteRules.AsNoTracking()
                            join site in dbContext.Sites.AsNoTracking() on route.SiteId equals site.Id
                            where route.IsEnabled
                                && site.IsEnabled
                                && site.ProtocolType == protocolType
                            select new CachedProxyRouteTarget
                            {
                                RouteId = route.Id,
                                SiteId = site.Id,
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
}

public sealed class CachedProxyRouteTarget
{
    public Guid RouteId { get; set; }
    public Guid SiteId { get; set; }
    public string ExternalModelName { get; set; } = string.Empty;
    public string UpstreamModelName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int ModelPriority { get; set; }
    public int InstancePriority { get; set; }
    public int Priority { get; set; }
}
