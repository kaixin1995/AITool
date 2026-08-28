using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using AITool.Application.Kimi;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Kimi;

/// <summary>
/// Kimi (Moonshot AI) OAuth 客户端实现。
/// 支持 RFC 8628 OAuth2 Device Authorization Grant 与 Token 刷新。
/// </summary>
public sealed class KimiOAuthClient : IKimiOAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<KimiOAuthClient> _logger;

    public KimiOAuthClient(HttpClient httpClient, ILogger<KimiOAuthClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private static void ApplyCommonHeaders(HttpRequestMessage request, string? deviceId)
    {
        var resolvedDeviceId = !string.IsNullOrWhiteSpace(deviceId) ? deviceId.Trim() : Guid.NewGuid().ToString();
        request.Headers.TryAddWithoutValidation("X-Msh-Platform", "CLIProxyAPI");
        request.Headers.TryAddWithoutValidation("X-Msh-Version", KimiConstants.ClientVersion);
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Name", Environment.MachineName);
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Model", GetDeviceModel());
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Id", resolvedDeviceId);
    }

    private static string GetDeviceModel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"Windows {RuntimeInformation.ProcessArchitecture}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return $"macOS {RuntimeInformation.ProcessArchitecture}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return $"Linux {RuntimeInformation.ProcessArchitecture}";
        return $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}";
    }

    /// <inheritdoc />
    public async Task<KimiDeviceCodeResponse> StartDeviceFlowAsync(string? deviceId, CancellationToken ct)
    {
        var resolvedDeviceId = !string.IsNullOrWhiteSpace(deviceId) ? deviceId.Trim() : Guid.NewGuid().ToString();
        var formData = new Dictionary<string, string>
        {
            ["client_id"] = KimiConstants.ClientId
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, KimiConstants.DeviceAuthorizationEndpoint)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        ApplyCommonHeaders(request, resolvedDeviceId);

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Kimi device code request failed ({StatusCode}): {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Kimi 设备授权请求失败 ({(int)response.StatusCode})：{body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var deviceCode = root.TryGetProperty("device_code", out var dc) ? dc.GetString() ?? string.Empty : string.Empty;
        var userCode = root.TryGetProperty("user_code", out var uc) ? uc.GetString() ?? string.Empty : string.Empty;
        var verificationUri = root.TryGetProperty("verification_uri", out var vu) ? vu.GetString() ?? string.Empty : string.Empty;
        var verificationUriComplete = root.TryGetProperty("verification_uri_complete", out var vuc) ? vuc.GetString() ?? string.Empty : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var expVal) ? expVal : 300;
        var interval = root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var ivVal) ? ivVal : 5;

        if (string.IsNullOrWhiteSpace(verificationUriComplete) && !string.IsNullOrWhiteSpace(verificationUri) && !string.IsNullOrWhiteSpace(userCode))
        {
            verificationUriComplete = $"{verificationUri}?user_code={userCode}";
        }

        return new KimiDeviceCodeResponse
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUri = verificationUri,
            VerificationUriComplete = verificationUriComplete,
            ExpiresIn = expiresIn,
            Interval = interval,
            DeviceId = resolvedDeviceId
        };
    }

    /// <inheritdoc />
    public async Task<KimiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, string? deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
            throw new ArgumentException("deviceCode 不能为空", nameof(deviceCode));

        var formData = new Dictionary<string, string>
        {
            ["client_id"] = KimiConstants.ClientId,
            ["device_code"] = deviceCode.Trim(),
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, KimiConstants.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        ApplyCommonHeaders(request, deviceId);

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
        {
            var err = errProp.GetString()!;
            var errDesc = root.TryGetProperty("error_description", out var descProp) ? descProp.GetString() : null;

            if (string.Equals(err, "authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                return new KimiTokenExchangeResult { IsSuccess = false, IsPending = true, Error = err, ErrorDescription = errDesc };
            }
            if (string.Equals(err, "slow_down", StringComparison.OrdinalIgnoreCase))
            {
                return new KimiTokenExchangeResult { IsSuccess = false, IsSlowDown = true, Error = err, ErrorDescription = errDesc };
            }
            return new KimiTokenExchangeResult { IsSuccess = false, Error = err, ErrorDescription = errDesc };
        }

        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new KimiTokenExchangeResult
            {
                IsSuccess = false,
                Error = "empty_access_token",
                ErrorDescription = "Kimi 响应中未包含有效的 access_token"
            };
        }

        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        var tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "bearer" : "bearer";
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
        double expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetDouble(out var expVal) ? expVal : 0;

        DateTimeOffset? expiresAt = expiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : null;

        return new KimiTokenExchangeResult
        {
            IsSuccess = true,
            TokenSet = new KimiTokenSet
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                TokenType = tokenType,
                Scope = scope,
                ExpiresIn = expiresIn,
                ExpiresAt = expiresAt
            }
        };
    }

    /// <inheritdoc />
    public async Task<KimiTokenSet> RefreshTokenAsync(string refreshToken, string? deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("refreshToken 不能为空", nameof(refreshToken));

        var formData = new Dictionary<string, string>
        {
            ["client_id"] = KimiConstants.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken.Trim()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, KimiConstants.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        ApplyCommonHeaders(request, deviceId);

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"Kimi refresh_token 已失效或被拒绝 ({(int)response.StatusCode})：{body}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kimi 刷新 Token 失败 ({(int)response.StatusCode})：{body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Kimi 刷新响应中未包含有效的 access_token");
        }

        var newRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "bearer" : "bearer";
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
        double expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetDouble(out var expVal) ? expVal : 0;

        DateTimeOffset? expiresAt = expiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : null;

        return new KimiTokenSet
        {
            AccessToken = accessToken,
            RefreshToken = !string.IsNullOrWhiteSpace(newRefreshToken) ? newRefreshToken : refreshToken.Trim(),
            TokenType = tokenType,
            Scope = scope,
            ExpiresIn = expiresIn,
            ExpiresAt = expiresAt
        };
    }
}
