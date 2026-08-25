namespace AITool.Application.Google;

/// <summary>
/// Google 账号接入方式与上游端点常量。取值对齐 gcli2api 项目（src/utils.py / config.py）：
/// GeminiCLI 走 cloudcode-pa.googleapis.com 的 v1internal 接口（Gemini CLI 客户端身份），
/// Antigravity 走 daily-cloudcode-pa.googleapis.com 的 v1internal 接口（Antigravity CLI 客户端身份）。
/// </summary>
public static class GoogleAccountKinds
{
    /// <summary>Gemini CLI 客户端接入（https://cloudcode-pa.googleapis.com）。</summary>
    public const string GeminiCli = "GeminiCli";

    /// <summary>Antigravity CLI 客户端接入（https://daily-cloudcode-pa.googleapis.com）。</summary>
    public const string Antigravity = "Antigravity";

    /// <summary>隐藏 Site 的 ManagedSource 标识（站点管理页据此过滤托管 Site）。</summary>
    public const string ManagedSource = "Google";

    private const string GeminiCliBaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string AntigravityBaseUrl = "https://daily-cloudcode-pa.googleapis.com";

    private const string GeminiCliClientId = "681255809395-oo8ft2oprdrnp9e3aqf6av3hmdib135j.apps.googleusercontent.com";
    private const string GeminiCliClientSecret = "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";

    private const string AntigravityClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    private const string AntigravityClientSecret = "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";

    /// <summary>Google OAuth 授权端点（两种接入方式相同）。</summary>
    public const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/auth";

    /// <summary>Google OAuth 令牌端点（两种接入方式相同）。</summary>
    public const string TokenUrl = "https://oauth2.googleapis.com/token";

    /// <summary>userinfo 端点，登录后获取账号邮箱。</summary>
    public const string UserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";

    /// <summary>资源管理器项目列表端点，GeminiCli 登录时自动选择 cloud-platform 项目。</summary>
    public const string ProjectsUrl = "https://cloudresourcemanager.googleapis.com/v1/projects";

    /// <summary>固定回跳地址：Google 桌面客户端允许任意本地回环端口，浏览器会重定向到该地址
    /// （即使本机没有服务监听、页面显示无法访问，地址栏也携带 code/state，用户复制粘贴回完成登录）。</summary>
    public const string RedirectUri = "http://localhost:17891";

    /// <summary>判断接入方式是否为合法值。</summary>
    public static bool IsValid(string? accountKind)
    {
        return string.Equals(accountKind, GeminiCli, StringComparison.OrdinalIgnoreCase)
            || string.Equals(accountKind, Antigravity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>接入方式归一化（非法值按 GeminiCli 处理）。</summary>
    public static string Normalize(string? accountKind)
    {
        return string.Equals(accountKind, Antigravity, StringComparison.OrdinalIgnoreCase) ? Antigravity : GeminiCli;
    }

    /// <summary>获取接入方式对应的上游根地址。</summary>
    public static string GetBaseUrl(string accountKind)
    {
        return string.Equals(Normalize(accountKind), Antigravity, StringComparison.OrdinalIgnoreCase)
            ? AntigravityBaseUrl
            : GeminiCliBaseUrl;
    }

    /// <summary>获取接入方式对应的 OAuth client_id。</summary>
    public static string GetClientId(string accountKind)
    {
        return string.Equals(Normalize(accountKind), Antigravity, StringComparison.OrdinalIgnoreCase)
            ? AntigravityClientId
            : GeminiCliClientId;
    }

    /// <summary>获取接入方式对应的 OAuth client_secret。</summary>
    public static string GetClientSecret(string accountKind)
    {
        return string.Equals(Normalize(accountKind), Antigravity, StringComparison.OrdinalIgnoreCase)
            ? AntigravityClientSecret
            : GeminiCliClientSecret;
    }

    /// <summary>获取接入方式对应的 OAuth scope 列表（空格拼接后放入授权 URL）。</summary>
    public static IReadOnlyList<string> GetScopes(string accountKind)
    {
        return string.Equals(Normalize(accountKind), Antigravity, StringComparison.OrdinalIgnoreCase)
            ?
            [
                "https://www.googleapis.com/auth/cloud-platform",
                "https://www.googleapis.com/auth/userinfo.email",
                "https://www.googleapis.com/auth/userinfo.profile",
                "https://www.googleapis.com/auth/cclog",
                "https://www.googleapis.com/auth/experimentsandconfigs"
            ]
            :
            [
                "https://www.googleapis.com/auth/cloud-platform",
                "https://www.googleapis.com/auth/userinfo.email",
                "https://www.googleapis.com/auth/userinfo.profile"
            ];
    }

    /// <summary>GeminiCLI 客户端 User-Agent（含模型名占位，发送前替换）。</summary>
    public const string GeminiCliUserAgentTemplate = "GeminiCLI/0.35.2/{MODEL} (win32; x64; cloud-shell)";

    /// <summary>Antigravity CLI 客户端 User-Agent（对齐 agy 1.1.20 真实官方抓包特征）。</summary>
    public const string AntigravityUserAgent = "antigravity/cli/1.1.20 (aidev_client; os_type=windows; arch=amd64; cl=970154694; auth_method=consumer)";

    /// <summary>
    /// GeminiCLI 静态模型清单（对齐 gcli2api src/utils.py BASE_MODELS，去掉假流式/抗截断前缀变体）。
    /// 供给器与模型拉取器共用，避免两份清单漂移。
    /// </summary>
    public static readonly string[] GeminiCliModels =
    [
        "gemini-2.5-pro",
        "gemini-2.5-flash",
        "gemini-3-flash-preview",
        "gemini-3.1-pro-preview",
        "gemini-3.1-flash-lite",
        "gemini-3.5-flash"
    ];
}
