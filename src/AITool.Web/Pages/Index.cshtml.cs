using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages;

/// <summary>
/// 首页仪表盘页面模型，用于展示系统的概览统计信息。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 数据库上下文，用于读取首页统计数据。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化首页页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 当前启用的站点数量。
    /// </summary>
    public int EnabledSiteCount { get; set; }

    /// <summary>
    /// 已录入的模型总数。
    /// </summary>
    public int ModelCount { get; set; }

    /// <summary>
    /// 当前配置的路由规则数量。
    /// </summary>
    public int RouteRuleCount { get; set; }

    /// <summary>
    /// 处于启用状态的访问密钥数量。
    /// </summary>
    public int EnabledKeyCount { get; set; }

    /// <summary>
    /// 当前启用的检测任务数量。
    /// </summary>
    public int EnabledTaskCount { get; set; }

    /// <summary>
    /// 加载首页概览统计数据。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        EnabledSiteCount = await _dbContext.Sites.CountAsync(s => s.IsEnabled, cancellationToken);
        ModelCount = await _dbContext.ModelLibraryItems.CountAsync(cancellationToken);
        RouteRuleCount = await _dbContext.ProxyRouteRules.CountAsync(cancellationToken);
        EnabledKeyCount = await _dbContext.ProxyAccessKeys.CountAsync(k => k.IsEnabled, cancellationToken);
        EnabledTaskCount = await _dbContext.DetectionTasks.CountAsync(t => t.IsEnabled, cancellationToken);
    }
}
