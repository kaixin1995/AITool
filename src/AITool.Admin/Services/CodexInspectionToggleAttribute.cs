using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AITool.Admin.Services;

/// <summary>
/// 校验 Codex 巡检功能是否开启；关闭时返回 404，禁止通过直接调用 API 绕过页面隐藏。
/// 仅用于巡检相关 action，不影响其它 Codex 账号管理接口。
/// </summary>
public sealed class CodexInspectionToggleAttribute : ActionFilterAttribute
{
    private readonly ISystemRuntimeSettingsService _runtimeSettings;

    public CodexInspectionToggleAttribute(ISystemRuntimeSettingsService runtimeSettings)
    {
        _runtimeSettings = runtimeSettings;
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var settings = await _runtimeSettings.GetOrCreateAsync(context.HttpContext.RequestAborted);
        if (!settings.CodexFeaturesEnabled || !settings.CodexInspectionEnabled)
        {
            context.Result = new NotFoundObjectResult(new { message = "Codex 巡检未启用" });
            return;
        }

        await next();
    }
}
