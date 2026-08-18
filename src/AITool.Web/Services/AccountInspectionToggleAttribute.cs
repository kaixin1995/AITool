using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AITool.Web.Services;

/// <summary>
/// 校验通用账号额度巡检开关；关闭时返回 404，避免通过直接调用 API 绕过页面开关。
/// </summary>
public sealed class AccountInspectionToggleAttribute : ActionFilterAttribute
{
    private readonly ISystemRuntimeSettingsService _runtimeSettings;

    public AccountInspectionToggleAttribute(ISystemRuntimeSettingsService runtimeSettings)
    {
        _runtimeSettings = runtimeSettings;
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var settings = await _runtimeSettings.GetOrCreateAsync(context.HttpContext.RequestAborted);
        if (!settings.OAuthFeaturesEnabled || !settings.OAuthInspectionEnabled)
        {
            context.Result = new NotFoundObjectResult(new { message = "账号额度巡检未启用" });
            return;
        }

        await next();
    }
}
