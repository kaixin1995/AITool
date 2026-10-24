using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.OAuth;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// MiniMax Code 厂商定义（国内站 minimax.cn / 国际站 minimax.io，VendorKey=minimax-cn / minimax-global）。
/// <para>
/// 端点与协议对齐官方 mcode 客户端（参照 magpie-community/plugins/packages/minimax）：
/// OAuth 2 设备码流（PKCE S256，client_id=mcode-public，scope=agent.default，audience=agent-backend）；
/// 聊天走 {agent.llm}/mavis/api/v1/llm/v1 的 Anthropic Messages（Bearer access_token + 静态头）；
/// 额度 = M Plan 窗口（platform coding_plan/remains）+ 账号余额（agent 签名 API get_membership_info）。
/// 签名算法（x-signature/yy，MD5 链）为账号 API 专属，聊天转发不需要。
/// </para>
/// </summary>
public sealed class MiniMaxOAuthVendor : IDeviceOAuthVendor
{
    private const string ClientId = "mcode-public";
    private const string Scope = "agent.default";
    private const string Audience = "agent-backend";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string SignSecret = "I*7Cf%WZ#S&%1RlZJ&C2";

    private sealed record SiteSpec(string Key, string Label, string Account, string Llm, string Agent, string Platform, string Lang);

    private static readonly SiteSpec Cn = new(
        "minimax-cn", "MiniMax Code",
        "https://account.minimax.cn", "https://agent.minimax.cn", "https://agent.minimaxi.com", "https://www.minimaxi.com", "zh");
    private static readonly SiteSpec Global = new(
        "minimax-global", "MiniMax Code (Global)",
        "https://account.minimax.io", "https://agent.minimax.io", "https://agent.minimax.io", "https://platform.minimax.io", "en");

    private readonly HttpClient _httpClient;
    private readonly ILogger<MiniMaxOAuthVendor> _logger;
    private readonly SiteSpec _site;

    public MiniMaxOAuthVendor(HttpClient httpClient, ILogger<MiniMaxOAuthVendor> logger, bool global = false)
    {
        _httpClient = httpClient;
        _logger = logger;
        _site = global ? Global : Cn;
    }

    public string VendorKey => _site.Key;

    public string Label => _site.Label;

    public static IReadOnlyList<(string Slug, string DisplayName)> ModelList { get; } =
    [
        ("MiniMax-M3.1-Flash-Preview", "MiniMax M3.1 Flash Preview"),
        ("MiniMax-M3", "MiniMax M3"),
        ("MiniMax-M2.7", "MiniMax M2.7"),
        ("MiniMax-M2.7-highspeed", "MiniMax M2.7 Highspeed"),
    ];

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => ModelList;

    /// <summary>聊天站点：Anthropic 协议，{llm}/mavis/api/v1/llm/v1 基地址（含版本段）+ mcode 静态头。</summary>
    public DeviceOAuthChatSiteConfig? ChatSiteConfig => new(
        _site.Llm + "/mavis/api/v1/llm/v1",
        "Anthropic",
        SupportsOpenAi: false,
        SupportsAnthropic: true,
        SupportsResponses: false,
        new Dictionary<string, string>
        {
            ["x-api-key"] = "sk-xxx", // Anthropic SDK 占位密钥，真实鉴权在 Authorization Bearer（Site.ApiKey）
            ["User-Agent"] = "MiniMaxAgent",
            ["X-Mavis-Agent-Id"] = "main",
            ["anthropic-version"] = "2023-06-01",
            ["X-AITool-Auth-Mode"] = "bearer",
        });

    public async Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["audience"] = Audience,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var body = await PostFormAsync(_site.Account + "/oauth2/device/code", form, cancellationToken);
        var userCode = GetString(body, "user_code");
        var deviceCode = GetString(body, "device_code");
        var uri = GetString(body, "verification_uri") ?? GetString(body, "verification_url");
        if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(uri)
            || (string.IsNullOrEmpty(deviceCode) && GetNumber(body, "expired_in") is null))
        {
            throw new InvalidOperationException("MiniMax 设备码响应不完整");
        }

        // MiniMax 变体：可能没有 device_code，改用 user_code 轮询（expired_in 毫秒、interval 毫秒）。
        var pollByUser = string.IsNullOrEmpty(deviceCode);
        var session = JsonSerializer.Serialize(new MiniMaxSession
        {
            Verifier = verifier,
            DeviceCode = deviceCode,
            UserCode = userCode,
            PollByUser = pollByUser,
            IntervalMs = pollByUser ? (int?)GetNumber(body, "interval") ?? 5000 : 5000,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        return new DeviceOAuthStartResult(
            GetString(body, "verification_uri_complete") ?? uri,
            userCode,
            $"请在浏览器中完成 MiniMax 登录并授权（用户码 {userCode}）",
            session);
    }

    public async Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<MiniMaxSession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录码已过期，请重新发起" };
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = DeviceGrant,
            ["client_id"] = ClientId,
            ["code_verifier"] = session.Verifier,
        };
        if (session.PollByUser)
        {
            form["user_code"] = session.UserCode!;
        }
        else
        {
            form["device_code"] = session.DeviceCode!;
        }

        var (status, body) = await TryPostFormAsync(_site.Account + "/oauth2/token", form, cancellationToken);
        var bodyStatus = GetString(body, "status");
        if (status is 200 && bodyStatus is "pending")
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        if (status is 200 && bodyStatus is "slow_down")
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        if (status is 200 && bodyStatus is "denied" or "access_denied")
        {
            return new DeviceOAuthPollResult { IsDenied = true, Error = "用户拒绝了授权" };
        }

        if (status is 200 && bodyStatus is "expired" or "expired_token")
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录码已过期，请重新发起" };
        }

        if (status is 200)
        {
            return FromTokenBody(body);
        }

        var error = GetString(body, "error");
        if (error is "authorization_pending" or "slow_down")
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        return new DeviceOAuthPollResult { Error = $"MiniMax 登录失败：{error ?? status.ToString()}" };
    }

    public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["audience"] = Audience,
        };
        var (status, body) = await TryPostFormAsync(_site.Account + "/oauth2/token", form, cancellationToken);
        if (status != 200)
        {
            var error = GetString(body, "error");
            if (status is 400 or 401 && error == "invalid_grant")
            {
                // MiniMax 判定「已登出」的唯一形态：invalid_grant + HTTP 400（其余错误视为暂时性）。
                throw new DeviceOAuthRefreshRefusedException("refresh_token 已失效，请重新登录");
            }

            throw new InvalidOperationException($"MiniMax 刷新失败：HTTP {status} {error}");
        }

        var access = GetString(body, "access_token");
        if (string.IsNullOrEmpty(access))
        {
            throw new InvalidOperationException("MiniMax 刷新响应缺少 access_token");
        }

        return new DeviceOAuthTokenSet
        {
            AccessToken = access,
            // MiniMax 可能不回传新 refresh_token，沿用旧值。
            RefreshToken = GetString(body, "refresh_token") ?? refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetNumber(body, "expires_in") ?? 3600),
        };
    }

    public async Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // ① M Plan 窗口：platform coding_plan/remains（Bearer，无需签名）。
        List<AccountQuotaWindow> windows = [];
        string? planError = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _site.Platform + "/v1/api/openplatform/coding_plan/remains");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.AccessToken.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                windows.AddRange(MiniMaxQuotaParser.ParsePlanWindows(body));
            }
            else if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new DeviceOAuthQuotaResult { Success = false, Error = "凭证失效，请重新登录", CredentialInvalid = true };
            }
            else
            {
                planError = $"M Plan 查询 HTTP {(int)response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            planError = "M Plan 查询失败：" + ex.Message;
        }

        // ② 账号信息（套餐等级/积分余额）：agent 签名 API get_membership_info。
        string? planType = null;
        string? balance = null;
        try
        {
            var extra = TryParseExtra(extraJson);
            var userId = extra?.TryGetProperty("realUserId", out var uid) == true && uid.ValueKind == JsonValueKind.String
                ? uid.GetString()
                : null;
            var (status, body) = await SignedAccountCallAsync(
                "/matrix/api/v1/commerce/get_membership_info", token.AccessToken, userId, body: "{}", cancellationToken);
            if (status == 200)
            {
                var parsed = MiniMaxQuotaParser.ParseMembership(body);
                planType = parsed.PlanType;
                balance = parsed.BalanceText;
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("MiniMax membership query failed: {Message}", ex.Message);
        }

        if (windows.Count == 0 && planType is null && balance is null)
        {
            return new DeviceOAuthQuotaResult { Success = false, Error = planError ?? "未获取到任何额度数据" };
        }

        var label = BuildPlanLabel(planType, balance);
        return new DeviceOAuthQuotaResult
        {
            Success = true,
            PlanType = label,
            RawJson = MiniMaxQuotaParser.SerializeSnapshot(label, windows),
            Windows = windows,
        };
    }

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson)
        => MiniMaxQuotaParser.ParseCached(rawJson);

    private static string? BuildPlanLabel(string? planType, string? balance)
    {
        var label = string.IsNullOrWhiteSpace(planType) ? null : planType;
        if (!string.IsNullOrWhiteSpace(balance))
        {
            label = string.IsNullOrWhiteSpace(label) ? $"余额 {balance}" : $"{label} · 余额 {balance}";
        }

        return label;
    }

    private static DeviceOAuthPollResult FromTokenBody(JsonElement body)
    {
        var access = GetString(body, "access_token");
        if (string.IsNullOrEmpty(access))
        {
            return new DeviceOAuthPollResult { Error = "MiniMax 响应缺少 access_token" };
        }

        var expiresIn = GetNumber(body, "expires_in") ?? 2 * 3600;
        return new DeviceOAuthPollResult
        {
            Tokens = new DeviceOAuthTokenSet
            {
                AccessToken = access,
                RefreshToken = GetString(body, "refresh_token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            },
        };
    }

    private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        var (status, body) = await TryPostFormAsync(url, form, ct);
        if (status != 200)
        {
            throw new InvalidOperationException($"MiniMax OAuth HTTP {status}: {GetString(body, "error") ?? GetString(body, "error_description")}");
        }

        return body;
    }

    private async Task<(int Status, JsonElement Body)> TryPostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement body;
        try
        {
            body = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            body = JsonDocument.Parse("{}").RootElement.Clone();
        }

        return ((int)response.StatusCode, body);
    }

    /// <summary>
    /// MiniMax 账号 API 签名调用（对齐 mcode 的 signed()）：查询串带固定设备参数，
    /// 头带 x-timestamp + x-signature（md5(ts+secret+body)）与 yy（md5 链）。
    /// </summary>
    private async Task<(int Status, string Body)> SignedAccountCallAsync(
        string path, string accessToken, string? userId, string? body, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ts = now / 1000;
        var query = new Dictionary<string, string>
        {
            ["device_platform"] = "mcode",
            ["biz_id"] = "3",
            ["app_id"] = "3001",
            ["version_code"] = "22201",
            ["unix"] = now.ToString(),
            ["timezone_offset"] = ((int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes * -1).ToString(),
            ["sys_language"] = _site.Lang,
            ["lang"] = _site.Lang,
            ["device_id"] = "0",
            ["os_name"] = "win32",
            ["browser_name"] = "mcode",
            ["user_id"] = string.IsNullOrWhiteSpace(userId) ? "0" : userId!.Trim(),
            ["client"] = "mcode",
        };
        var queryString = string.Join("&", query.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        // 对齐 mcode signed()：at = 请求路径+查询串整体 encodeURIComponent；yy 与 x-signature 都是 md5 链。
        var at = Uri.EscapeDataString(path + "?" + queryString);
        var signature = body is null
            ? Md5($"{ts}{SignSecret}")
            : Md5($"{ts}{SignSecret}{body}");
        var yy = body is null
            ? Md5($"{at}_{{{Md5(now.ToString())}}}ooui")
            : Md5($"{at}_{body}{Md5(now.ToString())}ooui");

        var url = _site.Agent + path + "?" + queryString;
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "MiniMaxCode");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken.Trim());
        request.Headers.TryAddWithoutValidation("x-timestamp", ts.ToString());
        request.Headers.TryAddWithoutValidation("x-signature", signature);
        request.Headers.TryAddWithoutValidation("yy", yy);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, text);
    }

    private static JsonElement? TryParseExtra(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(extraJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Md5(string input)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static double? GetNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>登录会话状态（不透明，前端只回传）。</summary>
    private sealed class MiniMaxSession
    {
        public string Verifier { get; set; } = string.Empty;
        public string? DeviceCode { get; set; }
        public string? UserCode { get; set; }
        public bool PollByUser { get; set; }
        public int IntervalMs { get; set; } = 5000;
        public DateTimeOffset DeadlineUtc { get; set; }
    }
}
