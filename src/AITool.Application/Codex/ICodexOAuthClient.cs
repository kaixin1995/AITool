namespace AITool.Application.Codex;

/// <summary>
/// Codex（ChatGPT/OpenAI Codex CLI）OAuth 协议客户端接口。
/// <para>
/// 提供 PKCE 授权 URL 构造、授权码交换、refresh_token 刷新（带 single-flight）以及 id_token JWT 解析。
/// 协议细节移植自 CPA（reference-projects/CLIProxyAPI/internal/auth/codex）。
/// </para>
/// </summary>
public interface ICodexOAuthClient
{
    /// <summary>
    /// 创建一次 OAuth 会话，产出随机 state 与 PKCE code_verifier。
    /// state/verifier 的暂存与回调匹配由调用方（控制器）负责，本方法只生成。
    /// </summary>
    (string State, string Verifier) CreateOAuthSession();

    /// <summary>
    /// 构造授权 URL（含 PKCE challenge 与 Codex CLI 必需参数）。
    /// </summary>
    Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// 用授权码交换 token。
    /// </summary>
    Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken);

    /// <summary>
    /// 用 refresh_token 刷新 access_token。同一 refresh_token 的并发调用会被 single-flight 合并为一次真实上游请求。
    /// 注意刷新 scope 与授权不同（openid profile email，无 offline_access）。
    /// </summary>
    Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
}
