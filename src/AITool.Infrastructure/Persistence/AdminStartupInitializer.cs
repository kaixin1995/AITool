using AITool.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        // 补齐历史数据库缺失的列和表。
        // 这些迁移步骤是幂等的：已存在的列不会重复添加。
        await DatabaseSchemaMigrator.EnsureProxyUsageLogSchemaAsync(db);
        await DatabaseSchemaMigrator.EnsureConversationLogSchemaAsync(db);

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
    }
}
