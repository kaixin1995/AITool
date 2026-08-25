using System.Diagnostics.CodeAnalysis;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 站点与模型映射的出口网络代理（Egress Proxy）格式校验器。
/// 支持 http, https, socks4, socks4a, socks5 协议并校验有效端口。
/// </summary>
public static class EgressProxyValidator
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "socks4",
        "socks4a",
        "socks5"
    };

    /// <summary>
    /// 校验代理 URL 是否合法。
    /// 为空、null、"None" 或 "direct" 视为合法（即不走代理直连）。
    /// </summary>
    public static bool TryValidate(string? proxyUrl, [NotNullWhen(false)] out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            errorMessage = null;
            return true;
        }

        var trimmed = proxyUrl.Trim();
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "direct", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = null;
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            errorMessage = "出口网络代理地址不是合法的绝对 URL，例如：http://127.0.0.1:7890 或 socks5://127.0.0.1:10808";
            return false;
        }

        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            errorMessage = $"不支持的代理协议 '{uri.Scheme}'。仅支持 http://, https://, socks5://, socks4://, socks4a://";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            errorMessage = "出口网络代理地址缺少有效的主机名或 IP 地址";
            return false;
        }

        if (uri.Port <= 0 || uri.Port > 65535)
        {
            errorMessage = "出口网络代理端口号无效（必须在 1 到 65535 之间）";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
