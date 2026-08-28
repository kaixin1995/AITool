using AITool.Domain.Detection;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Scheduling;
using AITool.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 检测任务 API：Cron 定时检测任务的管理与执行。
/// <para>
/// 迁移自 <c>Pages/Admin/DetectionTasks/Index.cshtml.cs</c>。
/// 创建/启停后立即重新注册所有任务到 Hangfire；立即执行直接调用调度器（同步等待）。
/// 加载列表时会顺手解绑「指向已删除模型」的孤儿任务。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/detection-tasks")]
public sealed class DetectionTasksApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// Hangfire 检测调度器。
    /// </summary>
    private readonly HangfireDetectionScheduler _scheduler;

    /// <summary>
    /// 初始化检测任务 API 控制器。
    /// </summary>
    public DetectionTasksApiController(AppDbContext dbContext, HangfireDetectionScheduler scheduler)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
    }

    /// <summary>
    /// 获取检测任务列表（含可选模型、每个任务的最近一次执行 + top10 历史）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        // 1. 可选模型（创建表单用）。
        var availableModels = await _dbContext.ModelLibraryItems
            .OrderBy(m => m.ModelName)
            .Select(m => new { id = m.Id, modelName = m.ModelName, displayName = m.ModelName })
            .ToListAsync(cancellationToken);

        // 2. 任务列表（启用的排前面，再按名称）。
        var tasks = await _dbContext.DetectionTasks
            .OrderByDescending(t => t.IsEnabled)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        var taskIds = tasks.Select(t => t.Id).ToList();
        if (taskIds.Count == 0)
        {
            return Ok(ApiResponse.Ok(new { tasks = Array.Empty<object>(), availableModels }));
        }

        // 3. 执行记录：最近一次 + top10 历史。
        var recentExecutions = await _dbContext.DetectionTaskExecutions
            .Where(e => taskIds.Contains(e.DetectionTaskId))
            .ToListAsync(cancellationToken);
        var latestExecutions = recentExecutions
            .GroupBy(e => e.DetectionTaskId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.StartedAt).First());
        var historyByTask = recentExecutions
            .GroupBy(e => e.DetectionTaskId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.StartedAt).Take(10).ToList());

        // 4. 模型信息 + 孤儿任务解绑（指向已删除模型）。
        var modelIds = tasks.Where(t => t.ModelLibraryItemId.HasValue).Select(t => t.ModelLibraryItemId!.Value).Distinct().ToList();
        var models = modelIds.Count > 0
            ? await _dbContext.ModelLibraryItems.Where(m => modelIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m, cancellationToken)
            : new Dictionary<Guid, Domain.Models.ModelLibraryItem>();

        var orphanTasks = tasks.Where(t => t.ModelLibraryItemId.HasValue && !models.ContainsKey(t.ModelLibraryItemId!.Value)).ToList();
        foreach (var orphan in orphanTasks)
        {
            orphan.ModelLibraryItemId = null;
            await _dbContext.UpdateAsync(orphan, cancellationToken);
        }

        // 5. 投影。
        var taskDtos = tasks.Select(t =>
        {
            latestExecutions.TryGetValue(t.Id, out var latest);
            historyByTask.TryGetValue(t.Id, out var history);
            var modelName = t.ModelLibraryItemId.HasValue && models.TryGetValue(t.ModelLibraryItemId.Value, out var m)
                ? m.ModelName : null;
            return new
            {
                id = t.Id,
                name = t.Name,
                cronExpression = t.CronExpression,
                isEnabled = t.IsEnabled,
                modelLibraryItemId = t.ModelLibraryItemId,
                modelName,
                createdAt = t.CreatedAt,
                lastExecutionSummary = latest?.Summary,
                lastExecutionStatus = latest?.Status,
                lastExecutionStartedAt = latest?.StartedAt,
                lastExecutionFinishedAt = latest?.FinishedAt,
                executionHistory = (history ?? new List<DetectionTaskExecution>()).Select(e => new
                {
                    startedAt = e.StartedAt,
                    finishedAt = e.FinishedAt,
                    status = e.Status,
                    summary = e.Summary
                }).ToList()
            };
        }).ToList();

        return Ok(ApiResponse.Ok(new { tasks = taskDtos, availableModels }));
    }

    /// <summary>
    /// 创建检测任务。创建后立即重新注册所有任务到 Hangfire。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDetectionTaskRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Name) || string.IsNullOrWhiteSpace(request.CronExpression))
        {
            return BadRequest(ApiResponse.Fail("任务名称和 Cron 表达式不能为空", "invalid_input"));
        }

        var task = new DetectionTask
        {
            Name = request.Name.Trim(),
            CronExpression = request.CronExpression.Trim(),
            IsEnabled = true,
            ModelLibraryItemId = request.ModelLibraryItemId.HasValue && request.ModelLibraryItemId.Value != Guid.Empty
                ? request.ModelLibraryItemId
                : null
        };
        _dbContext.DetectionTasks.Add(task);
        await _scheduler.ScheduleAllAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new { id = task.Id }, $"任务 \"{task.Name}\" 创建成功"));
    }

    /// <summary>
    /// 切换任务启用状态。切换后立即重新注册所有任务到 Hangfire。
    /// </summary>
    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.DetectionTasks.InSingleAsync(id);
        if (task is null)
        {
            return NotFound(ApiResponse.Fail("任务不存在", "task_not_found"));
        }

        task.IsEnabled = !task.IsEnabled;
        await _dbContext.UpdateAsync(task, cancellationToken);
        await _scheduler.ScheduleAllAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new { isEnabled = task.IsEnabled }, $"任务已{(task.IsEnabled ? "启用" : "禁用")}"));
    }

    /// <summary>
    /// 立即执行任务（同步等待调度器执行完毕，不走 Hangfire 队列）。
    /// </summary>
    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> Execute(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.DetectionTasks.InSingleAsync(id);
        if (task is null)
        {
            return NotFound(ApiResponse.Fail("任务不存在", "task_not_found"));
        }

        try
        {
            await _scheduler.ExecuteDetectionTaskAsync(id, cancellationToken);
        }
        catch (Exception ex)
        {
            return Ok(ApiResponse.Fail($"执行失败：{ex.Message}", "execute_failed"));
        }

        return Ok(ApiResponse.Ok("任务已触发执行"));
    }

    /// <summary>
    /// 删除检测任务（级联清理执行历史，避免留孤儿记录）。
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.DetectionTasks.InSingleAsync(id);
        if (task is null)
        {
            return NotFound(ApiResponse.Fail("任务不存在", "task_not_found"));
        }

        // 级联清理执行历史（DetectionTaskExecution 无数据库级 FK 级联，需手动删）。
        var executions = await _dbContext.DetectionTaskExecutions
            .Where(x => x.DetectionTaskId == id)
            .ToListAsync(cancellationToken);
        if (executions.Count > 0)
        {
            _dbContext.DetectionTaskExecutions.RemoveRange(executions);
        }
        _dbContext.DetectionTasks.Remove(task);
        await _scheduler.ScheduleAllAsync(cancellationToken);

        return Ok(ApiResponse.Ok("任务已删除"));
    }
}

/// <summary>
/// 创建检测任务请求。
/// </summary>
public sealed class CreateDetectionTaskRequest
{
    /// <summary>
    /// 任务名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// Cron 表达式。
    /// </summary>
    public string CronExpression { get; set; } = string.Empty;
    /// <summary>
    /// 关联模型 Id（null 或空 Guid 表示检测全部模型）。
    /// </summary>
    public Guid? ModelLibraryItemId { get; set; }
}
