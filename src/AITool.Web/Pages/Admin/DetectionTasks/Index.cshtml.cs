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
    public string? LastExecutionSummary { get; set; }
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

    // 操作结果提示
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载检测任务列表及最近执行记录
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // 先加载全部执行记录，客户端分组取最新（SQLite 不支持 DateTimeOffset 的 ORDER BY）
        var allExecutions = await _dbContext.DetectionTaskExecutions.ToListAsync(cancellationToken);
        var latestExecutions = allExecutions
            .GroupBy(e => e.DetectionTaskId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.StartedAt).First());

        Tasks = await _dbContext.DetectionTasks
            .OrderByDescending(t => t.IsEnabled)
            .ThenBy(t => t.Name)
            .Select(t => new DetectionTaskViewModel
            {
                TaskId = t.Id,
                Name = t.Name,
                CronExpression = t.CronExpression,
                IsEnabled = t.IsEnabled
            })
            .ToListAsync(cancellationToken);

        foreach (var task in Tasks)
        {
            if (latestExecutions.TryGetValue(task.TaskId, out var execution))
            {
                task.LastExecutionSummary = execution.Summary;
            }
        }
    }

    // 创建新的检测任务
    public async Task<IActionResult> OnPostCreateAsync(string name, string cronExpression, CancellationToken cancellationToken)
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
            IsEnabled = true
        };
        _dbContext.DetectionTasks.Add(task);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 注册到 Hangfire 调度
        await _scheduler.ScheduleAllAsync(cancellationToken);

        StatusMessage = $"任务 \"{name}\" 创建成功";
        StatusSuccess = true;
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 切换任务启用/禁用状态
    public async Task<IActionResult> OnPostToggleAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _dbContext.DetectionTasks.FindAsync([taskId], cancellationToken);
        if (task is null) return RedirectToPage();

        task.IsEnabled = !task.IsEnabled;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _scheduler.ScheduleAllAsync(cancellationToken);

        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 手动触发任务立即执行
    public async Task<IActionResult> OnPostExecuteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await _scheduler.ExecuteDetectionTaskAsync(taskId, cancellationToken);

        StatusMessage = "任务已触发执行";
        StatusSuccess = true;
        await OnGetAsync(cancellationToken);
        return Page();
    }
}
