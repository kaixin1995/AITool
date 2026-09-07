using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;


namespace AITool.Infrastructure.Security;

/// <summary>
/// Admin 侧跨宿主调用注入器：为所有发往 Core 的 HTTP 请求自动附加共享密钥头
/// （<c>CoreServer:SharedSecret</c> 配置非空时）。密钥空值时静默跳过——与 Core 端
/// 「未配置密钥 = 仅回环放行」的退化策略配套，同机部署开箱即用。
/// </summary>
public sealed class CoreAuthHeaderHandler : DelegatingHandler
{
    private readonly string _sharedSecret;

    public CoreAuthHeaderHandler(IConfiguration configuration)
    {
        _sharedSecret = configuration["CoreServer:SharedSecret"] ?? string.Empty;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_sharedSecret))
        {
            request.Headers.TryAddWithoutValidation("X-Core-Auth", _sharedSecret);
        }

        return base.SendAsync(request, cancellationToken);
    }
}