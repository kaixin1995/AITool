using AITool.Application.Operations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// Admin 宿主启动后自动将当前数据库配置同步到 Core 宿主的后台服务。
///
/// 工作流程：
/// 1. Admin 启动后等待一小段延迟（确保 Core 宿主可能已经先启动完成）
/// 2. 从数据库构建完整配置快照
/// 3. 向 Core 下发全量同步
/// 4. 如果 Core 尚未就绪（连接失败），按指数退避重试
///
/// 这个服务确保 Admin 和 Core 联合部署时，Core 不会长时间处于无配置状态。
/// 后续 Admin 后台页面修改配置后，通过 AdminCacheInvalidationService
/// 会再次向 Core 下发全量同步，本服务只负责启动时的首次同步。
/// </summary>
public sealed class CoreConfigSyncHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CoreConfigSyncHostedService> _logger;
    private readonly CoreSyncStatusStore _syncStatusStore;

    // 启动后首次同步前的等待时间，给 Core 宿主一点启动时间
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    // 最大重试次数
    private const int MaxRetries = 5;

    // 重试基础间隔
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 初始化 Core 配置同步后台服务。
    /// </summary>
    public CoreConfigSyncHostedService(
        IServiceProvider serviceProvider,
        ILogger<CoreConfigSyncHostedService> logger,
        CoreSyncStatusStore syncStatusStore)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncStatusStore = syncStatusStore;
    }

    /// <summary>
    /// 服务启动时触发首次配置同步。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 在后台线程执行同步，不阻塞宿主启动
        _ = Task.Run(() => SyncConfigToCoreAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 服务停止时无需额外处理。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 执行配置同步主流程：构建快照 → 下发到 Core → 按需重试。
    /// </summary>
    private async Task SyncConfigToCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("开始向 Core 宿主执行启动时配置同步...");

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISystemRuntimeSettingsService>();
                var coreClient = scope.ServiceProvider.GetRequiredService<CoreAdminClient>();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var siteCount = await dbContext.Sites.CountAsync(cancellationToken);
                var accessKeyCount = await dbContext.ProxyAccessKeys.CountAsync(cancellationToken);

                if (siteCount == 0 || accessKeyCount == 0)
                {
                    _logger.LogInformation(
                        "跳过启动时 Core 配置同步：当前数据库缺少必要配置。Sites={SiteCount}, AccessKeys={AccessKeyCount}。",
                        siteCount,
                        accessKeyCount);
                    return;
                }

                var attemptedAt = DateTimeOffset.UtcNow;
                var configVersion = attemptedAt.ToUnixTimeMilliseconds();
                var snapshot = await settingsService.BuildCoreRuntimeConfigSnapshotAsync(configVersion, cancellationToken);

                // 向 Core 下发全量同步
                var result = await coreClient.FullSyncAsync(snapshot, cancellationToken);

                if (result.Ignored)
                {
                    _logger.LogInformation(
                        "Core 宿主已持有相同配置（版本 {ConfigVersion}），跳过同步。",
                        result.ConfigVersion);
                    _syncStatusStore.MarkSuccess(attemptedAt, $"启动同步已忽略（版本 {result.ConfigVersion}）");
                }
                else
                {
                    _logger.LogInformation(
                        "配置同步成功。Core 已应用版本 {ConfigVersion}，哈希 {ConfigHash}。",
                        result.ConfigVersion,
                        result.ConfigHash);
                    _syncStatusStore.MarkSuccess(attemptedAt, $"启动同步成功（版本 {result.ConfigVersion}）");
                }

                return;
            }
            catch (Exception ex)
            {
                _syncStatusStore.MarkFailure(
                    DateTimeOffset.UtcNow,
                    $"启动同步失败（第 {attempt}/{MaxRetries} 次）",
                    ex.GetBaseException().Message);
                _logger.LogWarning(
                    ex,
                    "向 Core 宿主同步配置失败（第 {Attempt}/{MaxRetries} 次尝试）。" +
                    "如果 Core 宿主尚未启动，将在 {RetryDelay} 秒后重试。",
                    attempt,
                    MaxRetries,
                    RetryBaseDelay.TotalSeconds * attempt);

                if (attempt < MaxRetries)
                {
                    try
                    {
                        // 指数退避：每次重试间隔递增
                        await Task.Delay(RetryBaseDelay * attempt, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        _logger.LogError(
            "连续 {MaxRetries} 次尝试向 Core 宿主同步配置均失败。" +
            "Core 宿主可能在稍后启动后需要手动触发配置同步，或等待下一次 Admin 缓存失效时自动同步。",
            MaxRetries);
    }
}
