using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages.Admin.Conversations;

/// <summary>
/// 对话记录页面模型。
/// 检查系统设置中的 ConversationLogEnabled 标记，未启用时返回 404。
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly AdminQueryMetadataService _adminQueryMetadataService;

    public IndexModel(AdminQueryMetadataService adminQueryMetadataService)
    {
        _adminQueryMetadataService = adminQueryMetadataService;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _adminQueryMetadataService.GetRuntimeSettingsAsync(cancellationToken);
        if (!settings.ConversationLogEnabled)
        {
            return NotFound();
        }

        return Page();
    }
}
