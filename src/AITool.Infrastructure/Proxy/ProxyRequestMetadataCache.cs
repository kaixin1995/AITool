using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.CoreRuntime;
using AITool.Application.Proxy;
using AITool.Domain.Codex;
using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 代理请求元数据缓存。
/// </summary>
public sealed partial class ProxyRequestMetadataCache
{
    /// <summary>
    /// 缓存有效时长。
    /// 所有业务写操作（站点/模型/路由/密钥/兼容规则/运行时设置/Codex/Google/Kimi 账号等，含全部
    /// 后台服务的 token 刷新/配额巡检写入）都有对应的 Invalidate* 显式失效，变更即时生效；
    /// TTL 仅作"漏网写入"（绕过应用层的改库、未来新代码漏调失效）的兜底自愈。
    /// 300 秒在把全表重建的对象图制造量（GC/LOH 压力）降低一个数量级的同时，
    /// 把极端情况下的脏数据窗口限制在 5 分钟内。
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(300);
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
    /// Codex 账号列表缓存键（账号少且低频变更，巡检高频读，适合缓存）。
    /// </summary>
    private const string CodexAccountsCacheKey = "codex-accounts";
    private const string GoogleAccountsCacheKey = "google-accounts";
    private const string KimiAccountsCacheKey = "kimi-accounts";
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
    /// 缓存键级别的加载锁，避免冷缓存并发 miss 重复执行全表查询；锁条目在无人使用时自动回收。
    /// </summary>
    private readonly KeyedAsyncLock _cacheLoadLocks = new();
    /// <summary>
    /// 每个缓存键的失效代数。构建期间若发生显式失效，旧构建结果不会重新写回缓存。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _cacheGenerations = new(StringComparer.Ordinal);
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
    /// <summary>
    /// Core 运行时配置提供者。
    /// 仅在 Core 宿主中注册，用于从 Admin 下发的配置快照读取代理运行时数据。
    /// 在 Web/Admin 宿主中为 null，此时回退到数据库查询。
    /// </summary>
    private readonly ICoreRuntimeConfigProvider? _configProvider;

    /// <summary>
    /// 初始化代理请求元数据缓存。
    /// Web/Admin 宿主省略 configProvider 参数，通过数据库查询获取缓存数据；
    /// Core 宿主传入 configProvider，从 Admin 下发的配置快照直接读取运行时数据。
    /// </summary>
    public ProxyRequestMetadataCache(
        IMemoryCache memoryCache,
        IServiceScopeFactory scopeFactory,
        ICoreRuntimeConfigProvider? configProvider = null)
    {
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
        _configProvider = configProvider;
    }

    /// <summary>
    /// 创建独立的 SqlSugarClient（有自己的连接），用完即释放。
    /// 所有缓存未命中时的查库都走这个方法，避免与单例 SqlSugarScope 并发竞态。
    /// </summary>
    private SqlSugar.ISqlSugarClient CreateIndependentClient()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = dbContext.Client.CopyNew();
        // 连接级 PRAGMA 不继承单例连接，需手动设置
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        client.Ado.ExecuteCommand("PRAGMA cache_size=-65536;");
        return client;
    }

    /// <summary>
    /// 读取或构建缓存项，并确保同一键在冷缓存期间只执行一次构建委托。
    /// </summary>
    private async Task<T?> GetOrCreateCachedAsync<T>(
        string key,
        Func<ICacheEntry, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(key, out T? cached))
        {
            return cached;
        }

        using (await _cacheLoadLocks.WaitAsync(key, cancellationToken))
        {
            if (_memoryCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var generation = GetCacheGeneration(key);
            using var entry = _memoryCache.CreateEntry(key);
            var value = await factory(entry);
            if (generation == GetCacheGeneration(key))
            {
                entry.Value = value;
            }

            return value;
        }
    }

    private long GetCacheGeneration(string key)
    {
        return _cacheGenerations.TryGetValue(key, out var generation) ? generation : 0;
    }

    private void InvalidateCacheKey(string key)
    {
        _cacheGenerations.AddOrUpdate(
            key,
            1,
            static (_, generation) => generation == long.MaxValue ? 0 : generation + 1);
        _memoryCache.Remove(key);
    }

    private void InvalidateCacheKeys(params string[] keys)
    {
        foreach (var key in keys)
        {
            InvalidateCacheKey(key);
        }
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
        // Core 宿主：从 Admin 下发的配置快照直接读取运行时设置
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    RuntimeSettingsCacheKey,
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        var s = snapshot?.RuntimeSettings;

                        // 快照中未包含运行时设置时使用默认值
                        return Task.FromResult(s is null
                            ? new CachedProxyRuntimeSettings()
                            : new CachedProxyRuntimeSettings
                            {
                                ProxyRequestTimeoutSeconds = s.ProxyRequestTimeoutSeconds,
                                ProxyRetryCount = s.ProxyRetryCount,
                                RateLimitRetryCount = s.RateLimitRetryCount,
                                CircuitBreakerFailureThreshold = s.CircuitBreakerFailureThreshold,
                                CircuitBreakerRecoveryMinutes = s.CircuitBreakerRecoveryMinutes,
                                ConversationLogEnabled = s.ConversationLogEnabled,
                                ConcurrencyMode = s.ConcurrencyMode,
                                ConcurrencyQueueTimeoutSeconds = s.ConcurrencyQueueTimeoutSeconds,
                                DeveloperFeaturesEnabled = s.DeveloperFeaturesEnabled,
                                DeveloperTraceEnabled = s.DeveloperTraceEnabled,
                                DeveloperFailureDumpEnabled = s.DeveloperFailureDumpEnabled,
                                DeveloperSimulatorEnabled = s.DeveloperSimulatorEnabled,
                                DeveloperProtocolDiagnosticsEnabled = s.DeveloperProtocolDiagnosticsEnabled,
                                DeveloperSqlMigrationsEnabled = s.DeveloperSqlMigrationsEnabled
                            });
                    })
                ?? new CachedProxyRuntimeSettings();
        }

        // Web/Admin 宿主：从数据库查询完整的运行时设置。
        return await GetOrCreateCachedAsync(
                RuntimeSettingsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    // 用独立连接查库，避免与单例 SqlSugarScope 并发竞态
                    using var independentClient = CreateIndependentClient();
                    var settings = await independentClient.Queryable<AITool.Domain.Operations.SystemRuntimeSettings>()
                        .Where(x => x.Id == 1)
                        .FirstAsync(cancellationToken);

                    return settings is null
                        ? new CachedProxyRuntimeSettings()
                        : new CachedProxyRuntimeSettings
                        {
                            ProxyRequestTimeoutSeconds = settings.ProxyRequestTimeoutSeconds,
                            ProxyStreamIdleTimeoutSeconds = settings.ProxyStreamIdleTimeoutSeconds,
                            ProxyRetryCount = settings.ProxyRetryCount,
                            RateLimitRetryCount = settings.RateLimitRetryCount,
                            DetectionRequestTimeoutSeconds = settings.DetectionRequestTimeoutSeconds,
                            DetectionRetryCount = settings.DetectionRetryCount,
                            DetectionConcurrency = settings.DetectionConcurrency,
                            CircuitBreakerFailureThreshold = settings.CircuitBreakerFailureThreshold,
                            CircuitBreakerRecoveryMinutes = settings.CircuitBreakerRecoveryMinutes,
                            UsageLogAutoCleanupEnabled = settings.UsageLogAutoCleanupEnabled,
                            DeveloperFeaturesEnabled = settings.DeveloperFeaturesEnabled,
                            DeveloperTraceEnabled = settings.DeveloperTraceEnabled,
                            DeveloperFailureDumpEnabled = settings.DeveloperFailureDumpEnabled,
                            DeveloperSimulatorEnabled = settings.DeveloperSimulatorEnabled,
                            DeveloperProtocolDiagnosticsEnabled = settings.DeveloperProtocolDiagnosticsEnabled,
                            DeveloperSqlMigrationsEnabled = settings.DeveloperSqlMigrationsEnabled,
                            ConcurrencyMode = settings.ConcurrencyMode,
                            ConcurrencyQueueTimeoutSeconds = settings.ConcurrencyQueueTimeoutSeconds,
                            OAuthFeaturesEnabled = settings.OAuthFeaturesEnabled,
                            OAuthInspectionEnabled = settings.OAuthInspectionEnabled,
                            OAuthInspectionIntervalSeconds = settings.OAuthInspectionIntervalSeconds,
                            OAuthQuotaMaxCacheHours = settings.OAuthQuotaMaxCacheHours,
                            OAuthAutoDisableThresholdPercent = settings.OAuthAutoDisableThresholdPercent,
                            OAuthInspectionCacheEnabled = settings.OAuthInspectionCacheEnabled
                        };
                }, cancellationToken)
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
    /// 获取所有路由候选（含多 Key 展开后的每条候选），供调试页按 CircuitKey 反查路由/站点/Key 信息。
    /// 不过滤模型名和可用性，因为熔断状态可能对应任意候选。
    /// </summary>
    public async Task<IReadOnlyList<CachedProxyRouteTarget>> GetAllRouteTargetsAsync(CancellationToken cancellationToken)
    {
        return await GetRouteTargetsAsync(cancellationToken);
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
        InvalidateCacheKeys(AccessKeyCacheKey, DeveloperDefaultAccessKeyCacheKey);
    }

    /// <summary>
    /// 清除 Codex 账号列表缓存。账号发生增删改（额度更新/启停/token刷新/冷却/管理后台操作）后调用。
    /// </summary>
    public void InvalidateCodexAccounts()
    {
        InvalidateCacheKey(CodexAccountsCacheKey);
    }

    /// <summary>
    /// 清除 Google 账号列表缓存（含路由目标里的 project 映射，一并失效路由缓存）。
    /// 账号发生增删改（额度更新/启停/token刷新/管理后台操作）后调用。
    /// </summary>
    public void InvalidateGoogleAccounts()
    {
        InvalidateCacheKey(GoogleAccountsCacheKey);
        InvalidateRouteTargets();
    }

    /// <summary>
    /// 清除 Kimi 账号列表缓存（一并失效路由缓存）。
    /// </summary>
    public void InvalidateKimiAccounts()
    {
        InvalidateCacheKey(KimiAccountsCacheKey);
        InvalidateRouteTargets();
    }

    /// <summary>
    /// 兼容规则集发生增删改后调用。规则随路由目标一起缓存，故复用路由缓存失效。
    /// </summary>
    public void InvalidateCompatibilityProfiles()
    {
        InvalidateRouteTargets();
    }

    /// <summary>
    /// 获取待巡检的 Codex 账号列表（未被功能总开关禁用，按最近检查时间升序）。
    /// 走缓存，账号变更后需调 <see cref="InvalidateCodexAccounts"/> 失效。
    /// </summary>
    public async Task<List<CodexAccount>> GetCodexAccountsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主（_configProvider 非 null）没有 AppDbContext，直接返回空列表避免解析异常。
        // Core 不缓存 Codex 账号实体，巡检逻辑只在 Admin 运行。
        if (_configProvider is not null)
        {
            return [];
        }

        // 返回浅拷贝：调用方（额度巡检/自动禁用等）会原地修改这些实体并回写，
        // 共享同一实例会污染缓存内容并与其他并发调用方互相踩踏。
        var cached = await GetOrCreateCachedAsync(
                CodexAccountsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await dbContext.CodexAccounts
                        .Where(a => !a.DisabledByFeatureToggle)
                        .OrderBy(a => a.LastQuotaCheckedAt)
                        .ToListAsync(cancellationToken);
                }, cancellationToken)
            ?? [];

        return cached.Select(static a => a.Clone()).ToList();
    }

    /// <summary>
    /// 获取待巡检/刷新的 Google 账号列表（未被功能总开关禁用，按最近检查时间升序）。
    /// 走缓存，账号变更后需调 <see cref="InvalidateGoogleAccounts"/> 失效。
    /// </summary>
    public async Task<List<Domain.Google.GoogleAccount>> GetGoogleAccountsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主无数据库：OAuth 账号缓存仅 Admin 侧使用，Core 直接返回空列表。
        if (_configProvider is not null)
        {
            return [];
        }

        // 返回浅拷贝：调用方（额度巡检/后台刷新等）会原地修改这些实体并回写，
        // 共享同一实例会污染缓存内容并与其他并发调用方互相踩踏。
        var cached = await GetOrCreateCachedAsync(
                GoogleAccountsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await dbContext.GoogleAccounts
                        .Where(a => !a.DisabledByFeatureToggle && !a.DisabledByUpstream)
                        .OrderBy(a => a.LastQuotaCheckedAt)
                        .ToListAsync(cancellationToken);
                }, cancellationToken)
            ?? [];

        return cached.Select(static a => a.Clone()).ToList();
    }

    /// <summary>
    /// 获取待巡检/刷新的 Kimi 账号列表。
    /// 走缓存，账号变更后需调 <see cref="InvalidateKimiAccounts"/> 失效。
    /// </summary>
    public async Task<List<Domain.Kimi.KimiAccount>> GetKimiAccountsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主无数据库：OAuth 账号缓存仅 Admin 侧使用，Core 直接返回空列表。
        if (_configProvider is not null)
        {
            return [];
        }

        var cached = await GetOrCreateCachedAsync(
                KimiAccountsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await dbContext.KimiAccounts
                        .Where(a => !a.IsDeleted)
                        .OrderBy(a => a.CreatedAt)
                        .ToListAsync(cancellationToken);
                }, cancellationToken)
            ?? [];

        return cached.Select(static a => a.Clone()).ToList();
    }

    /// <summary>
    /// 清除运行时设置缓存。
    /// </summary>
    public void InvalidateRuntimeSettings()
    {
        InvalidateCacheKey(RuntimeSettingsCacheKey);
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
        InvalidateCacheKeys(
            RouteTargetsCacheKeyPrefix + "OpenAI",
            RouteTargetsCacheKeyPrefix + "Anthropic",
            RouteTargetsCacheKeyPrefix + "all",
            ChatModelsCacheKey,
            ChatTargetsCacheKey,
            ModelConcurrencyLimitsCacheKey,
            EnabledSiteNamesCacheKey,
            DeveloperDebugModelsCacheKey,
            FallbackMappingsCacheKey,
            EnabledModelsCacheKey);
    }

    /// <summary>
    /// 清除后台路由配置页使用的管理缓存。
    /// </summary>
    public void InvalidateAdminRouteMetadata()
    {
        InvalidateCacheKeys(
            RouteEntriesCacheKey,
            RouteSiteInstancesCacheKey,
            RouteModelsCacheKey,
            RouteDiscoveredSitesCacheKey,
            RouteRulesByEntryCacheKey);
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

        var shouldInvalidateRuntimeRoutes = false;
        lock (_deferredRouteTargetsLock)
        {
            foreach (var item in _deferredRouteTargetsByModel.ToList())
            {
                // 找到包含该 slotId 的任何 target 并移除（兼容 siteId 传入 SiteKeyId 或 SiteId 的情况）
                foreach (var pendingTarget in item.Value.PendingActiveSlots.Keys.ToList())
                {
                    if (string.Equals(pendingTarget.SiteModelName, siteModelName, StringComparison.Ordinal)
                        && item.Value.PendingActiveSlots.TryGetValue(pendingTarget, out var pendingSlots)
                        && pendingSlots.Remove(activeSlotId))
                    {
                        if (pendingSlots.Count == 0)
                        {
                            item.Value.PendingActiveSlots.Remove(pendingTarget);
                        }
                    }
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
        InvalidateCacheKeys(
            ChatModelsCacheKey,
            ChatTargetsCacheKey,
            FallbackMappingsCacheKey,
            EnabledModelsCacheKey);
        // 路由入口列表与候选规则都回填了模型显示名称，模型变更（含显示名称编辑）时一并失效。
        InvalidateCacheKeys(RouteEntriesCacheKey, RouteRulesByEntryCacheKey);
    }

    /// <summary>
    /// 加载访问密钥缓存。
    /// </summary>
    private async Task<Dictionary<string, CachedProxyAccessKey>> GetAccessKeysAsync(CancellationToken cancellationToken)
    {
        // Core 宿主：从 Admin 下发的配置快照直接读取密钥数据，不依赖数据库
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    AccessKeyCacheKey,
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        if (snapshot?.AccessKeys is null)
                        {
                            return Task.FromResult(new Dictionary<string, CachedProxyAccessKey>(StringComparer.Ordinal));
                        }

                        var keys = snapshot.AccessKeys
                            .Where(x => x.IsEnabled)
                            .Select(x => new CachedProxyAccessKey
                            {
                                Id = x.Id,
                                AccessKeyHash = x.AccessKeyHash,
                                AllowedRouteNames = x.AllowedRouteNames
                            })
                            .ToList();

                        return Task.FromResult(keys.ToDictionary(x => x.AccessKeyHash, x => x, StringComparer.Ordinal));
                    })
                ?? [];
        }

        // Web/Admin 宿主：从数据库查询。
        return await GetOrCreateCachedAsync(
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
                }, cancellationToken)
            ?? [];
    }

    /// <summary>
    /// 加载路由目标缓存。
    /// </summary>
    private async Task<IReadOnlyList<CachedProxyRouteTarget>> GetRouteTargetsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主：从配置快照构建路由目标
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    RouteTargetsCacheKeyPrefix + "all",
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        if (snapshot?.RouteRules is null || snapshot.Sites is null)
                        {
                            return Task.FromResult<IReadOnlyList<CachedProxyRouteTarget>>([]);
                        }

                        // 构建站点查找字典，用于快速匹配路由规则关联的站点
                        var sitesById = snapshot.Sites
                            .Where(s => s.IsEnabled)
                            .ToDictionary(s => s.Id, s => s);

                        // 站点密钥（多 Key）按 SiteId 分组，用于把路由按 Key 展开为多条候选。
                        // 快照里只下发启用的 SiteKey；没有 SiteKey 的站点回退用 Site.ApiKey。
                        var siteKeysBySite = (snapshot.SiteKeys ?? [])
                            .GroupBy(k => k.SiteId)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        // master 同步：派生数据（映射/模型/档案/凭证），用于三层仿真、出口代理、
                        // Google 项目标识与托管源（ManagedSource）投影——缺失会导致 Core 上 401 即刷
                        // 永不触发、仿真头/出口代理/Gemini project 失效（快照 DTO 与 Builder 已携带）。
                        var snapshotMappings = snapshot.SiteModelMappings ?? [];
                        var snapshotModelsById = (snapshot.Models ?? [])
                            .GroupBy(m => m.Id)
                            .ToDictionary(g => g.Key, g => g.First());
                        var snapshotModelsByName = (snapshot.Models ?? [])
                            .GroupBy(m => m.ModelName, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                        var mappingsByRemote = snapshotMappings
                            .GroupBy(m => (m.SiteId, m.RemoteModelName))
                            .ToDictionary(g => g.Key, g => g.First());
                        var mappingsByModelId = snapshotMappings
                            .GroupBy(m => (m.SiteId, m.ModelLibraryItemId))
                            .ToDictionary(g => g.Key, g => g.First());
                        var headerProfileMap = (IReadOnlyDictionary<string, Dictionary<string, string>>?)(snapshot.HeaderProfiles ?? [])
                            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => TryParseExtraHeaders(g.First().HeadersJson), StringComparer.OrdinalIgnoreCase);
                        var proxyMap = (snapshot.ProxyProfiles ?? [])
                            .GroupBy(pp => pp.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First().ProxyUrl, StringComparer.OrdinalIgnoreCase);
                        var googleProjectsBySite = (snapshot.AccountCredentials ?? [])
                            .Where(a => string.Equals(a.Provider, "Google", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.ProjectId))
                            .GroupBy(a => a.LinkedSiteId)
                            .ToDictionary(g => g.Key, g => g.First().ProjectId!);
                        // 基础路由投影（每条 route × site 一条），不含 Key 维度。
                        var baseTargets = snapshot.RouteRules
                            .Where(r => r.IsEnabled)
                            .Join(sitesById.Values, r => r.SiteId, s => s.Id, (rule, site) => new { rule, site })
                            .ToList();

                        // 按 SiteKey 展开：同一路由的每个启用 Key 各产出一条候选，实现"主备 Key + 各自独立并发计数"。
                        // 站点没有启用的 SiteKey（Codex 托管站点 / 未迁移）回退用 site.ApiKey 产出单条候选。
                        var targets = new List<CachedProxyRouteTarget>(baseTargets.Count);
                        foreach (var item in baseTargets)
                        {
                            var rule = item.rule;
                            var site = item.site;
                            // 把 CoreRuntimeSiteKey 转成本方法用的 SiteKey 形态（仅取展开所需字段）。
                            var keysForSite = siteKeysBySite.TryGetValue(site.Id, out var skList) && skList.Count > 0
                                ? skList.Select(k => new SiteKey { Id = k.Id, SiteId = k.SiteId, KeyValue = k.KeyValue, Priority = k.Priority, CreatedAt = k.CreatedAt, IsEnabled = k.IsEnabled }).ToList()
                                : null;
                            var keysBySiteTyped = keysForSite is null
                                ? new Dictionary<Guid, List<SiteKey>>()
                                : new Dictionary<Guid, List<SiteKey>> { [site.Id] = keysForSite };
                            var candidates = ResolveSiteKeyCandidates(site.Id, site.ApiKey, keysBySiteTyped);

                            // master 同步：按 (SiteId, SiteModelName) / (SiteId, ModelId) 双路解析映射，
                            // 与 Admin/DB 路径同口径（自定义对外名经模型反查），再走三层仿真/出口代理解析。
                            CoreRuntimeSiteModelMapping? mapping = null;
                            if (mappingsByRemote.TryGetValue((site.Id, rule.SiteModelName), out var cm1))
                            {
                                mapping = cm1;
                            }
                            else
                            {
                                snapshotModelsByName.TryGetValue(rule.UpstreamModelName, out var routeModel);
                                if (routeModel is not null && mappingsByModelId.TryGetValue((site.Id, routeModel.Id), out var cm2))
                                {
                                    mapping = cm2;
                                }
                            }
                            snapshotModelsByName.TryGetValue(rule.UpstreamModelName, out var modelForEmulation);
                            var clientEmulation = ResolveClientEmulation(mapping?.ClientEmulation, modelForEmulation?.ClientEmulation, site.ClientEmulation);
                            var extraHeaders = BuildEffectiveExtraHeaders(clientEmulation, headerProfileMap, site.ExtraHeadersJson, modelForEmulation?.ExtraHeadersJson, mapping?.ExtraHeadersJson);
                            var egressProxyUrl = ResolveEgressProxyUrl(mapping?.EgressProxyUrl, site.EgressProxyUrl, proxyMap);

                            foreach (var candidate in candidates)
                            {
                                targets.Add(new CachedProxyRouteTarget
                                {
                                    RouteId = rule.Id,
                                    SiteId = site.Id,
                                    SiteKeyId = candidate.SiteKeyId,
                                    CircuitKey = BuildCircuitKey(rule.Id, candidate.SiteKeyId, rule.SiteModelName),
                                    SiteName = site.Name,
                                    ProtocolType = ProxyProtocolResolver.ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic),
                                    EndpointPathMode = site.EndpointPathMode,
                                    SupportsOpenAi = site.SupportsOpenAi,
                                    SupportsAnthropic = site.SupportsAnthropic,
                                    ExternalModelName = rule.ExternalModelName,
                                    UpstreamModelName = rule.UpstreamModelName,
                                    SiteModelName = rule.SiteModelName,
                                    BaseUrl = site.BaseUrl,
                                    ApiKey = candidate.ApiKey,
                                    // Codex 隐藏 Site 的自定义请求头（Originator/Chatgpt-Account-Id 等），
                                    // Core 转发时通过 MergeExtraHeaders 注入上游，缺失会导致 Codex 请求被拒绝。
                                    ManagedSource = site.ManagedSource ?? string.Empty,
                                SupportsResponses = ProxyProtocolResolver.SupportsResponses(
                                    site.SupportsOpenAi,
                                    site.SupportsAnthropic,
                                    site.SupportsResponses,
                                    site.ProtocolType),
                                // 三层合并后的最终请求头（模板档案最底层注入 + Site/Model/Mapping 覆盖），
                                // 兼容旧语义：站点 ExtraHeadersJson 仍是最小兜底。
                                ExtraHeaders = extraHeaders.Count > 0 ? extraHeaders : TryParseExtraHeaders(site.ExtraHeadersJson),
                                ClientEmulation = clientEmulation,
                                EgressProxyUrl = egressProxyUrl,
                                GoogleProjectId = googleProjectsBySite.TryGetValue(site.Id, out var googleProject) ? googleProject : string.Empty,
                                    ModelPriority = rule.ModelPriority,
                                    InstancePriority = rule.InstancePriority,
                                    Priority = rule.Priority,
                                    // 派生字段：Admin 在构建快照时已按 model 关联预解析，Core 直接透传。
                                    OverrideReasoningEffort = rule.OverrideReasoningEffort,
                                    CompatibilityRules = rule.CompatibilityRules,
                                    AvailabilityMode = NormalizeAvailabilityMode(rule.AvailabilityMode),
                                    TimeRangesJson = NormalizeTimeRangesJson(rule.AvailabilityMode, rule.TimeRangesJson)
                                });
                            }
                        }

                        return Task.FromResult<IReadOnlyList<CachedProxyRouteTarget>>(targets);
                    })
                ?? [];
        }

        // Web/Admin 宿主：从数据库查询。
        return await GetOrCreateCachedAsync(
                RouteTargetsCacheKeyPrefix + "all",
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var routes = await dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
                    var sites = await dbContext.Sites.ToListAsync(cancellationToken);
                    var models = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
                    var mappings = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var mappingsBySiteAndRemote = mappings.GroupBy(m => (m.SiteId, m.RemoteModelName)).ToDictionary(g => g.Key, g => g.First());
                    var mappingsBySiteAndModelId = mappings.GroupBy(m => (m.SiteId, m.ModelLibraryItemId)).ToDictionary(g => g.Key, g => g.First());

                    // 一次性加载所有启用的站点密钥，按 SiteId 分组，供路由目标按 Key 展开为多条候选。
                    var siteKeys = await dbContext.SiteKeys
                        .Where(k => k.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var siteKeysBySite = siteKeys
                        .GroupBy(k => k.SiteId)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    // Google 账号（Gemini 上游）的项目 ID 按 LinkedSiteId 映射，注入路由目标供请求体封套使用。
                    var googleProjectsBySite = (await dbContext.GoogleAccounts
                            .ToListAsync(cancellationToken))
                        .Where(a => !string.IsNullOrWhiteSpace(a.ProjectId))
                        .GroupBy(a => a.LinkedSiteId)
                        .ToDictionary(g => g.Key, g => g.First().ProjectId!);
                    // 一次性加载所有启用的兼容规则集，构建 Id→规则列表字典，供路由目标投影时查（避免 N+1）。
                    var profiles = await dbContext.CompatibilityProfiles
                        .Where(p => p.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var profileRules = profiles.ToDictionary(
                        p => p.Id,
                        p => ParseCompatibilityRules(p.RulesJson));

                    var proxyProfiles = await dbContext.ProxyProfiles
                        .Where(p => p.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var proxyMap = proxyProfiles
                        .ToDictionary(p => p.Key, p => p.ProxyUrl, StringComparer.OrdinalIgnoreCase);
                    var headerProfileMap = await LoadHeaderProfileMapAsync(scope.ServiceProvider, cancellationToken);

                    // 基础路由投影（每条 route × site × model 一条），不含 Key 维度。
                    var baseRoutes = (
                            from route in routes
                            join site in sites on route.SiteId equals site.Id
                            join model in models on route.UpstreamModelName equals model.ModelName into modelGroup
                            from model in modelGroup.DefaultIfEmpty()
                            where route.IsEnabled && site.IsEnabled
                            select new
                            {
                                route, site, model
                            })
                        .ToList();

                    // 按 SiteKey 展开：同一路由的每个启用 Key 各产出一条候选，实现"主备 Key + 各自独立并发计数"。
                    // 站点没有启用的 SiteKey（Codex 托管站点 / 未迁移）回退用 site.ApiKey 产出单条候选。
                    var expanded = new List<CachedProxyRouteTarget>(baseRoutes.Count);
                    foreach (var item in baseRoutes)
                    {
                        var route = item.route;
                        var site = item.site;
                        var model = item.model;
                        var candidates = ResolveSiteKeyCandidates(site.Id, site.ApiKey, siteKeysBySite);

                        SiteModelMapping? mapping = null;
                        if (mappingsBySiteAndRemote.TryGetValue((site.Id, route.SiteModelName), out var m1))
                        {
                            mapping = m1;
                        }
                        else if (model != null && mappingsBySiteAndModelId.TryGetValue((site.Id, model.Id), out var m2))
                        {
                            mapping = m2;
                        }

                        var clientEmulation = ResolveClientEmulation(mapping?.ClientEmulation, model?.ClientEmulation, site.ClientEmulation);
                        var extraHeaders = BuildEffectiveExtraHeaders(clientEmulation, headerProfileMap, site.ExtraHeadersJson, model?.ExtraHeadersJson, mapping?.ExtraHeadersJson);
                        var egressProxyUrl = ResolveEgressProxyUrl(mapping?.EgressProxyUrl, site.EgressProxyUrl, proxyMap);
                        // 思考等级优先级：站点映射 > 模型库 > 透传（均为空则透传客户端原始值）。
                        var overrideReasoningEffort = !string.IsNullOrWhiteSpace(mapping?.OverrideReasoningEffort)
                            ? mapping!.OverrideReasoningEffort!.Trim()
                            : (model?.OverrideReasoningEffort ?? string.Empty);

                        foreach (var candidate in candidates)
                        {
                            expanded.Add(new CachedProxyRouteTarget
                            {
                                RouteId = route.Id,
                                SiteId = site.Id,
                                SiteKeyId = candidate.SiteKeyId,
                                CircuitKey = BuildCircuitKey(site.Id, candidate.SiteKeyId, route.SiteModelName),
                                SiteName = site.Name,
                                ManagedSource = site.ManagedSource ?? string.Empty,
                                ProtocolType = ProxyProtocolResolver.ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic, site.SupportsResponses, site.ProtocolType),
                                EndpointPathMode = site.EndpointPathMode,
                                SupportsOpenAi = site.SupportsOpenAi,
                                SupportsAnthropic = site.SupportsAnthropic,
                                SupportsResponses = ProxyProtocolResolver.SupportsResponses(
                                    site.SupportsOpenAi,
                                    site.SupportsAnthropic,
                                    site.SupportsResponses,
                                    site.ProtocolType),
                                ExternalModelName = route.ExternalModelName,
                                UpstreamModelName = route.UpstreamModelName,
                                SiteModelName = route.SiteModelName,
                                BaseUrl = site.BaseUrl,
                                ApiKey = candidate.ApiKey,
                                ExtraHeaders = extraHeaders,
                                ClientEmulation = clientEmulation,
                                EgressProxyUrl = egressProxyUrl,
                                GoogleProjectId = googleProjectsBySite.TryGetValue(site.Id, out var googleProject) ? googleProject : string.Empty,
                                ModelPriority = route.ModelPriority,
                                InstancePriority = route.InstancePriority,
                                Priority = route.Priority,
                                OverrideReasoningEffort = overrideReasoningEffort,
                                CompatibilityRules = GetRulesForModel(model, profileRules),
                                AvailabilityMode = NormalizeAvailabilityMode(route.AvailabilityMode),
                                TimeRangesJson = NormalizeTimeRangesJson(route.AvailabilityMode, route.TimeRangesJson)
                            });
                        }
                    }

                    return expanded;
                }, cancellationToken)
            ?? [];
    }

    /// <summary>
    /// 加载启用的模型缓存。
    /// </summary>
    private async Task<Dictionary<Guid, CachedEnabledModel>> GetEnabledModelsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主：从配置快照读取模型列表
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    EnabledModelsCacheKey,
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        if (snapshot?.Models is null)
                        {
                            return Task.FromResult(new Dictionary<Guid, CachedEnabledModel>());
                        }

                        var models = snapshot.Models
                            .Where(x => x.IsEnabled)
                            .Select(x => new CachedEnabledModel
                            {
                                ModelId = x.Id,
                                ModelName = x.ModelName,
                                DisplayName = x.DisplayName
                            })
                            .ToDictionary(x => x.ModelId, x => x);

                        return Task.FromResult(models);
                    })
                ?? [];
        }

        // Web/Admin 宿主：从数据库查询。
        return await GetOrCreateCachedAsync(
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
                }, cancellationToken)
            ?? [];
    }

    /// <summary>
    /// 加载兜底映射缓存。
    /// </summary>
    private async Task<Dictionary<Guid, CachedFallbackTarget>> GetFallbackMappingsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主：从配置快照构建兜底映射
        if (_configProvider is not null)
        {
            return await _memoryCache.GetOrCreateAsync(
                    FallbackMappingsCacheKey,
                    entry =>
                    {
                        entry.Priority = CacheItemPriority.NeverRemove;
                        var snapshot = _configProvider.GetCurrent();
                        if (snapshot?.SiteModelMappings is null || snapshot.Sites is null || snapshot.Models is null)
                        {
                            return Task.FromResult(new Dictionary<Guid, CachedFallbackTarget>());
                        }

                        var sitesById = snapshot.Sites
                            .Where(s => s.IsEnabled)
                            .ToDictionary(s => s.Id);
                        var modelsById = snapshot.Models
                            .Where(m => m.IsEnabled)
                            .ToDictionary(m => m.Id);
                        // 站点密钥（多 Key）按 SiteId 分组，用于把兜底候选按 Key 展开。
                        var siteKeysBySite = (snapshot.SiteKeys ?? [])
                            .GroupBy(k => k.SiteId)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        // 三表内存联查：映射 + 站点 + 模型
                        var validMappings = snapshot.SiteModelMappings
                            .Where(m => m.IsEnabled
                                && sitesById.TryGetValue(m.SiteId, out var site)
                                && modelsById.TryGetValue(m.ModelLibraryItemId, out _))
                            .Select(m =>
                            {
                                var site = sitesById[m.SiteId];
                                var model = modelsById[m.ModelLibraryItemId];
                                // master 同步：带上映射/模型维度的仿真与出口代理字段（兜底目标同口径）。
                                return new
                                {
                                    m.ModelLibraryItemId,
                                    m.Id,
                                    model.ModelName,
                                    m.SiteId,
                                    site,
                                    m.RemoteModelName,
                                    m.ClientEmulation,
                                    m.ExtraHeadersJson,
                                    m.EgressProxyUrl,
                                    ModelClientEmulation = model.ClientEmulation,
                                    ModelExtraHeadersJson = model.ExtraHeadersJson
                                };
                            })
                            .ToList();

                        // 每个模型取优先级最高的一个站点（与原逻辑一致：按站点名排序取第一个），
                        // 然后把该站点的所有启用 Key 展开为多条兜底候选，使 fallback 也支持多 Key。
                        var fallbackTargets = validMappings
                            .GroupBy(x => x.ModelLibraryItemId)
                            .SelectMany(grouped =>
                            {
                                var first = grouped
                                    .OrderBy(x => x.site.Name, StringComparer.OrdinalIgnoreCase)
                                    .First();

                                var keysForSite = siteKeysBySite.TryGetValue(first.site.Id, out var skList) && skList.Count > 0
                                    ? skList.Select(k => new Domain.Sites.SiteKey { Id = k.Id, SiteId = k.SiteId, KeyValue = k.KeyValue, Priority = k.Priority, CreatedAt = k.CreatedAt, IsEnabled = k.IsEnabled }).ToList()
                                    : null;
                                var keysBySiteTyped = keysForSite is null
                                    ? new Dictionary<Guid, List<Domain.Sites.SiteKey>>()
                                    : new Dictionary<Guid, List<Domain.Sites.SiteKey>> { [first.site.Id] = keysForSite };
                                var candidates = ResolveSiteKeyCandidates(first.site.Id, first.site.ApiKey, keysBySiteTyped);

                                // master 同步：三层仿真/出口代理解析（映射 > 模型 > 站点），与主路由同口径。
                                var fallbackEmulation = ResolveClientEmulation(first.ClientEmulation, first.ModelClientEmulation, first.site.ClientEmulation);
                                var fallbackHeaderProfileMap = (IReadOnlyDictionary<string, Dictionary<string, string>>?)(snapshot.HeaderProfiles ?? [])
                                    .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(g => g.Key, g => TryParseExtraHeaders(g.First().HeadersJson), StringComparer.OrdinalIgnoreCase);
                                var fallbackProxyMap = (snapshot.ProxyProfiles ?? [])
                                    .GroupBy(pp => pp.Key, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(g => g.Key, g => g.First().ProxyUrl, StringComparer.OrdinalIgnoreCase);
                                var fallbackGoogleProjects = (snapshot.AccountCredentials ?? [])
                                    .Where(a => string.Equals(a.Provider, "Google", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.ProjectId))
                                    .GroupBy(a => a.LinkedSiteId)
                                    .ToDictionary(g => g.Key, g => g.First().ProjectId!);
                                var fallbackHeaders = BuildEffectiveExtraHeaders(fallbackEmulation, fallbackHeaderProfileMap, first.site.ExtraHeadersJson, first.ModelExtraHeadersJson, first.ExtraHeadersJson);
                                var fallbackEgressProxy = ResolveEgressProxyUrl(first.EgressProxyUrl, first.site.EgressProxyUrl, fallbackProxyMap);
                                return candidates.Select(candidate => new CachedFallbackTarget
                                {
                                    ModelId = grouped.Key,
                                    ModelName = first.ModelName,
                                    SiteId = first.SiteId,
                                    SiteKeyId = candidate.SiteKeyId,
                                    CircuitKey = BuildCircuitKey(first.site.Id, candidate.SiteKeyId, first.RemoteModelName),
                                    SiteName = first.site.Name,
                                    ProtocolType = ProxyProtocolResolver.ResolveSiteProtocolType(first.site.SupportsOpenAi, first.site.SupportsAnthropic),
                                    BaseUrl = first.site.BaseUrl,
                                    EndpointPathMode = first.site.EndpointPathMode,
                                    ApiKey = candidate.ApiKey,
                                    ManagedSource = first.site.ManagedSource ?? string.Empty,
                                    ExtraHeaders = fallbackHeaders.Count > 0 ? fallbackHeaders : TryParseExtraHeaders(first.site.ExtraHeadersJson),
                                    ClientEmulation = fallbackEmulation,
                                    EgressProxyUrl = fallbackEgressProxy,
                                    GoogleProjectId = fallbackGoogleProjects.TryGetValue(first.site.Id, out var fallbackGoogleProject) ? fallbackGoogleProject : string.Empty,
                                    SiteModelName = first.RemoteModelName
                                });
                            })
                            .ToList();

                        // 兜底字典保留每个模型的主 Key 候选（Priority 最小的那个）。
                        // fallback 语义是"每个模型一个单目标兜底"，多 Key 的主备轮换由主路由 GetRouteTargetsAsync 处理。
                        return Task.FromResult(fallbackTargets
                            .GroupBy(x => x.ModelId)
                            .ToDictionary(g => g.Key, g => g.First()));
                    })
                ?? [];
        }

        // Web/Admin 宿主：从数据库查询。
        return await GetOrCreateCachedAsync(
                FallbackMappingsCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var mappingsData = await dbContext.SiteModelMappings.ToListAsync(cancellationToken);
                    var sitesData = await dbContext.Sites.ToListAsync(cancellationToken);
                    var modelsData = await dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
                    var siteKeysData = await dbContext.SiteKeys
                        .Where(k => k.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var siteKeysBySite = siteKeysData
                        .GroupBy(k => k.SiteId)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    // Google 账号（Gemini 上游）的项目 ID 按 LinkedSiteId 映射，注入兜底目标供请求体封套使用。
                    var googleProjectsBySite = (await dbContext.GoogleAccounts
                            .ToListAsync(cancellationToken))
                        .Where(a => !string.IsNullOrWhiteSpace(a.ProjectId))
                        .GroupBy(a => a.LinkedSiteId)
                        .ToDictionary(g => g.Key, g => g.First().ProjectId!);

                    var proxyProfiles = await dbContext.ProxyProfiles
                        .Where(p => p.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var proxyMap = proxyProfiles
                        .ToDictionary(p => p.Key, p => p.ProxyUrl, StringComparer.OrdinalIgnoreCase);
                    var fallbackHeaderProfileMap = await LoadHeaderProfileMapAsync(scope.ServiceProvider, cancellationToken);

                    var rawMappings = (
                            from mapping in mappingsData
                            join site in sitesData on mapping.SiteId equals site.Id
                            join model in modelsData on mapping.ModelLibraryItemId equals model.Id
                            where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                            select new
                            {
                                ModelId = model.Id,
                                model.ModelName,
                                ModelClientEmulation = model.ClientEmulation,
                                ModelExtraHeadersJson = model.ExtraHeadersJson,
                                SiteId = site.Id,
                                SiteName = site.Name,
                                site.ManagedSource,
                                site.SupportsOpenAi,
                                site.SupportsAnthropic,
                                site.SupportsResponses,
                                site.ProtocolType,
                                site.BaseUrl,
                                site.EndpointPathMode,
                                site.ApiKey,
                                MappingId = mapping.Id,
                                SiteModelName = mapping.RemoteModelName,
                                SiteExtraHeadersJson = site.ExtraHeadersJson,
                                SiteClientEmulation = site.ClientEmulation,
                                SiteEgressProxyUrl = site.EgressProxyUrl,
                                MappingClientEmulation = mapping.ClientEmulation,
                                MappingExtraHeadersJson = mapping.ExtraHeadersJson,
                                MappingEgressProxyUrl = mapping.EgressProxyUrl
                            })
                        .ToList();

                    // 每个模型取优先级最高的一个站点（与原逻辑一致：按站点名排序取第一个），
                    // 然后把该站点的所有启用 Key 展开为多条兜底候选，使 fallback 也支持多 Key。
                    var mappings = rawMappings
                        .GroupBy(x => x.ModelId)
                        .SelectMany(grouped =>
                        {
                            var first = grouped
                                .OrderBy(x => x.SiteName, StringComparer.OrdinalIgnoreCase)
                                .First();

                            var candidates = ResolveSiteKeyCandidates(first.SiteId, first.ApiKey, siteKeysBySite);
                            var fallbackEmulation = ResolveClientEmulation(first.MappingClientEmulation, first.ModelClientEmulation, first.SiteClientEmulation);
                            return candidates.Select(candidate => new CachedFallbackTarget
                            {
                                ModelId = grouped.Key,
                                ModelName = first.ModelName,
                                SiteId = first.SiteId,
                                SiteKeyId = candidate.SiteKeyId,
                                CircuitKey = BuildCircuitKey(first.SiteId, candidate.SiteKeyId, first.SiteModelName),
                                SiteName = first.SiteName,
                                ManagedSource = first.ManagedSource ?? string.Empty,
                                ProtocolType = ProxyProtocolResolver.ResolveSiteProtocolType(first.SupportsOpenAi, first.SupportsAnthropic, first.SupportsResponses, first.ProtocolType),
                                BaseUrl = first.BaseUrl,
                                EndpointPathMode = first.EndpointPathMode,
                                ApiKey = candidate.ApiKey,
                                SiteModelName = first.SiteModelName,
                                ExtraHeaders = BuildEffectiveExtraHeaders(fallbackEmulation, fallbackHeaderProfileMap, first.SiteExtraHeadersJson, first.ModelExtraHeadersJson, first.MappingExtraHeadersJson),
                                ClientEmulation = fallbackEmulation,
                                EgressProxyUrl = ResolveEgressProxyUrl(first.MappingEgressProxyUrl, first.SiteEgressProxyUrl, proxyMap),
                                GoogleProjectId = googleProjectsBySite.TryGetValue(first.SiteId, out var fallbackGoogleProject) ? fallbackGoogleProject : string.Empty
                            });
                        })
                        .ToList();

                    // 兜底字典保留每个模型的主 Key 候选（Priority 最小的那个）。
                    // fallback 语义是"每个模型一个单目标兜底"，多 Key 的主备轮换由主路由 GetRouteTargetsAsync 处理。
                    return mappings
                        .GroupBy(x => x.ModelId)
                        .ToDictionary(g => g.Key, g => g.First());
                }, cancellationToken)
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
    /// 解析规则集的 RulesJson 为规则列表。解析失败返回空列表，不影响转发。
    /// </summary>
    private static IReadOnlyList<CompatibilityRule> ParseCompatibilityRules(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson)) return Array.Empty<CompatibilityRule>();
        try
        {
            var rules = JsonSerializer.Deserialize<List<CompatibilityRule>>(rulesJson);
            return rules is null || rules.Count == 0 ? Array.Empty<CompatibilityRule>() : rules;
        }
        catch
        {
            return Array.Empty<CompatibilityRule>();
        }
    }

    /// <summary>
    /// 取模型关联的兼容规则集（按 CompatibilityProfileId 查字典）。模型或 profileId 为空、或字典里没有则返回空。
    /// </summary>
    private static IReadOnlyList<CompatibilityRule> GetRulesForModel(ModelLibraryItem? model, Dictionary<Guid, IReadOnlyList<CompatibilityRule>> profileRules)
    {
        var profileId = model?.CompatibilityProfileId;
        if (profileId is null || profileId == Guid.Empty) return Array.Empty<CompatibilityRule>();
        return profileRules.TryGetValue(profileId.Value, out var rules) ? rules : Array.Empty<CompatibilityRule>();
    }

    /// <summary>
    /// 加载启用的请求头模板方案（Key → 解析后的请求头字典，占位符原样保留、请求时由引擎求值）。
    /// 从 IHeaderProfileCatalogService 读取本地 client-header-profiles.json。
    /// 仅在缓存构建期调用；模板增删改由 HeaderProfilesApiController 失效路由缓存触发重建。
    /// </summary>
    private static async Task<Dictionary<string, Dictionary<string, string>>> LoadHeaderProfileMapAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var catalogService = serviceProvider.GetService<IHeaderProfileCatalogService>();
        if (catalogService == null)
        {
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        var profiles = await catalogService.GetAllAsync(cancellationToken);
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (!profile.IsEnabled || string.IsNullOrWhiteSpace(profile.Key) || string.IsNullOrWhiteSpace(profile.HeadersJson))
            {
                continue;
            }

            var headers = TryParseExtraHeaders(profile.HeadersJson);
            if (headers.Count > 0)
            {
                map.TryAdd(profile.Key.Trim(), headers);
            }
        }

        return map;
    }

    /// <summary>
    /// 反序列化 Site.ExtraHeadersJson 为大小写不敏感的请求头字典。
    /// 空或非法 JSON 返回空字典（容错：坏数据不阻断转发，仅该 Site 不带额外头）。
    /// 仅在缓存构建期调用（5s 一次），不在每请求路径。
    /// </summary>
    private static Dictionary<string, string> TryParseExtraHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerPresets.CaseInsensitive);
            return dict != null
                ? new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 解析客户端特征模拟预设类型（优先级：SiteModelMapping > ModelLibraryItem > Site）。
    /// 内置预设返回归一化名称；非预设的自定义值按"请求头模板方案 Key"原样透传（运行时经 HeaderProfiles 解析）。
    /// </summary>
    internal static string ResolveClientEmulation(string? mappingEmulation, string? modelEmulation, string? siteEmulation)
    {
        foreach (var candidate in new[] { mappingEmulation, modelEmulation, siteEmulation })
        {
            var trimmed = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var normalized = ClientEmulationConstants.Normalize(trimmed);
            if (string.Equals(normalized, ClientEmulationConstants.None, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return normalized;
        }

        return ClientEmulationConstants.None;
    }

    /// <summary>
    /// 构建最终转发的自定义请求头：HeaderProfile 模板（最底层，可覆盖引擎内置预设硬编码）
    /// → Site → Model → SiteModelMapping（显式配置逐层覆盖）。占位符由引擎在请求时求值。
    /// </summary>
    internal static Dictionary<string, string> BuildEffectiveExtraHeaders(
        string clientEmulation,
        IReadOnlyDictionary<string, Dictionary<string, string>>? headerProfileMap,
        string? siteJson,
        string? modelJson,
        string? mappingJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headerProfileMap != null
            && !string.IsNullOrWhiteSpace(clientEmulation)
            && headerProfileMap.TryGetValue(clientEmulation.Trim(), out var profileHeaders))
        {
            foreach (var (key, value) in profileHeaders)
            {
                result[key] = value;
            }
        }

        foreach (var json in new[] { siteJson, modelJson, mappingJson })
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            foreach (var (key, value) in TryParseExtraHeaders(json))
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析站点专属出口网络代理（优先级：SiteModelMapping > Site > Direct 直连）。
    /// </summary>
    internal static string? ResolveEgressProxyUrl(string? mappingProxy, string? siteProxy, IReadOnlyDictionary<string, string>? proxyMap = null)
    {
        var raw = !string.IsNullOrWhiteSpace(mappingProxy)
            ? mappingProxy.Trim()
            : (!string.IsNullOrWhiteSpace(siteProxy) ? siteProxy.Trim() : null);

        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "direct", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (proxyMap != null && proxyMap.TryGetValue(raw, out var mappedUrl))
        {
            return mappedUrl;
        }

        return raw;
    }

    /// <summary>
    /// 合并多个层级的自定义请求头 JSON（后面的覆盖前面的：Site -> Model -> SiteModelMapping）。
    /// </summary>
    internal static Dictionary<string, string> MergeExtraHeaders(params string?[] extraHeadersJsons)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in extraHeadersJsons)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            var parsed = TryParseExtraHeaders(json);
            foreach (var kvp in parsed)
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// 为多 Key 展开准备的身份候选：一个站点可能产出多个候选，每个候选携带实际使用的密钥值和对应的 SiteKeyId。
    /// 站点没有启用的 SiteKey 时回退到站点默认密钥（兼容 Codex 托管站点和未迁移的老站点）。
    /// </summary>
    internal sealed record SiteKeyCandidate(Guid? SiteKeyId, string ApiKey);

    /// <summary>
    /// 取指定站点的密钥候选列表（按 Priority 升序，仅启用项）。
    /// <para>
    /// 优先返回该站点的启用 SiteKey；若站点没有任何启用的 SiteKey（Codex 托管站点或尚未迁移），
    /// 则回退用 <paramref name="fallbackApiKey"/> 产出单条候选，保证不回归。
    /// </para>
    /// </summary>
    internal static List<SiteKeyCandidate> ResolveSiteKeyCandidates(
        Guid siteId,
        string fallbackApiKey,
        Dictionary<Guid, List<SiteKey>> siteKeysBySite)
    {
        if (siteKeysBySite.TryGetValue(siteId, out var keys) && keys.Count > 0)
        {
            return keys
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Select(x => new SiteKeyCandidate(x.Id, x.KeyValue))
                .ToList();
        }

        // 回退：站点没有 SiteKey，用 Site.ApiKey 产出单条候选（null SiteKeyId 标记为兼容候选）
        return [new SiteKeyCandidate(null, fallbackApiKey)];
    }

    /// <summary>
    /// 合成熔断身份键：按 (SiteId, SiteKeyId, SiteModelName) 维度确定性派生。
    /// 熔断状态是"该站点该模型"的全局共享状态，不区分路由规则——路由规则的增删/排序
    /// （保存时规则 Id 会重建）不影响熔断键，同一站点同一模型出现在多个路由时共享熔断。
    /// SiteKeyId 维度保留：同一站点不同 Key（账号/凭证）各自熔断，不互相误伤。
    /// </summary>
    internal static Guid BuildCircuitKey(Guid siteId, Guid? siteKeyId, string siteModelName)
    {
        // 确定性派生：SiteId + SiteKeyId + 模型名的字节拼接后做 SHA256，取前 16 字节为 Guid。
        // 合成键稳定且与真实 Guid 空间冲突概率可忽略（不同组合必然不同键）。
        var modelNameBytes = System.Text.Encoding.UTF8.GetBytes(siteModelName ?? string.Empty);
        Span<byte> buffer = stackalloc byte[32 + modelNameBytes.Length];
        siteId.TryWriteBytes(buffer[..16]);
        if (siteKeyId is not null)
        {
            siteKeyId.Value.TryWriteBytes(buffer[16..32]);
        }
        else
        {
            buffer[16..32].Clear();
        }
        modelNameBytes.CopyTo(buffer[32..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return new Guid(hash[..16]);
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
    /// 流式转发空闲超时（秒）；0 表示不启用。
    /// </summary>
    public int ProxyStreamIdleTimeoutSeconds { get; set; }
    /// <summary>
    /// 代理重试次数。
    /// </summary>
    public int ProxyRetryCount { get; set; } = 1;
    /// <summary>
    /// 上游 429（速率限制）时的连续重试次数，默认 0（一次 429 即失败）。
    /// </summary>
    public int RateLimitRetryCount { get; set; }
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
    /// 调用追踪开关（热路径读取：关闭时代理请求跳过追踪采集）。
    /// </summary>
    public bool DeveloperTraceEnabled { get; set; } = true;
    /// <summary>
    /// 诊断抓包开关（失败请求自动落盘与采样的读取开关）。
    /// </summary>
    public bool DeveloperFailureDumpEnabled { get; set; } = true;
    /// <summary>
    /// 模拟器页开关。
    /// </summary>
    public bool DeveloperSimulatorEnabled { get; set; } = true;
    /// <summary>
    /// 协议诊断与 AI 自愈页开关。
    /// </summary>
    public bool DeveloperProtocolDiagnosticsEnabled { get; set; } = true;
    /// <summary>
    /// SQL 迁移页开关。
    /// </summary>
    public bool DeveloperSqlMigrationsEnabled { get; set; } = true;
    /// <summary>
    /// 并发打满时的处理策略：0 = 跳到下一顺位，1 = 排队等待。
    /// </summary>
    public int ConcurrencyMode { get; set; }
    /// <summary>
    /// 并发排队等待的最大时间（秒）。
    /// </summary>
    public int ConcurrencyQueueTimeoutSeconds { get; set; } = 120;
    /// <summary>
    /// OAuth 账号功能总开关。
    /// </summary>
    public bool OAuthFeaturesEnabled { get; set; }
    /// <summary>
    /// OAuth 账号巡检自动执行开关。
    /// </summary>
    public bool OAuthInspectionEnabled { get; set; }
    /// <summary>
    /// OAuth 账号巡检周期（秒）。
    /// </summary>
    public int OAuthInspectionIntervalSeconds { get; set; } = 1800;
    /// <summary>
    /// OAuth 账号额度缓存最大小时数。
    /// </summary>
    public int OAuthQuotaMaxCacheHours { get; set; } = 6;
    /// <summary>
    /// OAuth 账号自动禁用阈值（百分比，1-100）。
    /// </summary>
    public int OAuthAutoDisableThresholdPercent { get; set; } = 95;
    /// <summary>
    /// OAuth 账号巡检缓存复用开关。关闭时每轮巡检都真实刷新额度；开启时未被使用的账号沿用缓存快照。
    /// </summary>
    public bool OAuthInspectionCacheEnabled { get; set; }
    /// <summary>
    /// 是否启用对话记录功能（split 分支：控制对话记录页面显示以及写入）。
    /// </summary>
    public bool ConversationLogEnabled { get; set; } = true;
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
    /// 该候选实际使用的站点密钥标识。null 表示该站点没有 SiteKey 记录（Codex 托管站点或未迁移），
    /// 此时使用 <see cref="ApiKey"/>（回退自 Site.ApiKey）。多 Key 展开后同一路由会有多个候选，
    /// 各自携带不同的 SiteKeyId。
    /// </summary>
    public Guid? SiteKeyId { get; set; }
    /// <summary>
    /// 熔断/并发身份键。多 Key 展开的候选用合成(RouteId, SiteKeyId)，单 Key/兼容候选用 RouteId 本身。
    /// 转发循环用此键读写熔断状态，避免同一路由的不同 Key 互相误熔断。
    /// </summary>
    public Guid CircuitKey { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 站点托管来源，用于识别需要特殊凭证续期的 Codex 隐藏站点。
    /// </summary>
    public string ManagedSource { get; set; } = string.Empty;
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
    /// 是否支持 OpenAI Responses 原生接口。
    /// </summary>
    public bool SupportsResponses { get; set; }
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
    /// 从 Site.ExtraHeadersJson 反序列化的自定义转发请求头（大小写不敏感）。
    /// 空字典表示无额外头。Codex 隐藏 Site 用它携带 Originator / Chatgpt-Account-Id / User-Agent。
    /// </summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 客户端特征模拟预设类型（None | OpenCode | ClaudeCode | CodexCli | Antigravity | Custom）。
    /// </summary>
    public string ClientEmulation { get; set; } = "None";
    /// <summary>
    /// 站点专用出口网络代理地址。
    /// </summary>
    public string? EgressProxyUrl { get; set; }
    /// <summary>
    /// Google 账号（Antigravity 隐藏 Site）的项目 ID，作为 Gemini 上游请求体 project 字段。
    /// 空表示非 Google 托管站点。
    /// </summary>
    public string GoogleProjectId { get; set; } = string.Empty;
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
    /// 强制覆盖的思考等级。空=不干预，非空=强制覆盖转发给上游的思考等级。
    /// </summary>
    public string OverrideReasoningEffort { get; set; } = string.Empty;
    /// <summary>
    /// 该模型关联的兼容规则集（已解析的规则列表）。转发时按 isPassthrough 筛选 scope 后应用。
    /// 为空表示不应用任何规则。
    /// </summary>
    public IReadOnlyList<CompatibilityRule> CompatibilityRules { get; set; } = Array.Empty<CompatibilityRule>();
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
        return ProxyProtocolResolver.SupportsProtocol(
            protocolType,
            SupportsOpenAi,
            SupportsAnthropic,
            SupportsResponses,
            ProtocolType);
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
        return ProxyProtocolResolver.ResolveProtocolForClient(
            clientProtocol,
            ProtocolType,
            SupportsOpenAi,
            SupportsAnthropic,
            SupportsResponses,
            ProtocolType);
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
    /// 该候选实际使用的站点密钥标识。null 表示回退到 <see cref="ApiKey"/>（Site.ApiKey）。
    /// </summary>
    public Guid? SiteKeyId { get; set; }
    /// <summary>
    /// 熔断/并发身份键，多 Key 展开后用于区分同一映射的不同 Key。
    /// </summary>
    public Guid CircuitKey { get; set; }
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
    /// <summary>
    /// 从 Site.ExtraHeadersJson 反序列化的自定义转发请求头。
    /// </summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 客户端特征模拟预设类型。
    /// </summary>
    public string ClientEmulation { get; set; } = "None";
    /// <summary>
    /// 站点专用出口网络代理地址。
    /// </summary>
    public string? EgressProxyUrl { get; set; }
    /// <summary>
    /// Google 账号（Gemini 上游隐藏 Site）的项目 ID，空表示非 Google 托管站点。
    /// </summary>
    public string GoogleProjectId { get; set; } = string.Empty;
    /// <summary>
    /// 托管提供商标识（Codex | Google | kimi_oauth；自建为空）。聊天转发 401 即刷按此分流。
    /// </summary>
    public string ManagedSource { get; set; } = string.Empty;
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
    /// 该候选实际使用的站点密钥标识。null 表示回退到 <see cref="ApiKey"/>（Site.ApiKey）。
    /// </summary>
    public Guid? SiteKeyId { get; set; }
    /// <summary>
    /// 熔断/并发身份键，多 Key 展开后用于区分同一映射的不同 Key。
    /// </summary>
    public Guid CircuitKey { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 站点托管来源，用于识别需要特殊凭证维护的 Google 隐藏站点。
    /// </summary>
    public string ManagedSource { get; set; } = string.Empty;
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
    /// <summary>
    /// 从 Site.ExtraHeadersJson 反序列化的自定义转发请求头。
    /// </summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 客户端特征模拟预设类型。
    /// </summary>
    public string ClientEmulation { get; set; } = "None";
    /// <summary>
    /// 站点专用出口网络代理地址。
    /// </summary>
    public string? EgressProxyUrl { get; set; }
    /// <summary>
    /// Google 账号（Gemini 上游隐藏 Site）的项目 ID，空表示非 Google 托管站点。
    /// </summary>
    public string GoogleProjectId { get; set; } = string.Empty;
}

