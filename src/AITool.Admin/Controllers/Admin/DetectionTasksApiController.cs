using AITool.Domain.Detection;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Scheduling;
using AITool.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 检测任务 API：秒级定时检测任务的管理与执行。
/// <para>
/// 迁移自 <c>Pages/Admin/DetectionTasks/Index.cshtml.cs</c>。
/// 调度由 <see cref="DetectionTaskSchedulerService"/>（BackgroundService 轮询 + 随机抖动）承担，
/// 配置写入数据库后下一 tick 自动生效，无需手动重注册；立即执行直接调用调度器（同步等待）。
/// 加载列表时会顺手解绑「指向已删除模型/映射」的孤儿任务，并把遗留 Cron 任务迁移为秒级间隔。
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
    /// 检测任务调度服务（秒级轮询 + 抖动）。
    /// </summary>
    private readonly DetectionTaskSchedulerService _scheduler;

    /// <summary>
    /// 初始化检测任务 API 控制器。
    /// </summary>
    public DetectionTasksApiController(AppDbContext dbContext, DetectionTaskSchedulerService scheduler)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
    }

    /// <summary>
    /// 获取检测任务列表（含可选站点模型目标、每个任务的最近一次执行 + top10 历史）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        // 0. 遗留 Cron 任务迁移为秒级间隔（幂等）。
        await _scheduler.MigrateLegacyCronTasksAsync(cancellationToken);

        // 1. 可选站点模型目标（创建表单用）：数据源与聊天页一致（站点×模型映射，仅启用项）。
        // SqlSugar 不支持查询语法多表 join，先各自读出再内存连接（管理页低频查询，性能可接受）。
        var targetMappings = await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled)
            .ToListAsync(cancellationToken);
        var targetSiteIds = targetMappings.Select(m => m.SiteId).Distinct().ToList();
        var targetSites = await _dbContext.Sites
            .Where(s => s.IsEnabled && targetSiteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);
        var targetModelIds = targetMappings.Select(m => m.ModelLibraryItemId).Distinct().ToList();
        var targetModels = await _dbContext.ModelLibraryItems
            .Where(m => m.IsEnabled && targetModelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);
        var availableTargets = targetMappings
            .Where(m => targetSites.ContainsKey(m.SiteId) && targetModels.ContainsKey(m.ModelLibraryItemId))
            .Select(m => new
            {
                mappingId = m.Id,
                siteId = m.SiteId,
                siteName = targetSites[m.SiteId].Name,
                remoteModelName = m.RemoteModelName,
                modelLibraryItemId = m.ModelLibraryItemId,
                modelName = targetModels[m.ModelLibraryItemId].ModelName
            })
            .OrderBy(t => t.siteName)
            .ThenBy(t => t.remoteModelName)
            .ToList();
        var targetLookup = availableTargets.ToDictionary(t => t.mappingId, t => t);

        // 2. 任务列表（启用的排前面，再按名称）。
        var tasks = await _dbContext.DetectionTasks
            .OrderByDescending(t => t.IsEnabled)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        var taskIds = tasks.Select(t => t.Id).ToList();
        if (taskIds.Count == 0)
        {
            return Ok(ApiResponse.Ok(new { tasks = Array.Empty<object>(), availableTargets }));
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

        // 4. 目标信息 + 孤儿任务解绑（指向已删除模型/映射）。
        var modelIds = tasks.Where(t => t.ModelLibraryItemId.HasValue).Select(t => t.ModelLibraryItemId!.Value).Distinct().ToList();
        var models = modelIds.Count > 0
            ? await _dbContext.ModelLibraryItems.Where(m => modelIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m, cancellationToken)
            : new Dictionary<Guid, Domain.Models.ModelLibraryItem>();

        var orphanTasks = tasks.Where(t =>
            (t.ModelLibraryItemId.HasValue && !models.ContainsKey(t.ModelLibraryItemId!.Value)) ||
            (t.SiteModelMappingId.HasValue && !targetLookup.ContainsKey(t.SiteModelMappingId!.Value))).ToList();
        foreach (var orphan in orphanTasks)
        {
            if (orphan.SiteModelMappingId.HasValue && !targetLookup.ContainsKey(orphan.SiteModelMappingId!.Value))
            {
                orphan.SiteModelMappingId = null;
            }
            if (orphan.ModelLibraryItemId.HasValue && !models.ContainsKey(orphan.ModelLibraryItemId!.Value))
            {
                orphan.ModelLibraryItemId = null;
            }
            await _dbContext.UpdateAsync(orphan, cancellationToken);
        }

        // 5. 投影。
        var taskDtos = tasks.Select(t =>
        {
            latestExecutions.TryGetValue(t.Id, out var latest);
            historyByTask.TryGetValue(t.Id, out var history);
            var modelName = t.ModelLibraryItemId.HasValue && models.TryGetValue(t.ModelLibraryItemId.Value, out var m)
                ? m.ModelName : null;

            // 目标展示：绑定映射 → 「站点 / 远端模型名」；绑定模型 → 模型名；全部 → null。
            string? siteName = null;
            string? remoteModelName = null;
            if (t.SiteModelMappingId.HasValue && targetLookup.TryGetValue(t.SiteModelMappingId.Value, out var target))
            {
                siteName = target.siteName;
                remoteModelName = target.remoteModelName;
            }

            return new
            {
                id = t.Id,
                name = t.Name,
                intervalSeconds = t.IntervalSeconds > 0 ? t.IntervalSeconds : 60,
                isEnabled = t.IsEnabled,
                siteModelMappingId = t.SiteModelMappingId,
                siteName,
                remoteModelName,
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

        return Ok(ApiResponse.Ok(new { tasks = taskDtos, availableTargets }));
    }

    /// <summary>
    /// 创建检测任务。配置入库后由调度服务下一 tick 自动接管，无需手动重注册。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDetectionTaskRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(ApiResponse.Fail("任务名称不能为空", "invalid_input"));
        }

        // 间隔钳制：最小 10 秒，上限 24 小时。
        var intervalSeconds = Math.Clamp(request.IntervalSeconds <= 0 ? 60 : request.IntervalSeconds, 10, 86400);

        Guid? siteModelMappingId = request.SiteModelMappingId.HasValue && request.SiteModelMappingId.Value != Guid.Empty
            ? request.SiteModelMappingId.Value
            : null;
        if (siteModelMappingId.HasValue)
        {
            var mappingExists = await _dbContext.SiteModelMappings.AnyAsync(m => m.Id == siteModelMappingId!.Value, cancellationToken);
            if (!mappingExists)
            {
                return BadRequest(ApiResponse.Fail("绑定的站点模型映射不存在", "mapping_not_found"));
            }
        }

        var task = new DetectionTask
        {
            Name = request.Name.Trim(),
            IntervalSeconds = intervalSeconds,
            // 遗留字段不再参与调度，写入可读标记便于排查历史数据。
            CronExpression = $"interval:{intervalSeconds}s",
            IsEnabled = true,
            SiteModelMappingId = siteModelMappingId,
            ModelLibraryItemId = request.ModelLibraryItemId.HasValue && request.ModelLibraryItemId.Value != Guid.Empty
                ? request.ModelLibraryItemId
                : null
        };
        await _dbContext.InsertAsync(task, cancellationToken);

        return Ok(ApiResponse.Ok(new { id = task.Id }, $"任务 \"{task.Name}\" 创建成功，每 {intervalSeconds} 秒执行一次"));
    }

    /// <summary>
    /// 切换任务启用状态。调度服务下一 tick 自动生效。
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

        return Ok(ApiResponse.Ok(new { isEnabled = task.IsEnabled }, $"任务已{(task.IsEnabled ? "启用" : "禁用")}"));
    }

    /// <summary>
    /// 立即执行任务（同步等待执行完毕，不走调度队列）。
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
    /// 执行间隔（秒），最小 10。调度时自动附加随机抖动。
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>
    /// 绑定的站点模型映射 Id（null 或空 Guid 表示检测全部站点模型映射）。
    /// </summary>
    public Guid? SiteModelMappingId { get; set; }
    /// <summary>
    /// 关联模型 Id（遗留字段；未绑定映射时可指定按模型检测全部映射）。
    /// </summary>
    public Guid? ModelLibraryItemId { get; set; }
}
