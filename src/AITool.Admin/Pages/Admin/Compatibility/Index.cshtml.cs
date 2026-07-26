using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages.Admin.Compatibility;

/// <summary>
/// 兼容规则集管理页面模型，仅负责返回页面入口。数据读写走 CompatibilityProfilesApiController。
/// </summary>
public sealed class IndexModel : PageModel
{
    /// <summary>
    /// 处理页面首次访问。
    /// </summary>
    public void OnGet()
    {
        ViewData["Title"] = "兼容规则集";
    }
}
