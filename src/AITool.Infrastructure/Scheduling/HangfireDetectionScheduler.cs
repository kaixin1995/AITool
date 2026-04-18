using AITool.Application.Detection;
using AITool.Domain.Detection;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.Scheduling;

// 基于 Hangfire 的定时检测调度器，负责注册和执行周期性检测任务
public sealed class HangfireDetectionScheduler
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HangfireDetectionScheduler(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // 将所有启用的检测任务注册到 Hangfire 调度队列
    public async Task ScheduleAllAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 确保数据库已创建，避免表不存在导致查询失败
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var tasks = await dbContext.DetectionTasks
            .Where(t => t.IsEnabled)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            RecurringJob.AddOrUpdate(
                $"detection-{task.Id}",
                () => ExecuteDetectionTaskAsync(task.Id, CancellationToken.None),
                task.CronExpression);
        }
    }

    // 执行单次检测任务，遍历所有站点模型映射并逐一探测
    public async Task ExecuteDetectionTaskAsync(Guid detectionTaskId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var probeService = scope.ServiceProvider.GetRequiredService<IModelProbeService>();

        var detectionTask = await dbContext.DetectionTasks.FindAsync([detectionTaskId], cancellationToken);
        if (detectionTask is null || !detectionTask.IsEnabled) return;

        // 创建执行记录
        var execution = new DetectionTaskExecution
        {
            DetectionTaskId = detectionTaskId,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow
        };
        dbContext.DetectionTaskExecutions.Add(execution);
        await dbContext.SaveChangesAsync(cancellationToken);

        var query = dbContext.SiteModelMappings.AsQueryable();

        // 如果任务指定了模型，只检测该模型的映射
        if (detectionTask.ModelLibraryItemId.HasValue)
        {
            query = query.Where(m => m.ModelLibraryItemId == detectionTask.ModelLibraryItemId.Value);
        }

        var mappings = await query.ToListAsync(cancellationToken);
        var successCount = 0;
        var failCount = 0;

        foreach (var mapping in mappings)
        {
            var site = await dbContext.Sites.FindAsync([mapping.SiteId], cancellationToken);
            var model = await dbContext.ModelLibraryItems.FindAsync([mapping.ModelLibraryItemId], cancellationToken);
            if (site is null || model is null) continue;

            var result = await probeService.ProbeAsync(site, model, cancellationToken);

            // 记录每条检测日志
            dbContext.DetectionLogs.Add(new DetectionLog
            {
                SiteId = mapping.SiteId,
                ModelLibraryItemId = mapping.ModelLibraryItemId,
                Status = result.Success ? "success" : "fail",
                DurationMs = result.DurationMs,
                ErrorMessage = result.ErrorMessage,
                CheckedAt = DateTimeOffset.UtcNow
            });

            // 更新映射状态
            mapping.LastStatus = result.Success ? "success" : "fail";

            if (result.Success) successCount++;
            else failCount++;
        }

        // 更新执行记录
        execution.Status = "completed";
        execution.FinishedAt = DateTimeOffset.UtcNow;
        execution.Summary = $"共检测 {mappings.Count} 个映射，成功 {successCount}，失败 {failCount}";

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
