using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Chat;

/// <summary>
/// 对话测试页面模型，仅负责返回页面本身，具体数据由接口提供。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 处理页面首次访问。
    /// </summary>
    public void OnGet() { }
}
