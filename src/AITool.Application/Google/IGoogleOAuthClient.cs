namespace AITool.Application.Google;

/// <summary>
/// Google OAuth 令牌交换结果。
/// </summary>
public sealed record GoogleTokenSet
{
    /// <summary>访问令牌。</summary>
    public required string AccessToken { get; init; }

    /// <summary>刷新令牌（consent 流程下首次授权返回，后续刷新可能不带）。</summary>
    public string? RefreshToken { get; init; }

    /// <summary>有效期（秒）。Google access_token 通常 3600 秒。</summary>
    public int ExpiresIn { get; init; }

    /// <summary>授权 scope。</summary>
    public string? Scope { get; init; }

    /// <summary>按当前时间计算出的过期时刻。</summary>
    public DateTimeOffset ExpiresAt => DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, ExpiresIn));
}

/// <summary>
/// Google OAuth 授权会话（state 防 CSRF；Google 桌面客户端流程不需要 PKCE）。
/// </summary>
public sealed record GoogleOAuthSession(string State)
{
    /// <summary>会话创建时间。超时会话在完成登录时拒绝。</summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>会话有效期（对齐 Codex OAuth 会话的 10 分钟）。</summary>
    public static TimeSpan Lifetime => TimeSpan.FromMinutes(10);

    /// <summary>判断会话是否过期。</summary>
    public bool IsExpired => DateTimeOffset.UtcNow - CreatedAt > Lifetime;
}

/// <summary>
/// loadCodeAssist 响应中提取的账号元信息（Antigravity 登录时获取 project/tier/积分）。
/// </summary>
public sealed record GoogleCodeAssistProfile
{
    /// <summary>cloudaicompanionProject（作为请求体 project 字段）。</summary>
    public string? ProjectId { get; init; }

    /// <summary>原始订阅等级标识（如 g1-pro-tier），已归一化为 free/pro/ultra 的 Tier 字段。</summary>
    public string? RawTier { get; init; }

    /// <summary>归一化订阅等级：free / pro / ultra。</summary>
    public string? Tier { get; init; }

    /// <summary>剩余积分数量（availableCredits[0].creditAmount）。</summary>
    public int? CreditAmount { get; init; }
}

/// <summary>
/// Google OAuth 客户端：生成授权 URL、交换授权码、刷新令牌，以及登录后元信息探测
/// （userinfo 邮箱、项目列表、loadCodeAssist 项目/等级）。端口与参数对齐 gcli2api 的 google_oauth_api.py。
/// </summary>
public interface IGoogleOAuthClient
{
    /// <summary>创建一个授权会话（生成 state）。</summary>
    GoogleOAuthSession CreateSession();

    /// <summary>构造授权 URL（access_type=offline + prompt=consent 确保返回 refresh_token）。</summary>
    string BuildAuthorizeUrl(string accountKind, GoogleOAuthSession session);

    /// <summary>用授权码交换令牌。</summary>
    Task<GoogleTokenSet> ExchangeCodeAsync(string accountKind, string code, CancellationToken ct);

    /// <summary>用 refresh_token 刷新 access_token。新令牌可能不含 refresh_token，调用方需保留旧值。</summary>
    Task<GoogleTokenSet> RefreshTokenAsync(string accountKind, string refreshToken, CancellationToken ct);

    /// <summary>获取授权账号邮箱（userinfo）。失败返回 null。</summary>
    Task<string?> GetUserEmailAsync(string accessToken, CancellationToken ct);

    /// <summary>获取用户可访问的活跃 Google Cloud 项目列表（projectId）。</summary>
    Task<IReadOnlyList<string>> GetUserProjectsAsync(string accessToken, CancellationToken ct);

    /// <summary>调用 loadCodeAssist 获取 Antigravity 账号的 project/tier/积分（对齐 gcli2api fetch_project_id_and_tier，含 onboardUser 轮询回退）。</summary>
    Task<GoogleCodeAssistProfile> LoadCodeAssistProfileAsync(string accountKind, string accessToken, CancellationToken ct);
}
