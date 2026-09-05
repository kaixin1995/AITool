using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Security;

/// <summary>
/// Core 宿主跨宿主管理端点（/api/core/*）的共享密钥鉴权中间件。
/// <para>
/// 规则（按序）：
/// 1. 非 /api/core 前缀（/v1/*、/health 等）直接放行——代理端点仍由 AccessKey 自校验，本中间件不介入；
/// 2. Testing 环境全放行（沿用 JWT 的 Testing 匿名模式，存量集成测试零改动）；
/// 3. 未配置 <c>CoreAuth:SharedSecret</c> 时退化为「仅本机回环可访问」——同机部署开箱即用，Core
///    独立服务能力（/v1）不受影响，并输出启动告警；
/// 4. 已配置时校验 <c>X-Core-Auth</c> 请求头，SHA256 摘要后 FixedTimeEquals 比较防时序侧信道。
/// </para>
/// </summary>
public sealed class CoreApiAuthMiddleware
{
    private const string AuthHeaderName = "X-Core-Auth";
    private const string ConfigKey = "CoreAuth:SharedSecret";

    private readonly RequestDelegate _next;
    private readonly ILogger<CoreApiAuthMiddleware> _logger;
    private readonly string _sharedSecret;

    public CoreApiAuthMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<CoreApiAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _sharedSecret = configuration[ConfigKey] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_sharedSecret))
        {
            logger.LogWarning(
                "CoreAuth:SharedSecret 未配置：/api/core/* 管理端点仅允许本机回环访问。" +
                "跨机部署（如 Admin 在本地、Core 在服务器）必须先配置共享密钥，否则 Admin 无法下发配置。");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. 仅保护管理端点；代理端点 /v1/*、健康检查 /health 等一律不经过本中间件逻辑。
        if (!context.Request.Path.StartsWithSegments("/api/core"))
        {
            await _next(context);
            return;
        }

        // 2. 测试环境放行（与 JWT 的 Testing 匿名模式一致，避免存量集成测试需要签发真实密钥）。
        var environment = context.RequestServices.GetService(typeof(IHostEnvironment)) as IHostEnvironment;
        if (environment?.IsEnvironment("Testing") == true)
        {
            await _next(context);
            return;
        }

        // 3. 未配置密钥：仅回环地址放行。
        if (string.IsNullOrWhiteSpace(_sharedSecret))
        {
            if (IsLoopback(context.Connection.RemoteIpAddress))
            {
                await _next(context);
                return;
            }

            _logger.LogWarning("跨宿主管理端点被远端访问，但共享密钥未配置，已拒绝。Remote={Remote}", context.Connection.RemoteIpAddress);
            await WriteUnauthorizedAsync(context);
            return;
        }

        // 4. 校验共享密钥头。
        var provided = context.Request.Headers[AuthHeaderName].FirstOrDefault();
        if (!string.IsNullOrEmpty(provided) && SecureEquals(provided, _sharedSecret))
        {
            await _next(context);
            return;
        }

        await WriteUnauthorizedAsync(context);
    }

    /// <summary>
    /// 判断是否属于本机回环：真实网络连接一定有远端地址；
    /// 进程内传输（TestServer/反代直连）的 RemoteIpAddress 为 null，视为本机调用者放行。
    /// </summary>
    private static bool IsLoopback(System.Net.IPAddress? address)
        => address is null || System.Net.IPAddress.IsLoopback(address);

    private static bool SecureEquals(string provided, string expected)
    {
        var a = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = "缺少或无效的跨宿主访问密钥，请在请求头携带 X-Core-Auth",
                type = "authentication_error",
                code = "invalid_core_auth"
            }
        });
    }
}