using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Codex;

/// <summary>
/// Codex 巡检页面（独立页面，可嵌入 iframe）。
/// 当巡检开关关闭时直接返回 404，避免通过直连 URL 绕过主页面页签隐藏。
/// </summary>
public class InspectionModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;

    public InspectionModel(ISystemRuntimeSettingsService runtimeSettingsService)
    {
        _runtimeSettingsService = runtimeSettingsService;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.CodexFeaturesEnabled || !settings.CodexInspectionEnabled)
        {
            return NotFound();
        }

        return Page();
    }
}
