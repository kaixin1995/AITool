using AITool.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// 提供管理后台宿主启动时共享的初始化能力，封装数据库创建、Schema 迁移和 Hangfire 调度注册。
/// <para>
/// Web 宿主和 Admin 宿主在启动时都需要执行数据库初始化和 Hangfire 调度任务注册。
/// 此类将这些通用步骤集中管理，避免两个 Program.cs 中的重复代码。
/// </para>
/// </summary>
public static class AdminStartupInitializer
{
    /// <summary>
    /// 执行管理后台启动时的数据库初始化和定时任务注册。
    /// <para>
    /// 包含三个步骤：
    /// <list type="number">
    ///     <item>创建或打开 SQLite 数据库（EnsureCreated）</item>
    ///     <item>补齐历史数据库缺失的表结构（Schema 迁移）</item>
    ///     <item>注册所有已启用的 Hangfire 定时检测任务</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="serviceProvider">应用程序的服务提供者。</param>
    /// <param name="logger">启动日志记录器，用于记录 Hangfire 调度注册失败时的警告。</param>
    public static async Task InitializeAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 确保数据库文件存在并创建表结构。
        // SQLite 的 EnsureCreated 仅在数据库不存在时创建完整结构，
        // 已有旧库不会自动增加新列或新表，下面的 Schema 迁移负责补齐。
        db.Database.EnsureCreated();

        // 启用 WAL 模式并调优持久化的 PRAGMA。
        // WAL（Write-Ahead Logging）让读写不再互斥，并发读写吞吐提升 3-10 倍；
        // synchronous=NORMAL 在 WAL 下安全且减少 fsync 频次 50%+。
        // 这两个 PRAGMA 是数据库文件级持久属性，执行一次即可。
        // 连接级 PRAGMA（cache_size/busy_timeout）由 SqlitePragmaInterceptor 在每次连接打开时设置。
        await ApplyPersistentPragmasAsync(db);

        // 补齐历史数据库缺失的列和表。
        // 这些迁移步骤是幂等的：已存在的列不会重复添加。
        await DatabaseSchemaMigrator.EnsureProxyUsageLogSchemaAsync(db);

        // 注册所有已启用的定时检测任务到 Hangfire 调度器。
        // 如果注册失败（如任务配置异常），仅记录警告，不阻止启动。
        var scheduler = scope.ServiceProvider.GetRequiredService<HangfireDetectionScheduler>();
        try
        {
            await scheduler.ScheduleAllAsync(default);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "启动时注册定时检测任务失败，将在下次启动时重试");
        }

        // 预热代理热路径缓存，避免首个代理请求触发运行时设置的 DB 往返。
        // 注意：测试环境跳过预热，因为测试工厂的种子数据在预热后才插入。
        // 只预热 RuntimeSettings（即使预热到空数据，数据变更时会被 Invalidate 重建）；
        // 不预热模型列表/路由（NeverRemove 缓存，预热空数据会导致后续请求永久命中空缓存）。
        if (!serviceProvider.GetService<IHostEnvironment>()?.IsEnvironment("Testing") ?? true)
        {
            try
            {
                var cache = scope.ServiceProvider.GetService<Proxy.ProxyRequestMetadataCache>();
                if (cache is not null)
                {
                    await cache.GetRuntimeSettingsAsync(default);
                    logger.LogInformation("代理热路径缓存预热完成（运行时设置）");
                }
            }
            catch (Exception ex)
            {
                // 预热失败不阻塞启动，首个请求会触发懒加载。
                logger.LogWarning(ex, "启动时预热代理缓存失败，首个代理请求将懒加载");
            }
        }
    }

    /// <summary>
    /// 应用数据库文件级持久化的 PRAGMA（WAL 模式 + synchronous=NORMAL）。
    /// 幂等：已设置时重复执行无副作用。
    /// </summary>
    private static async Task ApplyPersistentPragmasAsync(AppDbContext db)
    {
        var pragmaStatements = new[]
        {
            "PRAGMA journal_mode=WAL;",
            "PRAGMA synchronous=NORMAL;"
        };
        foreach (var statement in pragmaStatements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }
            catch
            {
                // PRAGMA 执行失败不阻塞启动（如只读环境），降级为默认模式。
            }
        }
    }
}
