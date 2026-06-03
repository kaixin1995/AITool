using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Chat;

/// <summary>
/// 对话测试页面模型，仅负责返回页面本身，具体数据由接口提供。
/// </summary>
public class IndexModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;

    public IndexModel(ISystemRuntimeSettingsService systemRuntimeSettingsService)
    {
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
    }

    /// <summary>
    /// 是否启用对话记录页签。
    /// </summary>
    public bool ConversationLogEnabled { get; private set; }

    /// <summary>
    /// 处理页面首次访问。
    /// </summary>
    public async Task OnGetAsync()
    {
        var settings = await _systemRuntimeSettingsService.GetOrCreateAsync();
        ConversationLogEnabled = settings.ConversationLogEnabled;
    }
}
