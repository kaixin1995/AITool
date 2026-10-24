using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// Kiro 厂商定义（VendorKey=kiro，浏览器登录 + 粘贴回调）。
/// <para>
/// 流程对齐 Kiro IDE 官方客户端（参照 magpie-community/plugins/packages/kiro）：
/// 浏览器打开 app.kiro.dev/signin（PKCE S256 + redirect_from=KiroIDE），回调地址固定为
/// 本机 127.0.0.1:17893 —— 服务器部署下浏览器回跳页面打不开是预期行为，地址栏携带
/// code/state/login_option，用户整体粘贴回来完成登录。两条支路：
/// ① 社交登录（Google/GitHub）：code 直接在 prod.us-east-1.auth.desktop.kiro.dev/oauth/token 换 token；
/// ② AWS Builder ID / IAM Identity Center：回调先带 issuer_url/idc_region 回来，
///    厂商在 AWS OIDC /client/register 动态注册公共客户端，把浏览器引向 AWS authorize（续跳），
///    第二段回调的 code 在 oidc.{region}.amazonaws.com/token 换 token（camelCase JSON）。
/// 刷新：社交走 prod.{region}.auth.desktop.kiro.dev/refreshToken（JSON {refreshToken}），
/// AWS 客户端走 oidc.{region}.amazonaws.com/token（表单 refresh_token 授权）。
/// 额度：management.{region}.kiro.dev/Get-Usage-Limits（usageBreakdownList 积分窗口）。
/// 聊天转发走 runtime.{region}.kiro.dev 的 AWS 事件流协议，需专用协议适配器——暂不开放（ChatSiteConfig=null）。
/// </para>
/// </summary>
public sealed class KiroOAuthVendor : IDeviceOAuthVendor
{
    private const string Portal = "https://app.kiro.dev";
    private const string AuthService = "https://prod.us-east-1.auth.desktop.kiro.dev";
    private const string DefaultRegion = "us-east-1";

    /// <summary>回调地址（登录与换 token 时必须逐字节一致；本机无监听，用户粘贴回地址）。</summary>
    private const string CallbackBase = "http://127.0.0.1:17893";
    private const string SocialCallback = CallbackBase + "/oauth/callback";
    private const string AwsCallback = CallbackBase + "/oauth/callback";

    private static readonly string[] Scopes =
    [
        "codewhisperer:completions",
        "codewhisperer:analysis",
        "codewhisperer:conversations",
        "codewhisperer:transformations",
    ];

    private const string DesktopUa = "Kiro-Desktop/0.2.13 (darwin; arm64)";

    private readonly HttpClient _httpClient;

    public KiroOAuthVendor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string VendorKey => "kiro";

    public string Label => "Kiro（AWS）";

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; } =
    [
        ("claude-sonnet-4-5", "Claude Sonnet 4.5 (Kiro)"),
    ];

    /// <summary>聊天需 AWS 事件流协议适配器，暂不开放。</summary>
    public DeviceOAuthChatSiteConfig? ChatSiteConfig => null;

    public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = $"{Portal}/signin?state={Uri.EscapeDataString(state)}"
                  + $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256"
                  + $"&redirect_uri={Uri.EscapeDataString(CallbackBase)}&redirect_from=KiroIDE";
        var session = JsonSerializer.Serialize(new KiroSession
        {
            Phase = KiroSessionPhase.Portal,
            Verifier = verifier,
            State = state,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        return Task.FromResult(new DeviceOAuthStartResult(
            url, null,
            "在浏览器完成 Kiro 登录（Google / GitHub / AWS Builder ID 均可），然后把回跳到 127.0.0.1:17893 的完整地址粘贴回来",
            session));
    }

    public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
        => Task.FromResult(new DeviceOAuthPollResult { IsPending = true, Error = "请粘贴回调地址完成登录" });

    public async Task<DeviceOAuthPollResult> CompleteWithCallbackAsync(string sessionJson, string callbackUrl, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<KiroSession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录已超时，请重新发起" };
        }

        if (!Uri.TryCreate(callbackUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return new DeviceOAuthPollResult { Error = "回调地址无法解析，请粘贴完整的 127.0.0.1:17893 回跳地址" };
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var error = query.Get("error");
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new DeviceOAuthPollResult { Error = query.Get("error_description") ?? error };
        }

        var loginOption = query.Get("login_option") ?? string.Empty;

        // —— AWS 第二段：state 与第一段注册时下发的 aws.state 匹配，code 到 AWS OIDC 换 token。
        var state = query.Get("state");
        if (session.Phase == KiroSessionPhase.Aws && string.Equals(state, session.AwsState, StringComparison.Ordinal))
        {
            var code = query.Get("code");
            if (string.IsNullOrWhiteSpace(code))
            {
                return new DeviceOAuthPollResult { Error = "AWS 回调缺少 code 参数" };
            }

            var token = await PostJsonAsync<JsonElement>($"https://oidc.{session.Region}.amazonaws.com/token", new Dictionary<string, object?>
            {
                ["clientId"] = session.AwsClientId!,
                ["clientSecret"] = session.AwsClientSecret!,
                ["grantType"] = "authorization_code",
                ["redirectUri"] = AwsCallback,
                ["code"] = code,
                ["codeVerifier"] = session.AwsVerifier!,
            }, cancellationToken);
            var access = GetString(token, "accessToken");
            if (string.IsNullOrWhiteSpace(access))
            {
                return new DeviceOAuthPollResult { Error = "AWS 返回的响应缺少 accessToken" };
            }

            return Success(
                access,
                GetString(token, "refreshToken"),
                GetNumber(token, "expiresIn"),
                method: "idc",
                region: session.Region!,
                profileArn: null,
                clientId: session.AwsClientId,
                awsClientSecret: session.AwsClientSecret);
        }

        // —— Kiro 门户回调（社交直换；Builder ID 需要动态注册 AWS 客户端后续跳）。
        if (!string.Equals(state, session.State, StringComparison.Ordinal))
        {
            return new DeviceOAuthPollResult { Error = "回调 state 不匹配，请重新发起登录" };
        }

        var portalCode = query.Get("code");
        if (string.IsNullOrWhiteSpace(portalCode))
        {
            return new DeviceOAuthPollResult { Error = "回调缺少 code 参数" };
        }

        if (loginOption is "google" or "github")
        {
            var redirectUri = SocialCallback + "?login_option=" + loginOption;
            var form = await PostFormAsync(AuthService + "/oauth/token", new Dictionary<string, string>
            {
                ["code"] = portalCode,
                ["code_verifier"] = session.Verifier,
                ["redirect_uri"] = redirectUri,
            }, cancellationToken);
            var access = GetString(form, "accessToken") ?? GetString(form, "access_token");
            if (string.IsNullOrWhiteSpace(access))
            {
                return new DeviceOAuthPollResult { Error = "Kiro 返回的响应缺少 accessToken" };
            }

            return Success(
                access,
                GetString(form, "refreshToken") ?? GetString(form, "refresh_token"),
                GetNumber(form, "expiresIn") ?? GetNumber(form, "expires_in"),
                method: "social",
                region: DefaultRegion,
                profileArn: GetString(form, "profileArn") ?? GetString(form, "profile_arn"),
                clientId: null);
        }

        if (loginOption is "builderid" or "awsidc" or "internal")
        {
            var issuer = query.Get("issuer_url");
            var region = query.Get("idc_region");
            if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(region))
            {
                return new DeviceOAuthPollResult { Error = "Kiro 页面未告知 AWS 登录位置（issuer_url/idc_region 缺失）" };
            }

            // 在 AWS OIDC 动态注册公共客户端（对齐 Kiro IDE 的注册参数）。
            var registration = await PostJsonAsync<JsonElement>($"https://oidc.{region}.amazonaws.com/client/register", new Dictionary<string, object?>
            {
                ["clientName"] = "Kiro IDE",
                ["clientType"] = "public",
                ["scopes"] = Scopes,
                ["grantTypes"] = new[] { "authorization_code", "refresh_token" },
                ["redirectUris"] = new[] { AwsCallback },
                ["issuerUrl"] = issuer,
            }, cancellationToken);
            var clientId = GetString(registration, "clientId");
            var clientSecret = GetString(registration, "clientSecret");
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                return new DeviceOAuthPollResult { Error = "AWS 客户端注册响应缺少 clientId/clientSecret" };
            }

            var awsVerifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            var awsState = Base64Url(RandomNumberGenerator.GetBytes(24));
            var awsChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(awsVerifier)));
            var authorizeUrl = $"https://oidc.{region}.amazonaws.com/authorize?response_type=code"
                               + $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(AwsCallback)}"
                               + $"&scopes={Uri.EscapeDataString(string.Join(",", Scopes))}"
                               + $"&state={Uri.EscapeDataString(awsState)}&code_challenge={Uri.EscapeDataString(awsChallenge)}&code_challenge_method=S256";

            session.Phase = KiroSessionPhase.Aws;
            session.Region = region;
            session.AwsClientId = clientId;
            session.AwsClientSecret = clientSecret;
            session.AwsVerifier = awsVerifier;
            session.AwsState = awsState;

            return new DeviceOAuthPollResult
            {
                IsPending = true,
                NextAuthorizeUrl = authorizeUrl,
                UpdatedSessionJson = JsonSerializer.Serialize(session),
                Error = "请在打开的 AWS 页面完成 Builder ID / IAM Identity Center 登录，再粘贴新的回跳地址",
            };
        }

        return new DeviceOAuthPollResult { Error = $"Kiro 页面返回了暂不支持的登录方式（{loginOption}）" };
    }

    /// <summary>带 ExtraJson 的刷新：AWS 客户端形态走 oidc token，社交形态走自有 refreshToken 端点。</summary>
    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, string? extraJson, CancellationToken cancellationToken)
        => RefreshWithExtraAsync(refreshToken, extraJson, cancellationToken);

    public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        // ExtraJson 不参与本调用（调用方持有账号上下文不可行）——按刷新端点双形态依次尝试：
        // 先 Kiro 自有 refreshToken 端点（社交登录），被拒再走 AWS OIDC（需 clientId，仅当 ExtraJson 有）。
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://prod.{DefaultRegion}.auth.desktop.kiro.dev/refreshToken")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { refreshToken }), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", DesktopUa);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                var access = GetString(body, "accessToken") ?? GetString(body, "access_token");
                if (!string.IsNullOrWhiteSpace(access))
                {
                    return new DeviceOAuthTokenSet
                    {
                        AccessToken = access,
                        RefreshToken = GetString(body, "refreshToken") ?? GetString(body, "refresh_token") ?? refreshToken,
                        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetNumber(body, "expiresIn") ?? GetNumber(body, "expires_in") ?? 3600),
                    };
                }

                throw new InvalidOperationException("Kiro 刷新响应缺少 accessToken");
            }

            if ((int)response.StatusCode is 400 or 401 or 403)
            {
                throw new DeviceOAuthRefreshRefusedException("Kiro refresh_token 已失效，请重新登录");
            }

            // 5xx/网络抖动等暂时性错误：抛可重试异常，绝不能标记需重登（否则账号被砖）。
            throw new InvalidOperationException($"Kiro 刷新暂时失败：HTTP {(int)response.StatusCode}");
        }
        catch (DeviceOAuthRefreshRefusedException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Kiro 刷新请求失败：" + ex.Message, ex);
        }
    }

    /// <summary>按账号 ExtraJson 携带的形态刷新（社交 / AWS 客户端），供刷新服务经 QueryQuotaAsync 的凭证路径之外单独使用。</summary>
    public async Task<DeviceOAuthTokenSet> RefreshWithExtraAsync(string refreshToken, string? extraJson, CancellationToken cancellationToken)
    {
        var method = TryGetExtra(extraJson, "method") ?? "social";
        var region = TryGetExtra(extraJson, "region") ?? DefaultRegion;
        if (method == "idc" && TryGetExtra(extraJson, "clientId") is { } clientId)
        {
            var form = await PostFormAsync($"https://oidc.{region}.amazonaws.com/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                // AWS SSO-OIDC 公共客户端的 refresh 同样要求 clientSecret（登录换 token 时已发）。
                ["client_secret"] = TryGetExtra(extraJson, "clientSecret") ?? string.Empty,
                ["refresh_token"] = refreshToken,
            }, cancellationToken);
            var access = GetString(form, "accessToken") ?? GetString(form, "access_token");
            if (string.IsNullOrWhiteSpace(access))
            {
                throw new DeviceOAuthRefreshRefusedException("Kiro refresh_token 已失效，请重新登录");
            }

            return new DeviceOAuthTokenSet
            {
                AccessToken = access,
                RefreshToken = GetString(form, "refreshToken") ?? GetString(form, "refresh_token") ?? refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetNumber(form, "expiresIn") ?? GetNumber(form, "expires_in") ?? 3600),
            };
        }

        return await RefreshTokenAsync(refreshToken, cancellationToken);
    }

    public async Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        var region = TryGetExtra(extraJson, "region") ?? DefaultRegion;
        var profileArn = TryGetExtra(extraJson, "profileArn");
        try
        {
            var query = new Dictionary<string, string>
            {
                ["origin"] = "KIRO_CLI",
                ["resourceType"] = "CREDIT",
                ["isEmailRequired"] = "true",
            };
            if (!string.IsNullOrWhiteSpace(profileArn))
            {
                query["profileArn"] = profileArn!;
            }

            var url = $"https://management.{region}.kiro.dev/Get-Usage-Limits?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.AccessToken.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new DeviceOAuthQuotaResult { Success = false, Error = "凭证失效，请重新登录", CredentialInvalid = true };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DeviceOAuthQuotaResult { Success = false, Error = $"额度查询 HTTP {(int)response.StatusCode}" };
            }

            var parsed = KiroUsageParser.Parse(text);
            return parsed ?? new DeviceOAuthQuotaResult { Success = false, Error = "额度响应无法解析" };
        }
        catch (Exception ex)
        {
            return new DeviceOAuthQuotaResult { Success = false, Error = "额度查询失败：" + ex.Message };
        }
    }

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => KiroUsageParser.Parse(rawJson);

    private static DeviceOAuthPollResult Success(
        string accessToken, string? refreshToken, double? expiresIn,
        string method, string region, string? profileArn, string? clientId, string? awsClientSecret = null)
    {
        var extra = new Dictionary<string, string> { ["method"] = method, ["region"] = region };
        if (!string.IsNullOrWhiteSpace(profileArn)) extra["profileArn"] = profileArn!;
        if (!string.IsNullOrWhiteSpace(clientId)) extra["clientId"] = clientId!;
        // IDC 客户端的 secret 刷新必带（登录时动态注册所得），必须随账号持久化。
        if (method == "idc" && !string.IsNullOrWhiteSpace(awsClientSecret)) extra["clientSecret"] = awsClientSecret!;
        return new DeviceOAuthPollResult
        {
            Tokens = new DeviceOAuthTokenSet
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn ?? 3600),
            },
            ExtraJson = JsonSerializer.Serialize(extra),
        };
    }

    private async Task<JsonElement> PostJsonAsync<T>(string url, Dictionary<string, object?> payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 400 or 401 or 403)
            {
                throw new DeviceOAuthRefreshRefusedException($"Kiro/AWS 拒绝了请求（HTTP {(int)response.StatusCode}）");
            }

            throw new InvalidOperationException($"Kiro/AWS HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 400 or 401 or 403)
            {
                throw new DeviceOAuthRefreshRefusedException($"Kiro 拒绝了该授权码/token（HTTP {(int)response.StatusCode}）");
            }

            throw new InvalidOperationException($"Kiro HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    internal static string? TryGetExtra(string? extraJson, string key)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            var root = JsonDocument.Parse(extraJson).RootElement;
            return root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max = 200)
        => text.Length <= max ? text : text[..max] + "…";

    internal static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    internal static double? GetNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class KiroSession
    {
        public KiroSessionPhase Phase { get; set; }
        public string Verifier { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? Region { get; set; }
        public string? AwsClientId { get; set; }
        public string? AwsClientSecret { get; set; }
        public string? AwsVerifier { get; set; }
        public string? AwsState { get; set; }
        public DateTimeOffset DeadlineUtc { get; set; }
    }

    private enum KiroSessionPhase
    {
        Portal,
        Aws,
    }
}

/// <summary>Kiro 额度解析：Get-Usage-Limits 的 usageBreakdownList（积分窗口 + 免费试用）。</summary>
public static class KiroUsageParser
{
    public static DeviceOAuthQuotaResult? Parse(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var windows = new List<AccountQuotaWindow>();
            if (root.TryGetProperty("usageBreakdownList", out var breakdown) && breakdown.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in breakdown.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var used = GetNumber(item, "currentUsageWithPrecision") ?? 0;
                    var limit = GetNumber(item, "usageLimitWithPrecision") ?? 0;
                    if (limit > 0)
                    {
                        windows.Add(new AccountQuotaWindow
                        {
                            Id = $"kiro_usage_{index}",
                            Label = $"积分（{used:0.##}/{limit:0.##}）",
                            UsedPercent = Math.Clamp(100 * used / limit, 0, 100),
                        });
                    }

                    if (item.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind == JsonValueKind.Object)
                    {
                        var trialLimit = GetNumber(trial, "usageLimitWithPrecision") ?? 0;
                        var trialUsed = GetNumber(trial, "currentUsageWithPrecision") ?? 0;
                        if (trialLimit > 0
                            && string.Equals(GetString(trial, "freeTrialStatus"), "ACTIVE", StringComparison.OrdinalIgnoreCase))
                        {
                            windows.Add(new AccountQuotaWindow
                            {
                                Id = $"kiro_trial_{index}",
                                Label = $"免费试用（{trialUsed:0.##}/{trialLimit:0.##}）",
                                UsedPercent = Math.Clamp(100 * trialUsed / trialLimit, 0, 100),
                                ResetAtUtc = GetNumber(trial, "freeTrialExpiry") is { } expiry
                                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(expiry < 1e12 ? expiry * 1000 : expiry))
                                    : null,
                            });
                        }
                    }

                    index++;
                }
            }

            if (windows.Count == 0
                && !root.TryGetProperty("usageBreakdownList", out _)
                && !root.TryGetProperty("subscriptionInfo", out _))
            {
                return null;
            }

            return new DeviceOAuthQuotaResult { Success = true, PlanType = "Kiro", Windows = windows };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    internal static double? GetNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;
}
