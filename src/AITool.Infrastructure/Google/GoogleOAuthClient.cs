using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AITool.Application.Google;
using AITool.Infrastructure.Common;

namespace AITool.Infrastructure.Google;

/// <summary>
/// Google OAuth 协议客户端实现。端点、client 凭据与流程对齐 gcli2api
/// （reference-projects/gcli2api/src/google_oauth_api.py）：
/// GeminiCLI/Antigravity 双套客户端身份，授权码 + refresh_token 两种换取方式，
/// 以及登录后的 userinfo / 项目列表 / loadCodeAssist 元信息探测。
/// </summary>
public sealed class GoogleOAuthClient : IGoogleOAuthClient
{
    private static readonly KeyedAsyncLock ApiEnableLocks = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> EnabledGeminiCliProjects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ApiEnableCacheDuration = TimeSpan.FromHours(6);
    private const string ServiceUsageBaseUrl = "https://serviceusage.googleapis.com";
    private static readonly string[] GeminiCliRequiredServices =
    [
        "geminicloudassist.googleapis.com",
        "cloudaicompanion.googleapis.com"
    ];

    /// <summary>
    /// 同一账号类型与 refresh_token 跨 transient 客户端实例共享刷新锁。
    /// </summary>
    private static readonly KeyedAsyncLock RefreshLocks = new();

    private readonly HttpClient _httpClient;

    public GoogleOAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public GoogleOAuthSession CreateSession()
    {
        // state：32 随机字节的 base64url，防 CSRF。
        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var state = Convert.ToBase64String(stateBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return new GoogleOAuthSession(state);
    }

    /// <inheritdoc />
    public string BuildAuthorizeUrl(string accountKind, GoogleOAuthSession session)
    {
        // 与 gcli2api Flow.get_auth_url 一致：offline + consent 确保签发 refresh_token。
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = GoogleAccountKinds.GetClientId(accountKind),
            ["redirect_uri"] = GoogleAccountKinds.RedirectUri,
            ["scope"] = string.Join(' ', GoogleAccountKinds.GetScopes(accountKind)),
            ["response_type"] = "code",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["state"] = session.State,
        };

        var query = string.Join('&', parameters
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return $"{GoogleAccountKinds.AuthorizeUrl}?{query}";
    }

    /// <inheritdoc />
    public async Task<GoogleTokenSet> ExchangeCodeAsync(string accountKind, string code, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = GoogleAccountKinds.GetClientId(accountKind),
            ["client_secret"] = GoogleAccountKinds.GetClientSecret(accountKind),
            ["redirect_uri"] = GoogleAccountKinds.RedirectUri,
            ["code"] = code,
        };

        return await PostTokenEndpointAsync(parameters, ct);
    }

    /// <inheritdoc />
    public async Task<GoogleTokenSet> RefreshTokenAsync(string accountKind, string refreshToken, CancellationToken ct)
    {
        var lockKey = $"{accountKind}\n{refreshToken}";
        using (await RefreshLocks.WaitAsync(lockKey, ct))
        {
            return await RefreshTokenCoreAsync(accountKind, refreshToken, ct);
        }
    }

    private async Task<GoogleTokenSet> RefreshTokenCoreAsync(string accountKind, string refreshToken, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = GoogleAccountKinds.GetClientId(accountKind),
            ["client_secret"] = GoogleAccountKinds.GetClientSecret(accountKind),
            ["refresh_token"] = refreshToken,
        };

        return await PostTokenEndpointAsync(parameters, ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetUserEmailAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GoogleAccountKinds.UserInfoUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("email", out var email)
                && email.ValueKind == JsonValueKind.String
                ? email.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetUserProjectsAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GoogleAccountKinds.ProjectsUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", "geminicli-oauth/1.0");
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var projects = new List<string>();
            if (doc.RootElement.TryGetProperty("projects", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var project in list.EnumerateArray())
                {
                    // 只取 ACTIVE 项目（对齐 gcli2api get_user_projects）。
                    if (project.TryGetProperty("lifecycleState", out var state)
                        && state.ValueKind == JsonValueKind.String
                        && !string.Equals(state.GetString(), "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (project.TryGetProperty("projectId", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        projects.Add(id.GetString()!);
                    }
                }
            }

            return projects;
        }
        catch
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<GoogleCodeAssistProfile> LoadCodeAssistProfileAsync(string accountKind, string accessToken, CancellationToken ct)
    {
        var baseUrl = GoogleAccountKinds.GetBaseUrl(accountKind);
        var profile = await TryLoadCodeAssistAsync(baseUrl, accessToken, ct);
        if (profile?.ProjectId is not null)
        {
            return profile;
        }

        // 回退 onboardUser（长时间运行操作，需轮询），对齐 gcli2api _try_onboard_user。
        var projectId = await TryOnboardUserAsync(baseUrl, accessToken, ct);
        return new GoogleCodeAssistProfile
        {
            ProjectId = projectId,
            RawTier = profile?.RawTier,
            Tier = profile?.Tier,
            CreditAmount = profile?.CreditAmount,
        };
    }

    /// <inheritdoc />
    public async Task<bool> EnsureGeminiCliApisAsync(string accessToken, string projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        projectId = projectId.Trim();
        if (EnabledGeminiCliProjects.TryGetValue(projectId, out var cachedUntil)
            && cachedUntil > DateTimeOffset.UtcNow)
        {
            return true;
        }

        using (await ApiEnableLocks.WaitAsync(projectId, ct))
        {
            if (EnabledGeminiCliProjects.TryGetValue(projectId, out cachedUntil)
                && cachedUntil > DateTimeOffset.UtcNow)
            {
                return true;
            }

            var allEnabled = true;
            foreach (var service in GeminiCliRequiredServices)
            {
                var serviceUrl = $"{ServiceUsageBaseUrl}/v1/projects/{Uri.EscapeDataString(projectId)}/services/{service}";
                try
                {
                    using var statusRequest = new HttpRequestMessage(HttpMethod.Get, serviceUrl);
                    statusRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    statusRequest.Headers.TryAddWithoutValidation("User-Agent", "geminicli-oauth/1.0");
                    using var statusResponse = await _httpClient.SendAsync(statusRequest, ct);
                    var statusBody = await statusResponse.Content.ReadAsStringAsync(ct);
                    if (statusResponse.IsSuccessStatusCode && IsServiceEnabledResponse(statusBody))
                    {
                        continue;
                    }

                    using var enableRequest = new HttpRequestMessage(HttpMethod.Post, serviceUrl + ":enable")
                    {
                        Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                    };
                    enableRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    enableRequest.Headers.TryAddWithoutValidation("User-Agent", "geminicli-oauth/1.0");
                    using var enableResponse = await _httpClient.SendAsync(enableRequest, ct);
                    var enableBody = await enableResponse.Content.ReadAsStringAsync(ct);
                    if (enableResponse.IsSuccessStatusCode
                        || (enableResponse.StatusCode == System.Net.HttpStatusCode.BadRequest
                            && enableBody.Contains("already enabled", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    allEnabled = false;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    allEnabled = false;
                }
            }

            if (allEnabled)
            {
                EnabledGeminiCliProjects[projectId] = DateTimeOffset.UtcNow.Add(ApiEnableCacheDuration);
            }

            return allEnabled;
        }
    }

    private async Task<GoogleCodeAssistProfile?> TryLoadCodeAssistAsync(string baseUrl, string accessToken, CancellationToken ct)
    {
        try
        {
            var body = new
            {
                metadata = new { ideType = "ANTIGRAVITY" }
            };
            using var response = await PostInternalAsync(
                $"{baseUrl}/v1internal:loadCodeAssist", accessToken, body, "antigravity", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            string? rawTier = null;
            if (root.TryGetProperty("paidTier", out var paidTier) && paidTier.ValueKind == JsonValueKind.Object
                && paidTier.TryGetProperty("id", out var paidId) && paidId.ValueKind == JsonValueKind.String)
            {
                rawTier = paidId.GetString();
            }
            else if (root.TryGetProperty("currentTier", out var currentTier) && currentTier.ValueKind == JsonValueKind.Object
                && currentTier.TryGetProperty("id", out var currentId) && currentId.ValueKind == JsonValueKind.String)
            {
                rawTier = currentId.GetString();
            }

            int? creditAmount = null;
            if (root.TryGetProperty("paidTier", out var paidForCredits) && paidForCredits.ValueKind == JsonValueKind.Object
                && paidForCredits.TryGetProperty("availableCredits", out var credits) && credits.ValueKind == JsonValueKind.Array)
            {
                foreach (var credit in credits.EnumerateArray())
                {
                    if (credit.ValueKind == JsonValueKind.Object
                        && credit.TryGetProperty("creditAmount", out var amount))
                    {
                        if (amount.ValueKind == JsonValueKind.Number && amount.TryGetInt32(out var parsed))
                        {
                            creditAmount = parsed;
                        }
                        else if (amount.ValueKind == JsonValueKind.String && int.TryParse(amount.GetString(), out var parsedString))
                        {
                            creditAmount = parsedString;
                        }
                    }

                    break;
                }
            }

            // 无 currentTier 表示尚未激活，onboardUser 回退处理。
            var activated = root.TryGetProperty("currentTier", out var activatedTier)
                && (activatedTier.ValueKind == JsonValueKind.Object || activatedTier.ValueKind == JsonValueKind.String);
            string? projectId = null;
            if (activated && root.TryGetProperty("cloudaicompanionProject", out var project)
                && project.ValueKind == JsonValueKind.String)
            {
                projectId = project.GetString();
            }

            return new GoogleCodeAssistProfile
            {
                ProjectId = projectId,
                RawTier = rawTier,
                Tier = MapRawTier(rawTier),
                CreditAmount = creditAmount,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsServiceEnabledResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("state", out var state)
                && state.ValueKind == JsonValueKind.String
                && string.Equals(state.GetString(), "ENABLED", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? MapRawTier(string? rawTier)
    {
        // 对齐 gcli2api _map_raw_tier：未知取值按 pro 处理。
        if (string.IsNullOrWhiteSpace(rawTier))
        {
            return null;
        }

        return rawTier.Trim().ToLowerInvariant() switch
        {
            "g1-ultra-tier" => "ultra",
            "ws-ai-ultra-business-tier" => "ultra",
            "g1-pro-tier" => "pro",
            "helium-tier" => "pro",
            "standard-tier" => "pro",
            "free-tier" => "free",
            _ => "pro"
        };
    }

    private async Task<string?> TryOnboardUserAsync(string baseUrl, string accessToken, CancellationToken ct)
    {
        try
        {
            // 先取默认 tier（对齐 gcli2api _get_onboard_tier）。
            var loadBody = new
            {
                metadata = new
                {
                    ideType = "ANTIGRAVITY",
                    platform = "PLATFORM_UNSPECIFIED",
                    pluginType = "GEMINI"
                }
            };
            string tierId = "LEGACY";
            using (var loadResponse = await PostInternalAsync($"{baseUrl}/v1internal:loadCodeAssist", accessToken, loadBody, "antigravity", ct))
            {
                if (loadResponse.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await loadResponse.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("allowedTiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tier in tiers.EnumerateArray())
                        {
                            if (tier.ValueKind == JsonValueKind.Object
                                && tier.TryGetProperty("isDefault", out var isDefault)
                                && isDefault.ValueKind == JsonValueKind.True
                                && tier.TryGetProperty("id", out var id)
                                && id.ValueKind == JsonValueKind.String)
                            {
                                tierId = id.GetString()!;
                                break;
                            }
                        }
                    }
                }
            }

            var onboardBody = new
            {
                tierId,
                metadata = new
                {
                    ideType = "ANTIGRAVITY",
                    platform = "PLATFORM_UNSPECIFIED",
                    pluginType = "GEMINI"
                }
            };

            // 长时间运行操作轮询（对齐 gcli2api：5 次 × 2 秒）。
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var response = await PostInternalAsync($"{baseUrl}/v1internal:onboardUser", accessToken, onboardBody, "antigravity", ct);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                if (doc.RootElement.TryGetProperty("response", out var inner) && inner.ValueKind == JsonValueKind.Object
                    && inner.TryGetProperty("cloudaicompanionProject", out var project))
                {
                    if (project.ValueKind == JsonValueKind.String)
                    {
                        return project.GetString();
                    }

                    if (project.ValueKind == JsonValueKind.Object
                        && project.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        return id.GetString();
                    }
                }

                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> PostInternalAsync(
        string url,
        string accessToken,
        object body,
        string userAgent,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return await _httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// 统一的 token 端点 POST：form-urlencoded 请求，解析 JSON 响应为 GoogleTokenSet。
    /// </summary>
    private async Task<GoogleTokenSet> PostTokenEndpointAsync(Dictionary<string, string?> parameters, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(parameters);
        using var request = new HttpRequestMessage(HttpMethod.Post, GoogleAccountKinds.TokenUrl) { Content = content };
        request.Headers.Add("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Google token endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var atEl) && atEl.ValueKind == JsonValueKind.String
            ? atEl.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException("Google token endpoint response missing access_token");
        }

        return new GoogleTokenSet
        {
            AccessToken = accessToken,
            RefreshToken = root.TryGetProperty("refresh_token", out var rtEl) && rtEl.ValueKind == JsonValueKind.String
                ? rtEl.GetString()
                : null,
            ExpiresIn = root.TryGetProperty("expires_in", out var eiEl) && eiEl.TryGetInt32(out var secs) ? secs : 3600,
            Scope = root.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.String
                ? scopeEl.GetString()
                : null,
        };
    }
}
