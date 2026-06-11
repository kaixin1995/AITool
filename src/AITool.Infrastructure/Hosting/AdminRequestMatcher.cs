using Microsoft.AspNetCore.Http;

namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 提供管理后台请求路径匹配能力，统一 Web 宿主和 Admin 宿主的路径判断逻辑。
/// <para>
/// Web 宿主同时承担代理和管理后台职责，需要通过路径区分请求类型；
/// Admin 宿主全部是后台请求，仅用于 Cookie 认证重定向判断。
/// </para>
/// </summary>
public static class AdminRequestMatcher
{
    /// <summary>
    /// 判断请求是否为管理后台相关请求（页面、API 或 Hangfire 仪表盘）。
    /// </summary>
    public static bool IsAdminRequest(HttpRequest request)
    {
        return IsAdminPageRequest(request) || IsAdminApiRequest(request) || IsHangfireRequest(request);
    }

    /// <summary>
    /// 判断请求是否为管理后台页面请求（首页或 /Admin 路径下）。
    /// </summary>
    public static bool IsAdminPageRequest(HttpRequest request)
    {
        var path = request.Path;
        return path == "/" || path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断请求是否为登录页请求。
    /// </summary>
    public static bool IsLoginPageRequest(HttpRequest request)
    {
        return request.Path == "/Login";
    }

    /// <summary>
    /// 判断请求是否为管理后台 API 请求（/api/admin 路径下）。
    /// </summary>
    public static bool IsAdminApiRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断请求是否为 Hangfire 仪表盘请求。
    /// </summary>
    public static bool IsHangfireRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/hangfire", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断请求是否为管理后台认证相关请求（后台页面或登录页）。
    /// <para>
    /// 此方法用于 Cookie 认证重定向判断：仅后台页面请求需要重定向到登录页，
    /// API 请求返回 401 状态码，代理请求不做任何处理。
    /// </para>
    /// </summary>
    public static bool IsAdminAuthRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/Login", StringComparison.OrdinalIgnoreCase);
    }
}
