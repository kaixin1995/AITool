using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Codex;

/// <summary>
/// Codex 账号管理页面模型（薄壳）。账号列表与操作由前端 fetch /api/admin/codex/* 动态完成。
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    /// <summary>
    /// 页面加载。无服务端数据，全部前端动态拉取。
    /// </summary>
    public void OnGet()
    {
    }
}
