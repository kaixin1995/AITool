using Microsoft.AspNetCore.Builder;

namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 管理后台认证中间件的注册扩展方法。
/// <para>
/// 提供简洁的 UseAdminAuthentication() 注册方式，
/// 代替 Web/Program.cs 中原来的内联 app.Use(async ...) 写法。
/// </para>
/// </summary>
public static class AdminAuthenticationMiddlewareExtensions
{
    /// <summary>
    /// 注册管理后台认证中间件。
    /// <para>
    /// 拦截未认证的后台页面和 API 请求，执行重定向到登录页或返回 401 状态码。
    /// 必须在 UseAuthentication 和 UseAuthorization 之后调用。
    /// </para>
    /// </summary>
    /// <param name="app">应用程序构建器。</param>
    public static IApplicationBuilder UseAdminAuthentication(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AdminAuthenticationMiddleware>();
    }
}
