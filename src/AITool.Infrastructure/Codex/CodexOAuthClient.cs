using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Codex;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// Codex OAuth 协议客户端实现。常量与流程移植自 CPA
/// （reference-projects/CLIProxyAPI/internal/auth/codex/openai_auth.go）。
/// </summary>
public sealed class CodexOAuthClient : ICodexOAuthClient
{
    // —— OAuth 端点与凭据常量（来自 CPA openai_auth.go:24-29）——
    private const string AuthURL = "https://auth.openai.com/oauth/authorize";
    private const string TokenURL = "https://auth.openai.com/oauth/token";
    private const string ClientID = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string RedirectURI = "http://localhost:1455/auth/callback";

    // —— PKCE verifier 随机字节数（CPA pkce.go: 96 字节）——
    private const int VerifierByteLength = 96;

    private readonly HttpClient _httpClient;

    /// <summary>
    /// 由 DI 通过 AddHttpClient 注入，复用连接池。
    /// </summary>
    public CodexOAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public (string State, string Verifier) CreateOAuthSession()
    {
        // state：32 随机字节的 base64url（URL 安全，作为回调匹配凭据）
        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var state = Base64UrlEncode(stateBytes);

        // verifier：96 随机字节的 base64url（CPA pkce.go）
        var verifier = GenerateCodeVerifier();

        return (state, verifier);
    }

    /// <inheritdoc />
    public string BuildAuthorizeUrl(string state, string verifier)
    {
        var challenge = GenerateCodeChallenge(verifier);

        // 参数顺序与 CPA openai_auth.go:66-86 一致
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = ClientID,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectURI,
            ["scope"] = "openid email profile offline_access",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "login",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
        };

        using var content = new FormUrlEncodedContent(parameters);
        var query = content.ReadAsStringAsync().Result;
        return $"{AuthURL}?{query}";
    }

    /// <inheritdoc />
    public async Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientID,
            ["code"] = code,
            ["redirect_uri"] = RedirectURI,
            ["code_verifier"] = verifier,
        };

        return await PostTokenEndpointAsync(parameters, cancellationToken);
    }

    // —— single-flight：同一 refresh_token 并发只触发一次真实上游请求 ——
    // 注意：OpenAI 上游会轮换 refresh_token，旧 token 对应的 SemaphoreSlim 若不清理会内存泄漏。
    // 策略：刷新完成后，若此刻没有其他等待者（CurrentCount==1 表示空闲），从字典移除并释放。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    /// <inheritdoc />
    public async Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        // 同一 refresh_token 串行化，避免并发重复打上游（CPA 用 singleflight.Group 达到同样目的）
        var gate = _refreshLocks.GetOrAdd(refreshToken, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 注意刷新 scope 与授权不同：openid profile email，无 offline_access（CPA openai_auth.go:210-278）
            var parameters = new Dictionary<string, string?>
            {
                ["client_id"] = ClientID,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = "openid profile email",
            };

            return await PostTokenEndpointAsync(parameters, cancellationToken);
        }
        finally
        {
            gate.Release();
            // 清理无竞争的 entry，避免 token 轮换后旧 SemaphoreSlim 泄漏。
            // 仅当此刻空闲（CurrentCount==1，即无人等待）才移除；并发等待中则保留复用。
            // 用 CompareExchange 风格：先判断空闲再 TryRemove，移除成功才 Dispose（避免误释放正在等待的信号量）。
            if (gate.CurrentCount == 1 && _refreshLocks.TryRemove(refreshToken, out var removed) && ReferenceEquals(removed, gate))
            {
                removed.Dispose();
            }
        }
    }

    /// <summary>
    /// 统一的 token 端点 POST：form-urlencoded 请求，解析 JSON 响应为 CodexTokenSet。
    /// </summary>
    private async Task<CodexTokenSet> PostTokenEndpointAsync(Dictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(parameters);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenURL) { Content = content };
        request.Headers.Add("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Codex token endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new CodexTokenSet
        {
            AccessToken = root.TryGetProperty("access_token", out var atEl) && atEl.ValueKind == JsonValueKind.String
                ? atEl.GetString() ?? string.Empty : string.Empty,
            RefreshToken = root.TryGetProperty("refresh_token", out var rtEl) && rtEl.ValueKind == JsonValueKind.String
                ? rtEl.GetString() ?? string.Empty : string.Empty,
            IdToken = root.TryGetProperty("id_token", out var itEl) && itEl.ValueKind == JsonValueKind.String
                ? itEl.GetString() ?? string.Empty : string.Empty,
            TokenType = root.TryGetProperty("token_type", out var ttEl) && ttEl.ValueKind == JsonValueKind.String
                ? ttEl.GetString() ?? "Bearer" : "Bearer",
            ExpiresIn = root.TryGetProperty("expires_in", out var eiEl) && eiEl.TryGetInt32(out var secs)
                ? secs : 3600,
        };
    }

    // —— PKCE 工具 ——

    /// <summary>
    /// 生成 PKCE code_verifier：96 随机字节的 base64url（无 padding）。对应 CPA generateCodeVerifier。
    /// </summary>
    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(VerifierByteLength);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// 生成 PKCE code_challenge：BASE64URL(SHA256(verifier))，无 padding。对应 CPA generateCodeChallenge。
    /// </summary>
    private static string GenerateCodeChallenge(string verifier)
    {
        var verifierBytes = Encoding.UTF8.GetBytes(verifier);
        var hash = SHA256.HashData(verifierBytes);
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// base64url 编码（无 padding）。
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
