using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages;

// 首页仪表盘模型，展示系统概览统计
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 启用站点数
    public int EnabledSiteCount { get; set; }
    // 模型总数
    public int ModelCount { get; set; }
    // 路由规则数
    public int RouteRuleCount { get; set; }
    // 启用访问密钥数
    public int EnabledKeyCount { get; set; }
    // 启用检测任务数
    public int EnabledTaskCount { get; set; }
    // 映射总数
    public int MappingCount { get; set; }

    // 加载概览统计数据
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        EnabledSiteCount = await _dbContext.Sites.CountAsync(s => s.IsEnabled, cancellationToken);
        ModelCount = await _dbContext.ModelLibraryItems.CountAsync(cancellationToken);
        RouteRuleCount = await _dbContext.ProxyRouteRules.CountAsync(cancellationToken);
        EnabledKeyCount = await _dbContext.ProxyAccessKeys.CountAsync(k => k.IsEnabled, cancellationToken);
        EnabledTaskCount = await _dbContext.DetectionTasks.CountAsync(t => t.IsEnabled, cancellationToken);
        MappingCount = await _dbContext.SiteModelMappings.CountAsync(cancellationToken);
    }
}
