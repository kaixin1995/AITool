namespace AITool.Application.Xai;

/// <summary>
/// xAI (Grok) OAuth 常量定义（与 reference-projects/cc-switch 的 xai_oauth_auth.rs 对齐，
/// client_id 与 Grok CLI 完全一致，token 对 grok.com 账单端点等效）。
/// </summary>
public static class XaiConstants
{
    /// <summary>xAI OIDC Issuer。</summary>
    public const string Issuer = "https://auth.x.ai";

    /// <summary>OIDC Discovery 端点（解析 device_authorization / token 端点）。</summary>
    public const string DiscoveryUrl = "https://auth.x.ai/.well-known/openid-configuration";

    /// <summary>xAI 官方 OAuth 客户端 ID（与 Grok CLI 一致）。</summary>
    public const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";

    /// <summary>OAuth 授权范围（offline_access 换取 refresh_token；grok-cli:access 供推理，api:access 供管理端点）。</summary>
    public const string Scope = "openid profile email offline_access grok-cli:access api:access";

    /// <summary>OAuth 请求 User-Agent。</summary>
    public const string OAuthUserAgent = "AITool-xai-oauth";

    /// <summary>xAI 推理 API 基础地址（隐藏站点 BaseUrl，原生 OpenAI Chat + Responses）。</summary>
    public const string ApiBaseUrl = "https://api.x.ai/v1";

    /// <summary>grok.com 账单端点（gRPC-web，查询 SuperGrok 订阅 credit 用量）。</summary>
    public const string GrokBillingEndpoint = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";

    /// <summary>托管站点标识源。</summary>
    public const string ManagedSource = "xai_oauth";

    /// <summary>access_token 提前刷新余量（xAI access_token 约 6 小时过期）。</summary>
    public const long TokenRefreshBufferMs = 60_000;

    /// <summary>
    /// 默认知名模型清单（对外公开名 == 上游 ID，xAI 无别名归一化）。
    /// 发往上游 /v1/models 实拉清单会归并进来，缺失时兜底。
    /// </summary>
    public static readonly IReadOnlyList<(string Slug, string DisplayName)> DefaultModels = new List<(string Slug, string DisplayName)>
    {
        ("grok-4.7", "Grok 4.7"),
        ("grok-4.5", "Grok 4.5"),
        ("grok-4-fast", "Grok 4 Fast"),
        ("grok-code-fast-1", "Grok Code Fast 1")
    };
}

/// <summary>
/// OIDC Discovery 文档（只取本流程用到的两个端点）。
/// </summary>
public sealed class XaiDiscoveryDocument
{
    public string DeviceAuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
}

/// <summary>
/// 设备授权码响应数据（RFC 8628 §3.2）。
/// </summary>
public sealed class XaiDeviceCodeResponse
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = string.Empty;
    public string VerificationUriComplete { get; set; } = string.Empty;
    public int ExpiresIn { get; set; } = 600;
    public int Interval { get; set; } = 5;
}

/// <summary>
/// Token 交换结果。
/// </summary>
public sealed class XaiTokenExchangeResult
{
    public bool IsSuccess { get; set; }
    public bool IsPending { get; set; }
    public bool IsSlowDown { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }
    public XaiTokenSet? TokenSet { get; set; }
}

/// <summary>
/// Token 集合（含从 JWT 解析出的账号身份）。
/// </summary>
public sealed class XaiTokenSet
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "bearer";
    public string? Scope { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>xAI 用户稳定标识（id_token / access_token JWT 的 sub claim）。</summary>
    public string? ExternalUserId { get; set; }

    /// <summary>展示用身份（email / preferred_username / name，按序取第一个非空）。</summary>
    public string? Email { get; set; }
}

/// <summary>
/// 刷新令牌已失效（上游 invalid_grant/invalid_token/401/403）：账号须重新登录。
/// </summary>
public sealed class XaiRefreshTokenInvalidException : InvalidOperationException
{
    public XaiRefreshTokenInvalidException()
        : base("xAI refresh_token 已失效，请重新登录")
    {
    }
}

/// <summary>
/// 账号创建/更新输入数据。
/// </summary>
public sealed class XaiProvisionInput
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? ExternalUserId { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "bearer";
    public string? Scope { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}

/// <summary>
/// 账号摘要信息（返回给前端展示）。
/// </summary>
public sealed class XaiAccountSummary
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? ExternalUserId { get; set; }
    public bool IsEnabled { get; set; }
    public bool RequiresReauth { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset? LastRefreshAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid LinkedSiteId { get; set; }
}

/// <summary>
/// xAI OAuth 客户端接口。
/// </summary>
public interface IXaiOAuthClient
{
    Task<XaiDeviceCodeResponse> StartDeviceFlowAsync(CancellationToken ct);
    Task<XaiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, CancellationToken ct);
    Task<XaiTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct);
}

/// <summary>
/// xAI 上游模型拉取接口。
/// </summary>
public interface IXaiModelFetcher
{
    Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAsync(string accessToken, CancellationToken ct);
}
