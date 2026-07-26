using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AITool.Admin.Services;

/// <summary>
/// 校验 Codex 功能总开关是否开启；关闭时返回 404，禁止通过直接调用 API 绕过。
/// 用法：在控制器类上加 [ServiceFilter(typeof(CodexFeatureToggleAttribute))]。
/// </summary>
public sealed class CodexFeatureToggleAttribute : ActionFilterAttribute
{
    private readonly ISystemRuntimeSettingsService _runtimeSettings;

    public CodexFeatureToggleAttribute(ISystemRuntimeSettingsService runtimeSettings)
    {
        _runtimeSettings = runtimeSettings;
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var settings = await _runtimeSettings.GetOrCreateAsync(context.HttpContext.RequestAborted);
        if (!settings.CodexFeaturesEnabled)
        {
            context.Result = new NotFoundObjectResult(new { message = "Codex 功能未启用" });
            return;
        }
        await next();
    }
}
