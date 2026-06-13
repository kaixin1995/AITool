using AITool.Application.Operations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages.Admin.Chat;

/// <summary>
/// 对话测试页面模型，仅负责返回页面本身，具体数据由接口提供。
/// <para>
/// 从 AITool.Web.Pages.Admin.Chat 迁移而来。页面仅依赖
/// <see cref="ISystemRuntimeSettingsService"/>，Admin DI 已注册该服务。
/// 页面 JavaScript 调用的 <c>/api/admin/chat/*</c> 端点仍由 Core 宿主上的
/// ChatApiController 提供（该控制器依赖代理转发等运行时组件，暂不迁移）。
/// </para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;
    private readonly AdminQueryMetadataService _adminQueryMetadataService;
    private readonly CoreAdminClient _coreClient;

    public IndexModel(
        ISystemRuntimeSettingsService systemRuntimeSettingsService,
        AdminQueryMetadataService adminQueryMetadataService,
        CoreAdminClient coreClient)
    {
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
        _adminQueryMetadataService = adminQueryMetadataService;
        _coreClient = coreClient;
    }

    /// <summary>
    /// 是否启用对话记录页签。
    /// </summary>
    public bool ConversationLogEnabled { get; private set; }

    /// <summary>
    /// Core 宿主基址，供页面 JavaScript 调用 Core 上的 Chat API。
    /// </summary>
    public string CoreBaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// 处理页面首次访问，读取对话记录开关状态。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _adminQueryMetadataService.GetRuntimeSettingsAsync(cancellationToken);
        ConversationLogEnabled = settings.ConversationLogEnabled;
        CoreBaseUrl = GetCoreBaseUrl();
    }

    private string GetCoreBaseUrl()
    {
        var baseAddress = _coreClient.BaseAddress;
        if (baseAddress is not null)
        {
            return baseAddress.ToString().TrimEnd('/').Replace("://0.0.0.0:", "://127.0.0.1:");
        }

        return "http://127.0.0.1:5029";
    }
}
