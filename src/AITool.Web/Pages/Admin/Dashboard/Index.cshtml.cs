using AITool.Application.Dashboard;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Dashboard;

// 状态看板页面模型，展示系统整体运行概览
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 看板概览统计数据
    public DashboardOverviewResult Overview { get; set; } = new();

    // 加载看板概览数据
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var enabledSiteCount = await _dbContext.Sites.CountAsync(s => s.IsEnabled, cancellationToken);
        var modelCount = await _dbContext.ModelLibraryItems.CountAsync(cancellationToken);
        var enabledTaskCount = await _dbContext.DetectionTasks.CountAsync(t => t.IsEnabled, cancellationToken);

        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        // 先加载全部再客户端过滤（SQLite 不支持 DateTimeOffset 的 WHERE 比较）
        var allDetections = await _dbContext.DetectionLogs.ToListAsync(cancellationToken);
        var recentDetections = allDetections
            .Where(d => d.CheckedAt >= cutoff)
            .ToList();

        var recentDetectionCount = recentDetections.Count;
        var recentSuccessCount = recentDetections.Count(d => d.Status == "success");
        var recentSuccessRate = recentDetectionCount > 0
            ? (double)recentSuccessCount / recentDetectionCount
            : 0;

        Overview = new DashboardOverviewResult
        {
            EnabledSiteCount = enabledSiteCount,
            ModelCount = modelCount,
            RecentDetectionCount = recentDetectionCount,
            RecentSuccessRate = recentSuccessRate,
            EnabledTaskCount = enabledTaskCount
        };
    }
}
