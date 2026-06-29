using AITool.Application.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.ClientSimulator;

/// <summary>
/// 客户端模拟器中的模型展示项。
/// </summary>
public sealed class ClientSimulatorModelItemViewModel
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 当前模型可命中的路由数量。
    /// </summary>
    public int RouteCount { get; set; }

    /// <summary>
    /// 模型是否支持 OpenAI 协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; }

    /// <summary>
    /// 模型是否支持 Anthropic 协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 当前环境下是否允许通过 OpenAI 协议调用。
    /// </summary>
    public bool CanUseOpenAi { get; set; }

    /// <summary>
    /// 当前环境下是否允许通过 Anthropic 协议调用。
    /// </summary>
    public bool CanUseAnthropic { get; set; }
}

/// <summary>
/// 客户端模拟器页面模型，当前仅作为开发者调试入口的兼容跳转页。
/// </summary>
public sealed class IndexModel : PageModel
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 运行时设置服务，用于判断开发者功能是否开启。
    /// </summary>
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;

    /// <summary>
    /// 初始化客户端模拟器页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext, ISystemRuntimeSettingsService runtimeSettingsService)
    {
        _dbContext = dbContext;
        _runtimeSettingsService = runtimeSettingsService;
    }

    /// <summary>
    /// 默认调用基地址。
    /// </summary>
    public string DefaultBaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// 默认访问密钥。
    /// </summary>
    public string DefaultAccessKey { get; private set; } = string.Empty;

    /// <summary>
    /// 默认 OpenAI 模型名称。
    /// </summary>
    public string DefaultOpenAiModel { get; private set; } = string.Empty;

    /// <summary>
    /// 默认 Anthropic 模型名称。
    /// </summary>
    public string DefaultAnthropicModel { get; private set; } = string.Empty;

    /// <summary>
    /// 页面可展示的模型列表。
    /// </summary>
    public List<ClientSimulatorModelItemViewModel> Models { get; private set; } = [];

    /// <summary>
    /// 访问旧入口时，统一跳转到开发者调用页面中的模拟器区域。
    /// </summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        return Redirect("/Admin/Developer/Invocations#developerSimulatorPane");
    }
}
