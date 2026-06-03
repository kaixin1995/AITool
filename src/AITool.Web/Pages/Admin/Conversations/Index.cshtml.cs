using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Conversations;

/// <summary>
/// 对话记录页面模型。
/// </summary>
public class IndexModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;

    public IndexModel(ISystemRuntimeSettingsService systemRuntimeSettingsService)
    {
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var settings = await _systemRuntimeSettingsService.GetOrCreateAsync();
        if (!settings.ConversationLogEnabled)
        {
            return NotFound();
        }

        return Page();
    }
}
