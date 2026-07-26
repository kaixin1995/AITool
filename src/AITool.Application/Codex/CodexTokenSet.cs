namespace AITool.Application.Codex;

/// <summary>
/// OAuth 授权码交换或 refresh_token 刷新后产出的 token 集合。
/// </summary>
public sealed class CodexTokenSet
{
    /// <summary>访问令牌，作为上游请求的 Bearer。</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>刷新令牌，用于后续刷新 access_token（部分上游会轮换，需以返回值为准）。</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>JWT id_token，包含 account_id / email / plan_type 等信息。</summary>
    public string IdToken { get; set; } = string.Empty;

    /// <summary>令牌类型，通常为 Bearer。</summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>access_token 有效期（秒），客户端据此计算过期时间。</summary>
    public int ExpiresIn { get; set; }
}
