using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 管理后台认证中间件，拦截未认证的后台请求并重定向到登录页或返回 401。
/// <para>
/// Web 宿主同时承担代理和管理后台职责，需要通过路径区分请求类型：
/// 代理请求直接放行，后台页面请求未登录时重定向到 /Login，
/// 后台 API 请求未登录时返回 401 状态码。
/// </para>
/// <para>
/// 此中间件从 Web/Program.cs 中提取，使认证逻辑独立可测试，
/// 并为 Admin 宿主复用提供基础。
/// </para>
/// </summary>
public class AdminAuthenticationMiddleware
{
    /// <summary>
    /// 下一个中间件委托。
    /// </summary>
    private readonly RequestDelegate _next;

    /// <summary>
    /// 初始化管理后台认证中间件。
    /// </summary>
    /// <param name="next">管道中的下一个中间件。</param>
    public AdminAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// 处理请求：判断是否为后台请求，未认证时执行重定向或返回 401。
    /// <para>
    /// 处理流程：
    /// <list type="number">
    ///     <item>测试环境、非后台请求、登录页请求直接放行</item>
    ///     <item>已认证用户直接放行</item>
    ///     <item>后台 API 请求返回 401</item>
    ///     <item>Hangfire 仪表盘请求重定向到登录页</item>
    ///     <item>已配置密码的请求重定向到登录页</item>
    ///     <item>其余请求也重定向到登录页</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="context">当前 HTTP 请求上下文。</param>
    /// <param name="environment">宿主环境信息，用于判断是否为测试环境。</param>
    /// <param name="authService">后台认证服务，用于检查是否已配置密码。</param>
    public async Task InvokeAsync(HttpContext context, IHostEnvironment environment, AdminAuthService authService)
    {
        // 测试环境直接放行，不拦截任何请求。
        // 非后台请求（代理请求等）直接放行。
        // 登录页请求直接放行，避免无限重定向。
        if (environment.IsEnvironment("Testing")
            || !AdminRequestMatcher.IsAdminRequest(context.Request)
            || AdminRequestMatcher.IsLoginPageRequest(context.Request))
        {
            await _next(context);
            return;
        }

        // 已认证用户直接放行。
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // 构建登录页重定向地址，携带当前请求路径作为返回地址。
        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        var loginUrl = string.IsNullOrWhiteSpace(returnUrl)
            ? "/Login"
            : $"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";

        // 后台 API 请求未登录时返回 401，前端 JavaScript 可据此弹出登录提示。
        if (AdminRequestMatcher.IsAdminApiRequest(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Hangfire 仪表盘请求重定向到登录页。
        if (AdminRequestMatcher.IsHangfireRequest(context.Request))
        {
            context.Response.Redirect(loginUrl);
            return;
        }

        // 已配置密码时重定向到登录页，未配置密码也重定向（引导设置密码）。
        context.Response.Redirect(loginUrl);
    }
}
