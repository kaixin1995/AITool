using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages;

/// <summary>
/// Core 宿主首页页面模型，展示代理运行状态概览。
/// 管理后台的仪表盘已迁移至 Admin 宿主。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 当前活跃的路由规则数量。从运行时缓存中读取。
    /// </summary>
    public int ActiveRouteCount { get; set; }

    /// <summary>
    /// 当前启用的站点数量。从运行时缓存中读取。
    /// </summary>
    public int ActiveSiteCount { get; set; }

    /// <summary>
    /// 加载代理运行状态概览。
    /// 当前阶段从运行时缓存读取统计信息。
    /// </summary>
    public void OnGet()
    {
        // 后续从 ProxyRequestMetadataCache 读取运行时统计
        ActiveRouteCount = 0;
        ActiveSiteCount = 0;
    }
}
