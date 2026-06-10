using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Admin.Services;

/// <summary>
/// Admin 宿主侧的缓存失效门面。
/// 当 Admin 写入数据库后，通过 <see cref="CoreAdminClient"/> 向 Core 宿主下发全量配置快照，
/// 使 Core 侧的运行时缓存（路由、模型、密钥等）及时刷新。
/// <para>
/// 这是对 AITool.Web 中同名服务的平行替代——Web 版直接操作
/// <c>ProxyRequestMetadataCache</c> 运行时对象，Admin 版则通过 HTTP 调用 Core 间接完成。
/// </para>
/// </summary>
public sealed class AdminCacheInvalidationService
{
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

    /// <summary>
    /// 日志记录器。
    /// </summary>
    private readonly ILogger<AdminCacheInvalidationService> _logger;

    /// <summary>
    /// 配置版本号，每次同步递增。单进程内递增即可满足唯一性。
    /// </summary>
    private long _configVersion;

    /// <summary>
    /// 初始化 Admin 侧缓存失效服务。
    /// </summary>
    public AdminCacheInvalidationService(
        CoreAdminClient coreClient,
        AppDbContext dbContext,
        ISystemRuntimeSettingsService runtimeSettingsService,
        ILogger<AdminCacheInvalidationService> logger)
    {
        _coreClient = coreClient;
        _dbContext = dbContext;
        _runtimeSettingsService = runtimeSettingsService;
        _logger = logger;
    }

    /// <summary>
    /// 失效访问密钥相关缓存。
    /// 触发全量同步，使 Core 刷新密钥缓存。
    /// </summary>
    public async Task InvalidateAccessKeysAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(cancellationToken);
    }

    /// <summary>
    /// 失效运行时设置缓存。
    /// </summary>
    public async Task InvalidateRuntimeSettingsAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(cancellationToken);
    }

    /// <summary>
    /// 失效模型相关缓存。
    /// </summary>
    public async Task InvalidateModelMetadataAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(cancellationToken);
    }

    /// <summary>
    /// 失效路由相关缓存。
    /// </summary>
    public async Task InvalidateRouteTargetsAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(cancellationToken);
    }

    /// <summary>
    /// 失效后台路由配置元数据缓存。
    /// </summary>
    public async Task InvalidateAdminRouteMetadataAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(cancellationToken);
    }

    /// <summary>
    /// 失效运行时路由缓存。
    /// </summary>
    public async Task InvalidateRuntimeRouteTargetsAsync(CancellationToken cancellationToken = default)
    {
        await SyncToCoreAsync(cancellationToken);
    }

    /// <summary>
    /// 从当前数据库读取所有主数据，构建完整配置快照并通过 CoreAdminClient 下发给 Core。
    /// Core 侧收到快照后会更新 <c>ProxyRequestMetadataCache</c> 等运行时缓存。
    /// <para>
    /// 采用全量同步模式，利用哈希校验避免无变化时的重复切换。
    /// </para>
    /// </summary>
    private async Task SyncToCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 递增配置版本号
            var version = Interlocked.Increment(ref _configVersion);

            // 从数据库读取最新主数据
            var sites = await _dbContext.Sites.ToListAsync(cancellationToken);
            var models = await _dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
            var mappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
            var routeEntries = await _dbContext.ProxyRouteEntries.ToListAsync(cancellationToken);
            var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
            var accessKeys = await _dbContext.ProxyAccessKeys.ToListAsync(cancellationToken);
            var runtimeSettings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);

            // 构建完整配置快照
            var snapshot = CoreRuntimeConfigSnapshotBuilder.Build(
                sites, models, mappings, routeEntries, routeRules, accessKeys,
                runtimeSettings, version, DateTimeOffset.UtcNow);

            // 下发给 Core
            var result = await _coreClient.FullSyncAsync(snapshot, cancellationToken);

            _logger.LogDebug(
                "Admin→Core 配置同步完成。Version={Version}, Applied={Applied}, Ignored={Ignored}",
                result.ConfigVersion, result.Applied, result.Ignored);
        }
        catch (Exception ex)
        {
            // 同步失败不影响 Admin 写操作本身，只记录警告日志。
            // Core 侧有定时轮询机制兜底，最终会拉到最新数据。
            _logger.LogWarning(ex, "Admin→Core 配置同步失败，将在下次写入时重试");
        }
    }
}
