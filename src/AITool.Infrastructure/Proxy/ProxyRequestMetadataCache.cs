using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.CoreRuntime;
using AITool.Domain.Codex;
using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 代理请求元数据缓存。
/// 统一由 Infrastructure 层提供，Web/Admin 宿主通过数据库查询获取缓存数据，
/// Core 宿主通过可选的 <see cref="ICoreRuntimeConfigProvider"/> 从 Admin 下发的配置快照读取。
/// </summary>
public sealed partial class ProxyRequestMetadataCache
{
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
    /// 在 Web/Admin 宿主中用于创建数据库查询作用域；
    /// 在纯 Core 宿主中不会被使用，因为数据直接从配置快照读取。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>
    /// 数据库连接字符串（仅 Web/Admin 宿主）。
    /// 缓存未命中时用 CopyNew() 创建独立连接查库，避免与 SqlSugarScope 单例连接并发竞态。
    /// </summary>
    private readonly string? _connectionString;
    /// <summary>
    /// Core 运行时配置提供者。
    /// 仅在 Core 宿主中注册，用于从 Admin 下发的配置快照读取代理运行时数据。
    /// 在 Web/Admin 宿主中为 null，此时回退到数据库查询。
    /// </summary>
    private readonly ICoreRuntimeConfigProvider? _configProvider;
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
    /// Web/Admin 宿主省略 configProvider 参数，通过数据库查询获取缓存数据；
    /// Core 宿主传入 configProvider，从 Admin 下发的配置快照直接读取运行时数据。
    /// </summary>
    public ProxyRequestMetadataCache(
        IMemoryCache memoryCache,
        IServiceScopeFactory scopeFactory,
        ICoreRuntimeConfigProvider? configProvider = null,
        string? connectionString = null)
    {
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
        _configProvider = configProvider;
        _connectionString = connectionString;
    }

    /// <summary>
    /// 创建一个独立的 SqlSugarClient（有自己的连接），用完即释放。
    /// 所有缓存未命中时的查库都走这个方法，避免与单例 SqlSugarScope 并发竞态。
    /// <para>性能说明：缓存为 NeverRemove，仅在首次加载或显式失效时触发查库，
    /// CopyNew 开销（创建连接 + 查询 + 关闭连接）约 1-3ms，可忽略。</para>
    /// </summary>
    private SqlSugar.ISqlSugarClient CreateIndependentClient()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // CopyNew 创建独立连接实例，不共享单例 SqlSugarScope 的连接。
        // 连接级 PRAGMA（busy_timeout/cache_size）需要手动设置，CopyNew 不会继承单例连接的 PRAGMA。
        var client = dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        client.Ado.ExecuteCommand("PRAGMA cache_size=-65536;");
        return client;
    }

    /// <summary>
    /// Core 运行时元数据入口。
    /// 这里聚合的缓存会直接影响访问密钥校验、运行时设置、路由目标选择和兜底行为，当前必须继续保持在代理主链路可直接访问的位置。
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
            // JSON 解析失败时 fail-close（拒绝所有路由），而非 fail-open（允许全部）。
            // 返回空集合表示"只允许列表中的路由"但列表为空 = 全部拒绝。
            return new HashSet<string>(StringComparer.Ordinal);
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
    /// Core 宿主中从配置快照的 RuntimeSettings 读取，Web/Admin 宿主中从数据库查询。
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
                                CircuitBreakerFailureThreshold = s.CircuitBreakerFailureThreshold,
                                CircuitBreakerRecoveryMinutes = s.CircuitBreakerRecoveryMinutes,
                                ConversationLogEnabled = s.ConversationLogEnabled,
                                ConcurrencyMode = s.ConcurrencyMode,
                                ConcurrencyQueueTimeoutSeconds = s.ConcurrencyQueueTimeoutSeconds,
                                DeveloperFeaturesEnabled = s.DeveloperFeaturesEnabled
                            });
                    })
                ?? new CachedProxyRuntimeSettings();
        }

        // Web/Admin 宿主：从数据库查询完整的运行时设置（包含检测相关字段）。
        // 缓存未命中时用独立连接（CopyNew）查库，不碰单例 SqlSugarScope 连接，避免并发竞态。
        return await _memoryCache.GetOrCreateAsync(
                RuntimeSettingsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

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
                            ConcurrencyQueueTimeoutSeconds = settings.ConcurrencyQueueTimeoutSeconds,
                            CodexFeaturesEnabled = settings.CodexFeaturesEnabled,
                            CodexInspectionEnabled = settings.CodexInspectionEnabled,
                            CodexInspectionIntervalMinutes = settings.CodexInspectionIntervalMinutes,
                            CodexQuotaMaxCacheHours = settings.CodexQuotaMaxCacheHours,
                            CodexAutoDisableThresholdPercent = settings.CodexAutoDisableThresholdPercent
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

    // Admin 查询职责已拆分到 ProxyRequestMetadataCache.AdminQueries.cs，
    // 这里保留运行时路径、共享失效入口和少量共享辅助逻辑，便于后续继续向 Core / Admin 双宿主分层收口。

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
    /// 共享失效入口。数据变更时清除缓存，下次查询自动从 DB/快照重建并永久缓存。
    /// </summary>
    public void InvalidateAccessKeys()
    {
        _memoryCache.Remove(AccessKeyCacheKey);
        InvalidateAdminDeveloperMetadata();
    }

    /// <summary>
    /// 清除 Codex 账号列表缓存。账号发生增删改（额度更新/启停/token刷新/冷却/管理后台操作）后调用。
    /// </summary>
    public void InvalidateCodexAccounts()
    {
        _memoryCache.Remove(CodexAccountsCacheKey);
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
    /// 注意：仅 Admin 宿主可用（需 AppDbContext）。Core 宿主返回空列表——Core 不持有 Codex 账号实体。
    /// </summary>
    public async Task<List<CodexAccount>> GetCodexAccountsAsync(CancellationToken cancellationToken)
    {
        // Core 宿主（_configProvider 非 null）没有 AppDbContext，直接返回空列表避免解析异常。
        // Core 不缓存 Codex 账号实体，巡检逻辑只在 Admin 运行。
        if (_configProvider is not null)
        {
            return [];
        }

        return await _memoryCache.GetOrCreateAsync(
                CodexAccountsCacheKey,
                async entry =>
                {
                    // 与其他路由/模型缓存一致采用 NeverRemove：账号变更时通过 InvalidateCodexAccounts 显式失效。
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var independentClient = CreateIndependentClient();
                    return await independentClient.Queryable<Domain.Codex.CodexAccount>()
                        .Where(a => !a.DisabledByFeatureToggle)
                        .OrderBy(a => a.LastQuotaCheckedAt)
                        .ToListAsync(cancellationToken);
                })
            ?? [];
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
    /// 这里同时命中运行时路由快照和后台页面查询缓存，是当前双宿主拆分里最典型的共享边界入口。
    /// </summary>
    public void InvalidateRouteTargets()
    {
        InvalidateRuntimeRouteTargets();
        InvalidateAdminRouteMetadata();
        InvalidateAdminChatMetadata();
        InvalidateAdminDeveloperMetadata();
    }

    /// <summary>
    /// 清除运行时代理使用的路由目标缓存。
    /// 这里只保留会直接影响代理请求选路、并发限制和兜底行为的缓存。
    /// </summary>
    public void InvalidateRuntimeRouteTargets()
    {
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "OpenAI");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "Anthropic");
        _memoryCache.Remove(RouteTargetsCacheKeyPrefix + "all");
        _memoryCache.Remove(ModelConcurrencyLimitsCacheKey);
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
    /// 清除后台聊天页依赖的管理缓存。
    /// </summary>
    public void InvalidateAdminChatMetadata()
    {
        _memoryCache.Remove(ChatModelsCacheKey);
        _memoryCache.Remove(ChatTargetsCacheKey);
    }

    /// <summary>
    /// 清除开发者页和站点名称等辅助查询缓存。
    /// 这部分目前仍与运行时服务同类暴露，但职责已经偏向 Admin 查询层。
    /// </summary>
    public void InvalidateAdminDeveloperMetadata()
    {
        _memoryCache.Remove(EnabledSiteNamesCacheKey);
        _memoryCache.Remove(DeveloperDefaultAccessKeyCacheKey);
        _memoryCache.Remove(DeveloperDebugModelsCacheKey);
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
    /// 清除模型相关缓存。
    /// 这里同时命中后台聊天页查询结果和运行时模型选择结果，后续可继续拆成更细的 Admin / Core 失效入口。
    /// </summary>
    public void InvalidateModelMetadata()
    {
        InvalidateAdminChatMetadata();
        _memoryCache.Remove(FallbackMappingsCacheKey);
        _memoryCache.Remove(EnabledModelsCacheKey);
    }

    /// <summary>
    /// 加载访问密钥缓存。
    /// Core 宿主中直接从配置快照读取，Web/Admin 宿主中从数据库查询。
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

        // Web/Admin 宿主：从数据库查询
        return await _memoryCache.GetOrCreateAsync(
                AccessKeyCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var independentClient = CreateIndependentClient();
                    var accessKeys = await independentClient.Queryable<Domain.Proxy.ProxyAccessKey>()
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
    /// Core 宿主中从配置快照的 RouteRules 和 Sites 构建路由目标，Web/Admin 宿主中从数据库联表查询。
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

                            foreach (var candidate in candidates)
                            {
                                targets.Add(new CachedProxyRouteTarget
                                {
                                    RouteId = rule.Id,
                                    SiteId = site.Id,
                                    SiteKeyId = candidate.SiteKeyId,
                                    CircuitKey = BuildCircuitKey(rule.Id, candidate.SiteKeyId),
                                    SiteName = site.Name,
                                    ProtocolType = ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic),
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
                                    ExtraHeaders = TryParseExtraHeaders(site.ExtraHeadersJson),
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

        // Web/Admin 宿主：从数据库联表查询
        return await _memoryCache.GetOrCreateAsync(
                RouteTargetsCacheKeyPrefix + "all",
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var independentClient = CreateIndependentClient();

                    // SqlSugar 不支持 LINQ query syntax 的多表 join，改为先各自读出再在内存连接。
                    var routeRows = await independentClient.Queryable<Domain.Proxy.ProxyRouteRule>().ToListAsync(cancellationToken);
                    var routeSiteRows = await independentClient.Queryable<Domain.Sites.Site>().ToListAsync(cancellationToken);
                    var models = await independentClient.Queryable<Domain.Models.ModelLibraryItem>().ToListAsync(cancellationToken);
                    // 一次性加载所有启用的站点密钥，按 SiteId 分组，供路由目标按 Key 展开为多条候选。
                    var siteKeyRows = await independentClient.Queryable<Domain.Sites.SiteKey>()
                        .Where(k => k.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var siteKeysBySite = siteKeyRows
                        .GroupBy(k => k.SiteId)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    // 一次性加载所有启用的兼容规则集，构建 Id→规则列表字典，供路由目标投影时查（避免 N+1）。
                    var profiles = await independentClient.Queryable<Domain.Proxy.CompatibilityProfile>()
                        .Where(p => p.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var profileRules = profiles.ToDictionary(
                        p => p.Id,
                        p => CompatibilityRuleParser.Parse(p.RulesJson));

                    // 基础路由投影（每条 route × site × model 一条），不含 Key 维度。
                    var baseRoutes = (
                            from route in routeRows
                            join site in routeSiteRows on route.SiteId equals site.Id
                            join model in models on route.UpstreamModelName equals model.ModelName into modelGroup
                            from model in modelGroup.DefaultIfEmpty()
                            where route.IsEnabled && site.IsEnabled
                            select new { route, site, model })
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

                        foreach (var candidate in candidates)
                        {
                            expanded.Add(new CachedProxyRouteTarget
                            {
                                RouteId = route.Id,
                                SiteId = site.Id,
                                SiteKeyId = candidate.SiteKeyId,
                                CircuitKey = BuildCircuitKey(route.Id, candidate.SiteKeyId),
                                SiteName = site.Name,
                                ProtocolType = ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic),
                                EndpointPathMode = site.EndpointPathMode,
                                SupportsOpenAi = site.SupportsOpenAi,
                                SupportsAnthropic = site.SupportsAnthropic,
                                ExternalModelName = route.ExternalModelName,
                                UpstreamModelName = route.UpstreamModelName,
                                SiteModelName = route.SiteModelName,
                                BaseUrl = site.BaseUrl,
                                ApiKey = candidate.ApiKey,
                                ExtraHeaders = TryParseExtraHeaders(site.ExtraHeadersJson),
                                ModelPriority = route.ModelPriority,
                                InstancePriority = route.InstancePriority,
                                Priority = route.Priority,
                                OverrideReasoningEffort = model?.OverrideReasoningEffort ?? string.Empty,
                                CompatibilityRules = CompatibilityRuleParser.GetRulesForModel(model?.CompatibilityProfileId, profileRules),
                                AvailabilityMode = NormalizeAvailabilityMode(route.AvailabilityMode),
                                TimeRangesJson = NormalizeTimeRangesJson(route.AvailabilityMode, route.TimeRangesJson)
                            });
                        }
                    }

                    return expanded;
                })
            ?? [];
    }

    /// <summary>
    /// 加载已启用模型缓存。
    /// Core 宿主中从配置快照的 Models 列表读取，Web/Admin 宿主中从数据库查询。
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

        // Web/Admin 宿主：从数据库查询
        return await _memoryCache.GetOrCreateAsync(
                EnabledModelsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var independentClient = CreateIndependentClient();

                    var models = await independentClient.Queryable<Domain.Models.ModelLibraryItem>()
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
    /// Core 宿主中从配置快照的 SiteModelMappings + Sites + Models 构建，
    /// Web/Admin 宿主中从数据库三表联查。
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
                                return new { m.ModelLibraryItemId, m.Id, model.ModelName, m.SiteId, site, m.RemoteModelName };
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
                                return candidates.Select(candidate => new CachedFallbackTarget
                                {
                                    ModelId = grouped.Key,
                                    ModelName = first.ModelName,
                                    SiteId = first.SiteId,
                                    SiteKeyId = candidate.SiteKeyId,
                                    CircuitKey = BuildCircuitKey(first.Id, candidate.SiteKeyId),
                                    SiteName = first.site.Name,
                                    ProtocolType = ResolveSiteProtocolType(first.site.SupportsOpenAi, first.site.SupportsAnthropic),
                                    BaseUrl = first.site.BaseUrl,
                                    EndpointPathMode = first.site.EndpointPathMode,
                                    ApiKey = candidate.ApiKey,
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

        // Web/Admin 宿主：从数据库三表联查
        return await _memoryCache.GetOrCreateAsync(
                FallbackMappingsCacheKey,
                async entry =>
                {
                    entry.Priority = CacheItemPriority.NeverRemove;

                    using var independentClient = CreateIndependentClient();

                    // SqlSugar 不支持 LINQ query syntax 的多表 join，改为先各自读出再在内存连接。
                    var fbMappings = await independentClient.Queryable<Domain.SiteCatalog.SiteModelMapping>().ToListAsync(cancellationToken);
                    var fbSites = await independentClient.Queryable<Domain.Sites.Site>().ToListAsync(cancellationToken);
                    var fbModels = await independentClient.Queryable<Domain.Models.ModelLibraryItem>().ToListAsync(cancellationToken);
                    var fbSiteKeys = await independentClient.Queryable<Domain.Sites.SiteKey>()
                        .Where(k => k.IsEnabled)
                        .ToListAsync(cancellationToken);
                    var fbSiteKeysBySite = fbSiteKeys
                        .GroupBy(k => k.SiteId)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    var rawMappings = (
                            from mapping in fbMappings
                            join site in fbSites on mapping.SiteId equals site.Id
                            join model in fbModels on mapping.ModelLibraryItemId equals model.Id
                            where mapping.IsEnabled && site.IsEnabled && model.IsEnabled
                            select new
                            {
                                ModelId = model.Id,
                                MappingId = mapping.Id,
                                model.ModelName,
                                SiteId = site.Id,
                                SiteName = site.Name,
                                site.SupportsOpenAi,
                                site.SupportsAnthropic,
                                site.BaseUrl,
                                site.EndpointPathMode,
                                site.ApiKey,
                                SiteModelName = mapping.RemoteModelName,
                                site.ExtraHeadersJson
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

                            var candidates = ResolveSiteKeyCandidates(first.SiteId, first.ApiKey, fbSiteKeysBySite);
                            return candidates.Select(candidate => new CachedFallbackTarget
                            {
                                ModelId = grouped.Key,
                                ModelName = first.ModelName,
                                SiteId = first.SiteId,
                                SiteKeyId = candidate.SiteKeyId,
                                CircuitKey = BuildCircuitKey(first.MappingId, candidate.SiteKeyId),
                                SiteName = first.SiteName,
                                ProtocolType = ResolveSiteProtocolType(first.SupportsOpenAi, first.SupportsAnthropic),
                                BaseUrl = first.BaseUrl,
                                EndpointPathMode = first.EndpointPathMode,
                                ApiKey = candidate.ApiKey,
                                SiteModelName = first.SiteModelName,
                                ExtraHeaders = TryParseExtraHeaders(first.ExtraHeadersJson)
                            });
                        })
                        .ToList();

                    // 兜底字典保留每个模型的主 Key 候选（Priority 最小的那个）。
                    return mappings
                        .GroupBy(x => x.ModelId)
                        .ToDictionary(g => g.Key, g => g.First());
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
    /// 合成熔断/并发身份键。多 Key 候选用确定性派生的 Guid，保证同一 (RouteId, SiteKeyId) 组合
    /// 始终映射到相同的合成键——这样某个 Key 连续失败只熔断它自己，不误伤同站点其他 Key。
    /// SiteKey 为 null 的兼容候选用 RouteId 本身。
    /// </summary>
    internal static Guid BuildCircuitKey(Guid routeId, Guid? siteKeyId)
    {
        if (siteKeyId is null)
        {
            return routeId;
        }

        // 确定性派生：把 RouteId 和 SiteKeyId 的字节拼接后做 SHA256，取前 16 字节为 Guid。
        // 这样合成键稳定且与真实 RouteRule.Id 空间冲突概率可忽略（不同 RouteId 必然不同键）。
        Span<byte> buffer = stackalloc byte[32];
        routeId.TryWriteBytes(buffer[..16]);
        siteKeyId.Value.TryWriteBytes(buffer[16..]);
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
    /// <summary>
    /// Codex 功能总开关。
    /// </summary>
    public bool CodexFeaturesEnabled { get; set; }
    /// <summary>
    /// Codex 巡检自动执行开关。
    /// </summary>
    public bool CodexInspectionEnabled { get; set; }
    /// <summary>
    /// Codex 巡检周期（分钟）。
    /// </summary>
    public int CodexInspectionIntervalMinutes { get; set; } = 30;
    /// <summary>
    /// Codex 额度缓存最大小时数。
    /// </summary>
    public int CodexQuotaMaxCacheHours { get; set; } = 6;
    /// <summary>
    /// Codex 自动禁用阈值（百分比，1-100）。
    /// </summary>
    public int CodexAutoDisableThresholdPercent { get; set; } = 95;
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
}

