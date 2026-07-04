using AITool.Application.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Codex;

/// <summary>
/// Codex 账号管理页面模型（薄壳）。账号列表与操作由前端 fetch /api/admin/codex/* 动态完成。
/// 受 Codex 功能总开关保护：关闭时重定向到系统设置页。
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _runtimeSettings;

    public IndexModel(ISystemRuntimeSettingsService runtimeSettings)
    {
        _runtimeSettings = runtimeSettings;
    }

    /// <summary>
    /// 是否启用巡检页签与独立巡检页。
    /// </summary>
    public bool InspectionEnabled { get; private set; }

    /// <summary>
    /// 页面加载。若 Codex 功能总开关关闭，重定向到系统设置页（避免直接访问 URL 绕过导航隐藏）。
    /// </summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettings.GetOrCreateAsync(cancellationToken);
        if (!settings.CodexFeaturesEnabled)
        {
            return RedirectToPage("/Admin/System/Settings");
        }

        InspectionEnabled = settings.CodexInspectionEnabled;
        return Page();
    }
}
