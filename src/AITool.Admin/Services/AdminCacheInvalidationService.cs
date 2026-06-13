using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.EntityFrameworkCore;

namespace AITool.Admin.Services;

/// <summary>
/// Admin 宿主侧的缓存失效门面。
/// 当 Admin 写入数据库后，通过 <see cref="CoreAdminClient"/> 向 Core 宿主下发配置同步请求，
/// 使 Core 侧的运行时缓存（路由、模型、密钥等）及时刷新。
/// <para>
/// 同步策略：优先使用增量 Patch（仅携带变更类别的完整列表），
/// 如果 Core 尚未初始化或 Patch 失败，自动回退到全量同步。
/// </para>
/// </summary>
public sealed class AdminCacheInvalidationService
{
    /// <summary>
    /// JSON 序列化选项，用于 Patch 哈希计算。
    /// </summary>
    private static readonly JsonSerializerOptions PatchHashSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// Core 客户端，用于向 Core 宿主下发配置同步请求。
    /// </summary>
    private readonly CoreAdminClient _coreClient;

    /// <summary>
    /// 数据库上下文，用于读取最新主数据以构建配置快照。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 运行时设置服务，用于读取当前系统设置。
    /// </summary>
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    private readonly CoreSyncStatusStore _syncStatusStore;

    /// <summary>
    /// 日志记录器。
    /// </summary>
    private readonly ILogger<AdminCacheInvalidationService> _logger;
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 配置版本号，使用 Unix 时间戳毫秒保证跨 Admin 重启单调递增。
    /// Core 端的 full-sync（启动时）和 patch-sync（运行时）都必须使用同一版本号体系，
    /// 否则 Core 会因为 patch 版本号小于 full-sync 版本号而一律忽略。
    /// </summary>
    private long ConfigVersion()
    {
        // 以当前时间戳为基础版本号，同时用原子递增避免同一毫秒内多次 sync 的版本冲突。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var version = Interlocked.Increment(ref _versionCounter);
        return Math.Max(version, now);
    }

    private long _versionCounter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// 自上次同步以来累积的变更类别集合。
    /// 多次 Invalidate 调用会合并类别，最终在一次同步中一起发送。
    /// </summary>
    private readonly HashSet<string> _pendingCategories = new(StringComparer.Ordinal);

    /// <summary>
    /// 累积类别的锁，确保并发 Invalidate 调用不会丢失类别。
    /// </summary>
    private readonly object _categoryLock = new();

    /// <summary>
    /// 初始化 Admin 侧缓存失效服务。
    /// </summary>
    public AdminCacheInvalidationService(
        CoreAdminClient coreClient,
        AppDbContext dbContext,
        ISystemRuntimeSettingsService runtimeSettingsService,
        CoreSyncStatusStore syncStatusStore,
        ILogger<AdminCacheInvalidationService> logger,
        ProxyRequestMetadataCache metadataCache)
    {
        _coreClient = coreClient;
        _dbContext = dbContext;
        _runtimeSettingsService = runtimeSettingsService;
        _syncStatusStore = syncStatusStore;
        _logger = logger;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 失效访问密钥相关缓存。
    /// </summary>
    public async Task InvalidateAccessKeysAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(["AccessKeys"], cancellationToken);
        _metadataCache.InvalidateAccessKeys(); // Admin 本地缓存同步失效
    }

    public async Task InvalidateRuntimeSettingsAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(["RuntimeSettings"], cancellationToken);
        _metadataCache.InvalidateRuntimeSettings(); // Admin 本地缓存同步失效
    }

    public async Task InvalidateModelMetadataAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(["Models", "SiteModelMappings"], cancellationToken);
        _metadataCache.InvalidateModelMetadata(); // Admin 本地缓存同步失效
    }

    public async Task InvalidateRouteTargetsAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(["Sites", "RouteRules", "RouteEntries"], cancellationToken);
        _metadataCache.InvalidateRouteTargets(); // Admin 本地缓存同步失效
    }

    public async Task InvalidateAdminRouteMetadataAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(["Sites", "RouteRules", "RouteEntries"], cancellationToken);
        _metadataCache.InvalidateRouteTargets(); // Admin 本地缓存同步失效
    }

    public async Task InvalidateRuntimeRouteTargetsAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(["Sites", "RouteRules", "RouteEntries"], cancellationToken);
        _metadataCache.InvalidateRouteTargets(); // Admin 本地缓存同步失效
    }

    /// <summary>
    /// 向 Core 发送增量 Patch 同步。
    /// 仅读取变更类别对应的数据库表，构建 Patch 载荷发送给 Core。
    /// 如果 Core 尚未初始化（返回 400），自动回退到全量同步。
    /// </summary>
    private async Task SyncToCoreAsync(string[] categories, CancellationToken cancellationToken)
    {
        try
        {
            var attemptedAt = DateTimeOffset.UtcNow;
            var version = ConfigVersion();
            var patch = await BuildPatchAsync(categories, version, cancellationToken);

            try
            {
                var result = await _coreClient.PatchSyncAsync(patch, cancellationToken);

                _logger.LogDebug(
                    "Admin→Core 增量同步完成。Version={Version}, Categories=[{Categories}], Applied={Applied}",
                    result.ConfigVersion, string.Join(", ", categories), result.Applied);
                _syncStatusStore.MarkSuccess(attemptedAt, $"增量同步成功（版本 {result.ConfigVersion}）");
            }
            catch (HttpRequestException ex) when (IsCoreNotInitialized(ex))
            {
                // Core 尚未初始化，回退到全量同步
                _logger.LogDebug(ex, "Core 尚未初始化，回退到全量同步");
                await FullSyncFallbackAsync(version, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // 同步失败不影响 Admin 写操作本身，只记录警告日志。
            // Core 侧有定时轮询机制兜底，最终会拉到最新数据。
            _syncStatusStore.MarkFailure(DateTimeOffset.UtcNow, "增量同步失败", ex.GetBaseException().Message);
            _logger.LogWarning(ex, "Admin→Core 配置同步失败，将在下次写入时重试");
        }
    }

    /// <summary>
    /// 构建增量 Patch 载荷，只读取变更类别对应的数据库表。
    /// </summary>
    private async Task<ConfigPatchPayload> BuildPatchAsync(string[] categories, long version, CancellationToken cancellationToken)
    {
        var categorySet = new HashSet<string>(categories, StringComparer.Ordinal);
        var patch = new ConfigPatchPayload
        {
            ConfigVersion = version,
            Categories = [.. categorySet]
        };

        // 按需读取数据库表，只加载变更类别对应的数据
        if (categorySet.Contains("Sites"))
        {
            var sites = await _dbContext.Sites.ToListAsync(cancellationToken);
            patch.Sites = sites
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeSite
                {
                    Id = x.Id,
                    Name = x.Name,
                    BaseUrl = x.BaseUrl,
                    EndpointPathMode = x.EndpointPathMode,
                    ApiKey = x.ApiKey,
                    ProtocolType = x.ProtocolType,
                    SupportsOpenAi = x.SupportsOpenAi,
                    SupportsAnthropic = x.SupportsAnthropic,
                    IsEnabled = x.IsEnabled
                })
                .ToList();
        }

        if (categorySet.Contains("Models"))
        {
            var models = await _dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
            patch.Models = models
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeModel
                {
                    Id = x.Id,
                    ModelName = x.ModelName,
                    DisplayName = x.DisplayName,
                    IsEnabled = x.IsEnabled
                })
                .ToList();
        }

        if (categorySet.Contains("SiteModelMappings"))
        {
            var mappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
            patch.SiteModelMappings = mappings
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeSiteModelMapping
                {
                    Id = x.Id,
                    SiteId = x.SiteId,
                    ModelLibraryItemId = x.ModelLibraryItemId,
                    RemoteModelName = x.RemoteModelName,
                    LastStatus = x.LastStatus,
                    IsEnabled = x.IsEnabled,
                    MaxConcurrency = x.MaxConcurrency
                })
                .ToList();
        }

        if (categorySet.Contains("RouteEntries"))
        {
            var routeEntries = await _dbContext.ProxyRouteEntries.ToListAsync(cancellationToken);
            patch.RouteEntries = routeEntries
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeRouteEntry
                {
                    Id = x.Id,
                    EntryName = x.EntryName
                })
                .ToList();
        }

        if (categorySet.Contains("RouteRules"))
        {
            var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
            patch.RouteRules = routeRules
                .OrderBy(x => x.ExternalModelName, StringComparer.Ordinal)
                .ThenBy(x => x.ModelPriority)
                .ThenBy(x => x.InstancePriority)
                .ThenBy(x => x.Priority)
                .ThenBy(x => x.Id)
                .Select(x => new CoreRuntimeRouteRule
                {
                    Id = x.Id,
                    ExternalModelName = x.ExternalModelName,
                    UpstreamModelName = x.UpstreamModelName,
                    SiteId = x.SiteId,
                    SiteModelName = x.SiteModelName,
                    Priority = x.Priority,
                    ModelPriority = x.ModelPriority,
                    InstancePriority = x.InstancePriority,
                    IsEnabled = x.IsEnabled,
                    AvailabilityMode = x.AvailabilityMode,
                    TimeRangesJson = x.TimeRangesJson
                })
                .ToList();
        }

        if (categorySet.Contains("AccessKeys"))
        {
            var accessKeys = await _dbContext.ProxyAccessKeys.ToListAsync(cancellationToken);
            patch.AccessKeys = accessKeys
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeAccessKey
                {
                    Id = x.Id,
                    KeyName = x.KeyName,
                    PlainKey = x.PlainKey,
                    AccessKeyHash = x.AccessKeyHash,
                    MaskedValue = x.MaskedValue,
                    IsEnabled = x.IsEnabled
                })
                .ToList();
        }

        if (categorySet.Contains("RuntimeSettings"))
        {
            var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
            patch.RuntimeSettings = new CoreRuntimeSettings
            {
                ProxyRequestTimeoutSeconds = settings.ProxyRequestTimeoutSeconds,
                ProxyRetryCount = settings.ProxyRetryCount,
                CircuitBreakerFailureThreshold = settings.CircuitBreakerFailureThreshold,
                CircuitBreakerRecoveryMinutes = settings.CircuitBreakerRecoveryMinutes,
                ConcurrencyMode = settings.ConcurrencyMode,
                ConcurrencyQueueTimeoutSeconds = settings.ConcurrencyQueueTimeoutSeconds,
                ConversationLogEnabled = settings.ConversationLogEnabled,
                DeveloperFeaturesEnabled = settings.DeveloperFeaturesEnabled
            };
        }

        // 计算 Patch 哈希用于去重
        patch.PatchHash = ComputePatchHash(patch);

        return patch;
    }

    /// <summary>
    /// 全量同步回退。当 Core 尚未初始化或 Patch 同步不可用时使用。
    /// </summary>
    private async Task FullSyncFallbackAsync(long version, CancellationToken cancellationToken)
    {
        var siteCount = await _dbContext.Sites.AsNoTracking().CountAsync(cancellationToken);
        var accessKeyCount = await _dbContext.ProxyAccessKeys.AsNoTracking().CountAsync(cancellationToken);

        if (siteCount == 0 || accessKeyCount == 0)
        {
            _logger.LogInformation(
                "跳过 Admin→Core 全量同步回退：当前数据库缺少必要配置。Sites={SiteCount}, AccessKeys={AccessKeyCount}。",
                siteCount,
                accessKeyCount);
            _syncStatusStore.MarkFailure(DateTimeOffset.UtcNow, "全量同步已跳过", "缺少 Sites 或 AccessKeys");
            return;
        }

        var sites = await _dbContext.Sites.ToListAsync(cancellationToken);
        var models = await _dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
        var mappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
        var routeEntries = await _dbContext.ProxyRouteEntries.ToListAsync(cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var accessKeys = await _dbContext.ProxyAccessKeys.ToListAsync(cancellationToken);
        var runtimeSettings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);

        var snapshot = CoreRuntimeConfigSnapshotBuilder.Build(
            sites, models, mappings, routeEntries, routeRules, accessKeys,
            runtimeSettings, version, DateTimeOffset.UtcNow);

        var result = await _coreClient.FullSyncAsync(snapshot, cancellationToken);

        _logger.LogDebug(
            "Admin→Core 全量同步回退完成。Version={Version}, Applied={Applied}",
            result.ConfigVersion, result.Applied);
        _syncStatusStore.MarkSuccess(DateTimeOffset.UtcNow, $"全量同步成功（版本 {result.ConfigVersion}）");
    }

    /// <summary>
    /// 计算 Patch 载荷中携带数据的 SHA256 哈希，用于 Core 端去重判断。
    /// 只序列化非 null 的类别数据。
    /// </summary>
    private static string ComputePatchHash(ConfigPatchPayload patch)
    {
        var payload = new Dictionary<string, object?>();

        // 按固定顺序添加非 null 类别，确保哈希确定性
        if (patch.Sites is not null) payload["Sites"] = patch.Sites;
        if (patch.Models is not null) payload["Models"] = patch.Models;
        if (patch.SiteModelMappings is not null) payload["SiteModelMappings"] = patch.SiteModelMappings;
        if (patch.RouteEntries is not null) payload["RouteEntries"] = patch.RouteEntries;
        if (patch.RouteRules is not null) payload["RouteRules"] = patch.RouteRules;
        if (patch.AccessKeys is not null) payload["AccessKeys"] = patch.AccessKeys;
        if (patch.RuntimeSettings is not null) payload["RuntimeSettings"] = patch.RuntimeSettings;

        var json = JsonSerializer.Serialize(payload, PatchHashSerializerOptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// 判断 HTTP 异常是否因为 Core 尚未初始化（返回 400 Bad Request）。
    /// 用于决定是否回退到全量同步。
    /// </summary>
    private static bool IsCoreNotInitialized(HttpRequestException ex)
    {
        // Core 未初始化时 patch-sync 返回 400，
        // HttpClient 会抛出 HttpRequestException 且 StatusCode == BadRequest
        return ex.Message.Contains("400");
    }
}
