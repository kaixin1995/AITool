using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages.Admin.Conversations;

/// <summary>
/// 对话记录页面模型。
/// 检查系统设置中的 ConversationLogEnabled 标记，未启用时返回 404。
/// </summary>
public sealed class IndexModel : PageModel
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
