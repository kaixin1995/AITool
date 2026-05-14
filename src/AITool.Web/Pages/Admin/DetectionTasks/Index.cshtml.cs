using AITool.Domain.Detection;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.DetectionTasks;

// 检测任务视图模型
public class DetectionTaskViewModel
{
    public Guid TaskId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? ModelName { get; set; }
    public string? LastExecutionSummary { get; set; }
    // 该任务的执行历史记录
    public List<ExecutionHistoryItem> ExecutionHistory { get; set; } = [];
}

// 执行历史条目
public class ExecutionHistoryItem
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Summary { get; set; }
}

// 模型下拉选项（复用轻量结构）
public class TaskModelSelectItem
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

// 检测任务管理页面模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly HangfireDetectionScheduler _scheduler;

    public IndexModel(AppDbContext dbContext, HangfireDetectionScheduler scheduler)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
    }

    // 检测任务列表
    public List<DetectionTaskViewModel> Tasks { get; set; } = [];

    // 模型下拉选项列表
    public List<TaskModelSelectItem> AvailableModels { get; set; } = [];

    // 操作结果提示
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载检测任务列表及执行记录
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // 加载所有模型供创建表单选择
        AvailableModels = await _dbContext.ModelLibraryItems
            .OrderBy(m => m.DisplayName)
            .Select(m => new TaskModelSelectItem
            {
                Id = m.Id,
                DisplayName = m.DisplayName
            })
            .ToListAsync(cancellationToken);

        // 加载全部执行记录，客户端分组
        var allExecutions = await _dbContext.DetectionTaskExecutions.ToListAsync(cancellationToken);
        var latestExecutions = allExecutions
            .GroupBy(e => e.DetectionTaskId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.StartedAt).First());

        // 按任务分组取每个任务最近 10 条执行记录
        var historyByTask = allExecutions
            .GroupBy(e => e.DetectionTaskId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.StartedAt).Take(10).ToList());

        // 加载模型信息用于显示关联模型名称
        var tasks = await _dbContext.DetectionTasks
            .OrderByDescending(t => t.IsEnabled)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        var modelIds = tasks
            .Where(t => t.ModelLibraryItemId.HasValue)
            .Select(t => t.ModelLibraryItemId!.Value)
            .Distinct().ToList();
        var models = modelIds.Any()
            ? await _dbContext.ModelLibraryItems
                .Where(m => modelIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m, cancellationToken)
            : new Dictionary<Guid, Domain.Models.ModelLibraryItem>();
        var orphanTaskIds = tasks
            .Where(t => t.ModelLibraryItemId.HasValue && !models.ContainsKey(t.ModelLibraryItemId.Value))
            .Select(t => t.Id)
            .ToList();
        if (orphanTaskIds.Count > 0)
        {
            // 历史删除模型后的遗留任务绑定在这里自动解绑，避免任务页继续引用无效模型。
            foreach (var task in tasks.Where(t => orphanTaskIds.Contains(t.Id)))
            {
                task.ModelLibraryItemId = null;
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        Tasks = tasks.Select(t =>
        {
            var vm = new DetectionTaskViewModel
            {
                TaskId = t.Id,
                Name = t.Name,
                CronExpression = t.CronExpression,
                IsEnabled = t.IsEnabled
            };

            // 关联模型名称
            if (t.ModelLibraryItemId.HasValue && models.TryGetValue(t.ModelLibraryItemId.Value, out var model))
            {
                vm.ModelName = model.DisplayName;
            }

            // 最近执行摘要
            if (latestExecutions.TryGetValue(t.Id, out var latest))
            {
                vm.LastExecutionSummary = latest.Summary;
            }

            // 执行历史
            if (historyByTask.TryGetValue(t.Id, out var history))
            {
                vm.ExecutionHistory = history.Select(e => new ExecutionHistoryItem
                {
                    StartedAt = e.StartedAt,
                    FinishedAt = e.FinishedAt,
                    Status = e.Status,
                    Summary = e.Summary
                }).ToList();
            }

            return vm;
        }).ToList();
    }

    // 创建新的检测任务
    public async Task<IActionResult> OnPostCreateAsync(string name, string cronExpression, Guid? modelId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cronExpression))
            {
                StatusMessage = "任务名称和 Cron 表达式不能为空";
                StatusSuccess = false;
                await OnGetAsync(cancellationToken);
                return Page();
            }

            var task = new DetectionTask
            {
                Name = name,
                CronExpression = cronExpression,
                IsEnabled = true,
                // 如果 modelId 有值且不为空 Guid，则关联指定模型
                ModelLibraryItemId = (modelId.HasValue && modelId.Value != Guid.Empty) ? modelId : null
            };
            _dbContext.DetectionTasks.Add(task);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // 注册到 Hangfire 调度
            await _scheduler.ScheduleAllAsync(cancellationToken);

            StatusMessage = $"任务 \"{name}\" 创建成功";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 切换任务启用/禁用状态
    public async Task<IActionResult> OnPostToggleAsync(Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            var task = await _dbContext.DetectionTasks.FindAsync([taskId], cancellationToken);
            if (task is null) return RedirectToPage();

            task.IsEnabled = !task.IsEnabled;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _scheduler.ScheduleAllAsync(cancellationToken);
            StatusMessage = "任务状态已切换";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 手动触发任务立即执行
    public async Task<IActionResult> OnPostExecuteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            await _scheduler.ExecuteDetectionTaskAsync(taskId, cancellationToken);
            StatusMessage = "任务已触发执行";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"执行失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }
}
