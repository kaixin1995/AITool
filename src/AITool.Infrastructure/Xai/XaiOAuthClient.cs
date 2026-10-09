using System.Net;
using System.Text.Json;
using AITool.Application.Xai;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Xai;

/// <summary>
/// xAI (Grok) OAuth 客户端实现。
/// <para>
/// RFC 8628 设备码授权 + OIDC Discovery（端点经 https + auth.x.ai:443 校验后缓存），
/// Token 刷新在上游返回 invalid_grant/invalid_token/401/403 时抛
/// <see cref="XaiRefreshTokenInvalidException"/>（账号转 RequiresReauth）。
/// </para>
/// </summary>
public sealed class XaiOAuthClient : IXaiOAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<XaiOAuthClient> _logger;
    private volatile XaiDiscoveryDocument? _discoveredEndpoints;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    public XaiOAuthClient(HttpClient httpClient, ILogger<XaiOAuthClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<XaiDeviceCodeResponse> StartDeviceFlowAsync(CancellationToken ct)
    {
        var endpoints = await DiscoverEndpointsAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.DeviceAuthorizationEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = XaiConstants.ClientId,
                ["scope"] = XaiConstants.Scope
            })
        };
        request.Headers.TryAddWithoutValidation("User-Agent", XaiConstants.OAuthUserAgent);

        var (isSuccess, status, body) = await SendAsync(request, ct);
        if (!isSuccess)
        {
            _logger.LogWarning("xAI device code request failed ({StatusCode}): {Body}", (int)status, body);
            throw new InvalidOperationException($"xAI 设备授权请求失败 ({(int)status})：{DescribeError(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var deviceCode = GetString(root, "device_code");
        var userCode = GetString(root, "user_code");
        var verificationUri = GetString(root, "verification_uri");
        var verificationUriComplete = GetString(root, "verification_uri_complete");

        return new XaiDeviceCodeResponse
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUri = verificationUri,
            // 完整链接优先（已带 user_code，用户点击即填）；缺失时手工拼接。
            VerificationUriComplete = !string.IsNullOrWhiteSpace(verificationUriComplete)
                ? verificationUriComplete
                : $"{verificationUri}?user_code={Uri.EscapeDataString(userCode)}",
            ExpiresIn = GetInt(root, "expires_in", 600),
            Interval = GetInt(root, "interval", 5)
        };
    }

    /// <inheritdoc />
    public async Task<XaiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            throw new ArgumentException("deviceCode 不能为空", nameof(deviceCode));
        }

        var endpoints = await DiscoverEndpointsAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = XaiConstants.ClientId,
                ["device_code"] = deviceCode.Trim()
            })
        };
        request.Headers.TryAddWithoutValidation("User-Agent", XaiConstants.OAuthUserAgent);

        var (isSuccess, status, body) = await SendAsync(request, ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // OAuth 错误以 application/json 的 error 字段返回（authorization_pending 等）。
        if (root.TryGetProperty("error", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
        {
            var err = errProp.GetString()!;
            var errDesc = root.TryGetProperty("error_description", out var descProp) ? descProp.GetString() : null;
            if (string.Equals(err, "authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                return new XaiTokenExchangeResult { IsSuccess = false, IsPending = true, Error = err, ErrorDescription = errDesc };
            }
            if (string.Equals(err, "slow_down", StringComparison.OrdinalIgnoreCase))
            {
                return new XaiTokenExchangeResult { IsSuccess = false, IsSlowDown = true, Error = err, ErrorDescription = errDesc };
            }
            return new XaiTokenExchangeResult { IsSuccess = false, Error = err, ErrorDescription = errDesc ?? "授权失败或已过期" };
        }

        if (!isSuccess)
        {
            return new XaiTokenExchangeResult { IsSuccess = false, Error = "http_error", ErrorDescription = $"xAI Token 请求失败 ({(int)status})" };
        }

        var tokenSet = ParseTokenSet(root);
        if (tokenSet == null)
        {
            return new XaiTokenExchangeResult { IsSuccess = false, Error = "empty_access_token", ErrorDescription = "xAI 响应中未包含有效的 access_token" };
        }

        return new XaiTokenExchangeResult { IsSuccess = true, TokenSet = tokenSet };
    }

    /// <inheritdoc />
    public async Task<XaiTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("refreshToken 不能为空", nameof(refreshToken));
        }

        var endpoints = await DiscoverEndpointsAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = XaiConstants.ClientId,
                ["refresh_token"] = refreshToken.Trim(),
                ["scope"] = XaiConstants.Scope
            })
        };
        request.Headers.TryAddWithoutValidation("User-Agent", XaiConstants.OAuthUserAgent);

        var (isSuccess, status, body) = await SendAsync(request, ct);
        var bodyLooksInvalid = !LooksLikeJsonObject(body);

        // 对齐 cc-switch：401/403（以及 400 且响应体非法）一律判定 refresh_token 失效，
        // 即使上游返回空/HTML/畸形错误体也让账号转入重新登录，避免卡在刷新失败循环。
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden
            || (status == HttpStatusCode.BadRequest && bodyLooksInvalid))
        {
            throw new XaiRefreshTokenInvalidException();
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
        {
            var err = errProp.GetString()!;
            if (string.Equals(err, "invalid_grant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(err, "invalid_token", StringComparison.OrdinalIgnoreCase))
            {
                throw new XaiRefreshTokenInvalidException();
            }
            throw new InvalidOperationException($"xAI 刷新 Token 失败 ({(int)status})：{err}");
        }

        if (!isSuccess)
        {
            throw new InvalidOperationException($"xAI 刷新 Token 失败 ({(int)status})：{DescribeError(body)}");
        }

        var tokenSet = ParseTokenSet(root);
        return tokenSet ?? throw new InvalidOperationException("xAI 刷新响应中未包含有效的 access_token");
    }

    /// <summary>
    /// OIDC Discovery（进程内缓存）：解析 device_authorization / token 端点并校验
    /// scheme=https、host=auth.x.ai、端口 443、无用户信息，防 discovery 响应把凭证
    /// 引去第三方主机。
    /// </summary>
    private async Task<XaiDiscoveryDocument> DiscoverEndpointsAsync(CancellationToken ct)
    {
        var cached = _discoveredEndpoints;
        if (cached != null)
        {
            return cached;
        }

        await _discoveryLock.WaitAsync(ct);
        try
        {
            if (_discoveredEndpoints != null)
            {
                return _discoveredEndpoints;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, XaiConstants.DiscoveryUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", XaiConstants.OAuthUserAgent);
            var (isSuccess, status, body) = await SendAsync(request, ct);
            if (!isSuccess)
            {
                throw new InvalidOperationException($"xAI OIDC Discovery 请求失败 ({(int)status})");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var document = new XaiDiscoveryDocument
            {
                DeviceAuthorizationEndpoint = GetString(root, "device_authorization_endpoint"),
                TokenEndpoint = GetString(root, "token_endpoint")
            };
            ValidateXaiEndpoint(document.DeviceAuthorizationEndpoint);
            ValidateXaiEndpoint(document.TokenEndpoint);

            _discoveredEndpoints = document;
            return document;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    /// <summary>discovery 下发的端点必须指向 auth.x.ai:443（https、无 userinfo）。</summary>
    private static void ValidateXaiEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "auth.x.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("xAI 认证端点 URL 无效（须为 https://auth.x.ai:443）");
        }
    }

    private static XaiTokenSet? ParseTokenSet(JsonElement root)
    {
        var accessToken = GetString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var expiresIn = GetInt(root, "expires_in", 0);
        var identity = ExtractIdentityFromTokens(accessToken, GetString(root, "id_token"));

        return new XaiTokenSet
        {
            AccessToken = accessToken,
            RefreshToken = GetString(root, "refresh_token"),
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "bearer" : "bearer",
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null,
            ExpiresAt = expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn) : null,
            ExternalUserId = identity?.UserId,
            Email = identity?.Email
        };
    }

    private static (string UserId, string? Email)? ExtractIdentityFromTokens(string accessToken, string? idToken)
    {
        // 身份优先取 id_token claims，回退 access_token（两者都是 JWT；只解码不验签——
        // token 来自 auth.x.ai 的 TLS 通道，本地验签无密钥可校）。
        var claims = (idToken != null ? ParseJwtClaims(idToken) : null)
            ?? ParseJwtClaims(accessToken);
        if (claims is not { } claimElement)
        {
            return null;
        }

        var sub = claimElement.TryGetProperty("sub", out var subProp) ? subProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(sub))
        {
            return null;
        }

        var email = new[] { "email", "preferred_username", "name" }
            .Select(name => claimElement.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return (sub.Trim(), email);
    }

    /// <summary>解析 JWT payload（Base64Url 段 → JSON），失败返回 null。</summary>
    private static JsonElement? ParseJwtClaims(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var element = doc.RootElement.Clone();
            return element;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<(bool IsSuccess, HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, response.StatusCode, body);
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value) && value > 0 ? value : fallback;

    private static bool LooksLikeJsonObject(string body)
        => !string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{');

    private static string DescribeError(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
