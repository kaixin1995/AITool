using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AITool.Admin;

/// <summary>
/// 测试专用认证 handler：总是返回"已认证"。
/// 仅在 Testing 环境注册，让集成测试无需签发真实 JWT 即可访问受保护 API。
/// 生产环境绝不使用。
/// </summary>
internal sealed class TestingAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public static readonly string AuthenticationScheme = "Testing";

    public TestingAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim(ClaimTypes.Name, "test-admin") };
        var identity = new ClaimsIdentity(claims, AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
