namespace AITool.Application.Google;

/// <summary>
/// Google 账号接入方式与上游端点常量。
/// Antigravity 走 daily-cloudcode-pa.googleapis.com 的 v1internal 接口（Antigravity CLI 客户端身份）。
/// 历史上曾提供 GeminiCli（cloudcode-pa.googleapis.com）接入，已下线；存量账号经 Normalize 归一化为 Antigravity。
/// </summary>
public static class GoogleAccountKinds
{
    /// <summary>Antigravity CLI 客户端接入（https://daily-cloudcode-pa.googleapis.com）。</summary>
    public const string Antigravity = "Antigravity";

    /// <summary>隐藏 Site 的 ManagedSource 标识（站点管理页据此过滤托管 Site）。</summary>
    public const string ManagedSource = "Google";

    private const string AntigravityBaseUrl = "https://daily-cloudcode-pa.googleapis.com";
    private const string AntigravityClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    private const string AntigravityClientSecret = "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";

    /// <summary>Google OAuth 授权端点。</summary>
    public const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/auth";

    /// <summary>Google OAuth 令牌端点。</summary>
    public const string TokenUrl = "https://oauth2.googleapis.com/token";

    /// <summary>userinfo 端点，登录后获取账号邮箱。</summary>
    public const string UserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";

    /// <summary>资源管理器项目列表端点。</summary>
    public const string ProjectsUrl = "https://cloudresourcemanager.googleapis.com/v1/projects";

    /// <summary>固定回跳地址：Google 桌面客户端允许任意本地回环端口，浏览器会重定向到该地址
    /// （即使本机没有服务监听、页面显示无法访问，地址栏也携带 code/state，用户复制粘贴回完成登录）。</summary>
    public const string RedirectUri = "http://localhost:17891";

    /// <summary>判断接入方式是否为合法值（GeminiCli 已下线，仅剩 Antigravity）。</summary>
    public static bool IsValid(string? accountKind)
    {
        return string.Equals(accountKind, Antigravity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>接入方式归一化（历史 GeminiCli 与未知值一并归一化为 Antigravity）。</summary>
    public static string Normalize(string? accountKind)
    {
        return Antigravity;
    }

    /// <summary>上游根地址（仅 Antigravity 一种接入方式）。</summary>
    public static string GetBaseUrl(string? accountKind = null)
    {
        return AntigravityBaseUrl;
    }

    /// <summary>OAuth client_id（仅 Antigravity 一种接入方式）。</summary>
    public static string GetClientId(string? accountKind = null)
    {
        return AntigravityClientId;
    }

    /// <summary>OAuth client_secret（仅 Antigravity 一种接入方式）。</summary>
    public static string GetClientSecret(string? accountKind = null)
    {
        return AntigravityClientSecret;
    }

    /// <summary>OAuth scope 列表（空格拼接后放入授权 URL）。</summary>
    public static IReadOnlyList<string> GetScopes(string? accountKind = null)
    {
        return
        [
            "https://www.googleapis.com/auth/cloud-platform",
            "https://www.googleapis.com/auth/userinfo.email",
            "https://www.googleapis.com/auth/userinfo.profile",
            "https://www.googleapis.com/auth/cclog",
            "https://www.googleapis.com/auth/experimentsandconfigs"
        ];
    }

    /// <summary>Antigravity CLI 客户端 User-Agent（对齐 agy 1.1.20 真实官方抓包特征）。</summary>
    public const string AntigravityUserAgent = "antigravity/cli/1.1.20 (aidev_client; os_type=windows; arch=amd64; cl=970154694; auth_method=consumer)";

    /// <summary>客户端 User-Agent（仅 Antigravity 一种接入方式）。</summary>
    public static string GetUserAgent(string? accountKind = null)
    {
        return AntigravityUserAgent;
    }
}
