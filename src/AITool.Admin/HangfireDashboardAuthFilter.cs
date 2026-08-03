using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace AITool.Admin;

/// <summary>
/// Hangfire Dashboard 鉴权过滤器。
/// <para>
/// JWT 存在前端 localStorage，浏览器访问 /hangfire 时不会自动携带，无法直接用 JWT 鉴权。
/// 此过滤器采用折中策略：
/// <list type="bullet">
/// <item>本地请求（loopback）或开发/测试环境：放行，保持原有便利性。</item>
/// <item>远程请求且非开发环境：要求 ASP.NET 已认证用户（若配了 Cookie/其他方案则生效，
/// 否则远程访问被拒绝，引导走前端管理界面）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    private readonly IHostEnvironment _environment;

    public HangfireDashboardAuthFilter(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        // 开发/测试环境放行，方便本地调试。
        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
        {
            return true;
        }

        // 本地回环请求放行（与 Hangfire 默认行为一致）。
        var connection = httpContext.Connection;
        if (connection.RemoteIpAddress is not null && connection.LocalIpAddress is not null)
        {
            if (connection.RemoteIpAddress.Equals(connection.LocalIpAddress))
            {
                return true;
            }
        }
        if (connection.RemoteIpAddress is null || System.Net.IPAddress.IsLoopback(connection.RemoteIpAddress))
        {
            return true;
        }

        // 远程请求要求已认证用户（生产环境保护）。
        // 注意：JWT Bearer 默认不处理浏览器页面请求（无 Authorization header），
        // 远程未认证访问会被拒绝，引导走前端管理界面。
        return httpContext.User?.Identity?.IsAuthenticated == true;
    }
}
