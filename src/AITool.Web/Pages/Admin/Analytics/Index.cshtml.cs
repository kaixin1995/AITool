using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Analytics;

/// <summary>
/// 可视化分析页面模型，页面主体数据由前端通过接口按需加载。
/// </summary>
public sealed class IndexModel : PageModel
{
    /// <summary>
    /// 处理页面首次访问，仅返回页面骨架。
    /// </summary>
    public void OnGet()
    {
    }
}
