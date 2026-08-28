namespace AITool.Application.Kimi;

/// <summary>
/// Kimi OAuth 常量定义（与 reference-projects/CLIProxyAPI 中的 kimi.go 对齐）。
/// </summary>
public static class KimiConstants
{
    /// <summary>Kimi Code 官方 OAuth 客户端 ID。</summary>
    public const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";

    /// <summary>Kimi OAuth 主机地址。</summary>
    public const string OAuthHost = "https://auth.kimi.com";

    /// <summary>设备码授权端点。</summary>
    public const string DeviceAuthorizationEndpoint = "https://auth.kimi.com/api/oauth/device_authorization";

    /// <summary>Token 交换与刷新端点。</summary>
    public const string TokenEndpoint = "https://auth.kimi.com/api/oauth/token";

    /// <summary>Kimi API 业务基础 URL。</summary>
    public const string ApiBaseUrl = "https://api.kimi.com/coding";

    /// <summary>托管站点标识源。</summary>
    public const string ManagedSource = "kimi_oauth";

    /// <summary>
    /// 模拟的 Kimi Code CLI 版本号（X-Msh-Version 值，不带 v 前缀）。
    /// </summary>
    public const string ClientVersion = "1.49.0";

    /// <summary>
    /// 发往 api.kimi.com 的 User-Agent。
    /// 以官方 Kimi Code CLI 1.49.0 真实抓包为准：UA 为 "KimiCLI/{版本}"，
    /// 同时携带 x-msh-platform=kimi_cli 与一组 x-stainless-*（OpenAI Python SDK 指纹）。
    /// （cc-switch 文档所称"kimi-cli UA 会 403"与实测不符，官方客户端即使用此 UA。）
    /// </summary>
    public const string ClientUserAgent = "KimiCLI/1.49.0";

    /// <summary>
    /// 默认知名模型清单（<b>对外公开名</b>，与 CLIProxyAPI 注册表一致，供模型库/路由入口展示）。
    /// 发往上游的真实 ID 由 <see cref="KimiModelNormalizer.NormalizeUpstreamModel"/> 换算
    /// （如 kimi-k2.5→k2.5、kimi-k2.7-code→kimi-for-coding）。
    /// </summary>
    public static readonly IReadOnlyList<(string Slug, string DisplayName)> DefaultModels = new List<(string Slug, string DisplayName)>
    {
        ("kimi-k2", "Kimi K2"),
        ("kimi-k2-thinking", "Kimi K2 Thinking"),
        ("kimi-k2.5", "Kimi K2.5"),
        ("kimi-k2.6", "Kimi K2.6"),
        ("kimi-k2.7-code", "Kimi K2.7 Code"),
        ("kimi-k2.7-code-highspeed", "Kimi K2.7 Code HighSpeed"),
        ("kimi-k3", "Kimi K3"),
        ("kimi-k3-256k", "Kimi K3 256K")
    };
}

/// <summary>
/// Kimi 上游模型名规范化（移植自 CLIProxyAPI normalizeKimiUpstreamModel）：
/// 剥离 CLIProxyAPI 风格的 kimi- 前缀与 [1m] 上下文后缀，并把 k2.7 Code 别名重映射为官方模型 ID，
/// 使发往上游 /v1/chat/completions 的 model 字段始终是上游规范 ID。
/// </summary>
public static class KimiModelNormalizer
{
    /// <summary>
    /// 把对外模型名归一化为上游规范 ID；已规范的输入原样返回（幂等）。
    /// </summary>
    public static string NormalizeUpstreamModel(string? model)
    {
        var trimmed = (model ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.EndsWith("[1m]", StringComparison.Ordinal))
        {
            lower = lower[..^"[1m]".Length];
        }

        var normalized = lower switch
        {
            "kimi-k2.7-code" or "k2.7-code" or "kimi-for-coding" or "for-coding" => "kimi-for-coding",
            "kimi-k2.7-code-highspeed" or "k2.7-code-highspeed" or "kimi-for-coding-highspeed" or "for-coding-highspeed" => "kimi-for-coding-highspeed",
            _ => lower.StartsWith("kimi-", StringComparison.Ordinal) ? lower["kimi-".Length..] : lower
        };

        return normalized;
    }

    /// <summary>
    /// 把上游规范 ID 反查为对外公开名（对齐 CLIProxyAPI 注册表命名，如 k2.5→kimi-k2.5、
    /// kimi-for-coding→kimi-k2.7-code）；不在已知清单中的 ID 原样返回（小写化）。
    /// </summary>
    public static string PublicModelNameFromUpstream(string? upstreamModel)
    {
        var trimmed = (upstreamModel ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower switch
        {
            "k2" => "kimi-k2",
            "k2-thinking" => "kimi-k2-thinking",
            "k2.5" => "kimi-k2.5",
            "k2.6" => "kimi-k2.6",
            "kimi-for-coding" or "k2.7-code" => "kimi-k2.7-code",
            "kimi-for-coding-highspeed" or "k2.7-code-highspeed" => "kimi-k2.7-code-highspeed",
            "k3" => "kimi-k3",
            "k3-256k" => "kimi-k3-256k",
            _ => lower
        };
    }
}

/// <summary>
/// 设备授权码响应数据。
/// </summary>
public sealed class KimiDeviceCodeResponse
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = string.Empty;
    public string VerificationUriComplete { get; set; } = string.Empty;
    public int ExpiresIn { get; set; } = 300;
    public int Interval { get; set; } = 5;
    public string DeviceId { get; set; } = string.Empty;
}

/// <summary>
/// Token 交换结果。
/// </summary>
public sealed class KimiTokenExchangeResult
{
    public bool IsSuccess { get; set; }
    public bool IsPending { get; set; }
    public bool IsSlowDown { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }
    public KimiTokenSet? TokenSet { get; set; }
}

/// <summary>
/// Token 集合。
/// </summary>
public sealed class KimiTokenSet
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "bearer";
    public double ExpiresIn { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// 账号创建/更新输入数据。
/// </summary>
public sealed class KimiProvisionInput
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? UserId { get; set; }
    public string? DeviceId { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "bearer";
    public string? Scope { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}

/// <summary>
/// 账号摘要信息（返回给前端展示）。
/// </summary>
public sealed class KimiAccountSummary
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? UserId { get; set; }
    public string? DeviceId { get; set; }
    public bool IsEnabled { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset? LastRefreshAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid LinkedSiteId { get; set; }
}

/// <summary>
/// Kimi OAuth 客户端接口。
/// </summary>
public interface IKimiOAuthClient
{
    Task<KimiDeviceCodeResponse> StartDeviceFlowAsync(string? deviceId, CancellationToken ct);
    Task<KimiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, string? deviceId, CancellationToken ct);
    Task<KimiTokenSet> RefreshTokenAsync(string refreshToken, string? deviceId, CancellationToken ct);
}

/// <summary>
/// Kimi 上游模型拉取接口。
/// </summary>
public interface IKimiModelFetcher
{
    Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAsync(string accessToken, string? deviceId, CancellationToken ct);
}
