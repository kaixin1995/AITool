using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AITool.Admin.Services;

/// <summary>
/// 校验 OAuth 账号功能总开关是否开启；关闭时返回 404，避免通过直接调用 API 绕过页面开关。
/// </summary>
public sealed class OAuthFeatureToggleAttribute : ActionFilterAttribute
{
    private readonly ISystemRuntimeSettingsService _runtimeSettings;

    public OAuthFeatureToggleAttribute(ISystemRuntimeSettingsService runtimeSettings)
    {
        _runtimeSettings = runtimeSettings;
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var settings = await _runtimeSettings.GetOrCreateAsync(context.HttpContext.RequestAborted);
        if (!settings.OAuthFeaturesEnabled)
        {
            context.Result = new NotFoundObjectResult(new { message = "OAuth 账号功能未启用" });
            return;
        }

        await next();
    }
}
