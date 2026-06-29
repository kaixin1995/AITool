using AITool.Domain.Detection;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Persistence;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.Scheduling;

/// <summary>
/// 基于 Hangfire 的定时检测调度器，负责注册和执行周期性检测任务
/// </summary>
public sealed class HangfireDetectionScheduler
{
    /// <summary>
    /// 服务范围工厂，用于在 Hangfire 作业中创建独立的 DI 作用域
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// 注入服务范围工厂
    /// </summary>
    public HangfireDetectionScheduler(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 将所有启用的检测任务注册到 Hangfire 调度队列
    /// </summary>
    public async Task ScheduleAllAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 数据库建表在启动时由 SqlSugarSetup.InitializeDatabase 完成，这里直接查询。
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

    /// <summary>
    /// 执行单次检测任务，遍历所有站点模型映射并逐一发起真实代理请求
    /// </summary>
    public async Task ExecuteDetectionTaskAsync(Guid detectionTaskId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var requestService = scope.ServiceProvider.GetRequiredService<ModelHealthRequestService>();

        var detectionTask = await dbContext.DetectionTasks
            .FirstAsync(t => t.Id == detectionTaskId, cancellationToken);
        if (detectionTask is null || !detectionTask.IsEnabled) return;

        // 创建执行记录
        var execution = new DetectionTaskExecution
        {
            DetectionTaskId = detectionTaskId,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow
        };
        await dbContext.InsertAsync(execution, cancellationToken);

        // 如果任务指定了模型，只检测该模型的映射
        var query = dbContext.SiteModelMappings
            .WhereIF(detectionTask.ModelLibraryItemId.HasValue, m => m.ModelLibraryItemId == detectionTask.ModelLibraryItemId!.Value);

        var mappings = await query.ToListAsync(cancellationToken);
        var runtimeSettings = await dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken)
            ?? new AITool.Domain.Operations.SystemRuntimeSettings();
        var successCount = 0;
        var failCount = 0;

        foreach (var batch in mappings.Chunk(Math.Max(1, runtimeSettings.DetectionConcurrency)))
        {
            var results = await Task.WhenAll(batch.Select(mapping => requestService.ProbeMappingAsync(mapping.Id, "detection-task", cancellationToken)));
            foreach (var result in results)
            {
                if (result.Status == "success") successCount++;
                else failCount++;
            }
        }

        execution.Status = "completed";
        execution.FinishedAt = DateTimeOffset.UtcNow;
        execution.Summary = $"共检测 {mappings.Count} 个映射，成功 {successCount}，失败 {failCount}";

        await dbContext.UpdateAsync(execution, cancellationToken);
    }
}
