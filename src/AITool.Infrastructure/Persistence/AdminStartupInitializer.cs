using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// 提供管理后台宿主启动时共享的初始化能力，封装数据库创建和 Hangfire 调度注册。
/// <para>
/// 管理后台宿主（以及历史上与 Web 共享启动逻辑的宿主）在启动时都需要执行数据库初始化和
/// Hangfire 调度任务注册。此类将这些通用步骤集中管理，避免 Program.cs 中的重复代码。
/// </para>
/// </summary>
public static class AdminStartupInitializer
{
    /// <summary>
    /// 执行管理后台启动时的数据库初始化和定时任务注册。
    /// <para>
    /// 包含三个步骤：
    /// <list type="number">
    ///     <item>CodeFirst 建表/补列 + 持久化 PRAGMA（SqlSugarSetup.InitializeDatabase）</item>
    ///     <item>（历史 ALTER TABLE 升级脚本已由 CodeFirst 差量建表替代，不再需要）</item>
    ///     <item>注册所有已启用的 Hangfire 定时检测任务</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="serviceProvider">应用程序的服务提供者。</param>
    /// <param name="logger">启动日志记录器，用于记录 Hangfire 调度注册失败时的警告。</param>
    public static async Task InitializeAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        using var scope = serviceProvider.CreateScope();

        // 初始化数据库：CodeFirst 建表（表已存在时只增不删，自动补齐缺失列）+ 持久化/连接级 PRAGMA。
        // 替代原 EF 的 EnsureCreated + 手写 ALTER TABLE 升级脚本。
        var sqlSugar = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        SqlSugarSetup.InitializeDatabase(sqlSugar);

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
                var cache = scope.ServiceProvider.GetService<ProxyRequestMetadataCache>();
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
}
