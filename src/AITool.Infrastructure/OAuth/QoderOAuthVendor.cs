using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// Qoder 厂商定义（国际站 qoder.com / 国内站 qoder.cn，VendorKey=qoder / qoder-cn）。
/// <para>
/// 端点对齐 Qoder CLI（参照 magpie-community/plugins/packages/qoder）：自定义设备授权
///（浏览器打开 /device/selectAccounts，后端轮询 /api/v1/deviceToken/poll）→
/// /api/v1/me/jobToken 换聊天凭证（国际站）或直接用设备 token（国内站 CLI 模式）；
/// 额度走 {openapi}/sash/api/v2/me/usage（设备 token）。
/// 聊天转发需 COSY 动态签名（md5 链 + 体编码），通用转发链路暂不支持——
/// ChatSiteConfig=null，账号先以「额度托管 + 自动禁用」形态接入。
/// </para>
/// </summary>
public sealed class QoderOAuthVendor : IDeviceOAuthVendor
{
    private sealed record SiteSpec(string Key, string Label, string ClientId, string Web, string OpenApi, bool DeviceChat)
    {
        public static SiteSpec International { get; } = new(
            "qoder", "Qoder", "732aef47-9cf2-46a2-95fe-4cebb5d0d1fa",
            "https://qoder.com", "https://openapi.qoder.sh", DeviceChat: false);

        public static SiteSpec China { get; } = new(
            "qoder-cn", "Qoder CN", "e883ade2-e6e3-4d6d-adf7-f92ceff5fdcb",
            "https://qoder.cn", "https://openapi.qoder.com.cn", DeviceChat: true);
    }

    private readonly HttpClient _httpClient;
    private readonly SiteSpec _site;

    public QoderOAuthVendor(HttpClient httpClient, bool china = false)
    {
        _httpClient = httpClient;
        _site = china ? SiteSpec.China : SiteSpec.International;
    }

    public string VendorKey => _site.Key;

    public string Label => _site.Label;

    public static IReadOnlyList<(string Slug, string DisplayName)> Models { get; } =
    [
        ("ultimate", "Qoder Ultimate"),
        ("fast", "Qoder Fast"),
        ("performance", "Qoder Performance"),
        ("efficient", "Qoder Efficient"),
    ];

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => Models;

    /// <summary>聊天转发需 COSY 动态签名，暂不支持（null = 仅额度托管）。</summary>
    public DeviceOAuthChatSiteConfig? ChatSiteConfig => null;

    public async Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["challenge"] = challenge,
            ["challenge_method"] = "S256",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["machine_id"] = Guid.NewGuid().ToString(),
            ["client_id"] = _site.ClientId,
        };
        var url = $"{_site.Web}/device/selectAccounts?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var session = JsonSerializer.Serialize(new QoderSession
        {
            Nonce = query["nonce"],
            Verifier = verifier,
            MachineId = query["machine_id"],
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        return new DeviceOAuthStartResult(url, null, $"请在浏览器中完成 {_site.Label} 登录并授权本设备", session);
    }

    public async Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<QoderSession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录已超时，请重新发起" };
        }

        // ① 轮询设备 token（2 秒间隔）。
        var pollUrl = $"{_site.OpenApi}/api/v1/deviceToken/poll?nonce={Uri.EscapeDataString(session.Nonce)}&verifier={Uri.EscapeDataString(session.Verifier)}&challenge_method=S256";
        JsonElement device;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pollUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DeviceOAuthPollResult { IsPending = true };
            }

            device = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception)
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        var deviceToken = GetString(device, "token");
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        // ② 国际站换 job token；国内站 CLI 模式直接用设备 token。
        string chatToken;
        string? chatRefresh = GetString(device, "refresh_token");
        DateTimeOffset? expires = ParseExpiry(GetNumber(device, "expired_at"), GetNumber(device, "expires_at"));
        try
        {
            using var jobRequest = new HttpRequestMessage(HttpMethod.Post, $"{_site.OpenApi}/api/v1/me/jobToken")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { clientId = _site.ClientId }), System.Text.Encoding.UTF8, "application/json"),
            };
            jobRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            jobRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + deviceToken);
            using var jobResponse = await _httpClient.SendAsync(jobRequest, cancellationToken);
            var jobText = await jobResponse.Content.ReadAsStringAsync(cancellationToken);
            if (jobResponse.IsSuccessStatusCode)
            {
                var job = JsonDocument.Parse(jobText).RootElement.Clone();
                var jobToken = GetString(job, "token");
                if (string.IsNullOrWhiteSpace(jobToken))
                {
                    return new DeviceOAuthPollResult { Error = "jobToken 响应缺少 token" };
                }

                chatToken = jobToken;
                chatRefresh = GetString(job, "refresh_token") ?? chatRefresh;
                expires = ParseExpiry(GetNumber(job, "expired_at"), GetNumber(job, "expires_at"));
            }
            else if (_site.DeviceChat && (int)jobResponse.StatusCode is >= 400 and < 500)
            {
                chatToken = deviceToken;
            }
            else
            {
                return new DeviceOAuthPollResult { Error = $"jobToken HTTP {(int)jobResponse.StatusCode}" };
            }
        }
        catch (Exception ex)
        {
            return new DeviceOAuthPollResult { Error = "jobToken 请求失败：" + ex.Message };
        }

        // ③ 用户信息（邮箱/昵称）。
        string? email = null;
        string? name = null;
        string? userId = GetString(device, "user_id");
        try
        {
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, $"{_site.OpenApi}/api/v1/userinfo");
            userRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            userRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + deviceToken);
            using var userResponse = await _httpClient.SendAsync(userRequest, cancellationToken);
            if (userResponse.IsSuccessStatusCode)
            {
                var user = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                email = GetString(user, "email");
                name = GetString(user, "name");
                userId = GetString(user, "id") ?? GetString(user, "user_id") ?? GetString(user, "uid") ?? userId;
            }
        }
        catch
        {
            // 用户信息失败不阻断登录。
        }

        // tokenSource 记录聊天凭证来源（job=国际站 jobToken / device=国内站设备 token），刷新端点按此分派。
        var tokenSource = chatToken == deviceToken ? "device" : "job";
        var extra = JsonSerializer.Serialize(new { deviceToken, machineId = session.MachineId, tokenSource });
        return new DeviceOAuthPollResult
        {
            Tokens = new DeviceOAuthTokenSet { AccessToken = chatToken, RefreshToken = chatRefresh, ExpiresAt = expires },
            Email = email,
            ExternalUserId = userId,
            DisplayName = name,
            ExtraJson = extra,
        };
    }

    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, string? extraJson, CancellationToken cancellationToken)
        => RefreshCoreAsync(refreshToken, TryGetExtraString(extraJson, "tokenSource"), cancellationToken);

    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        => RefreshCoreAsync(refreshToken, null, cancellationToken);

    private async Task<DeviceOAuthTokenSet> RefreshCoreAsync(string refreshToken, string? tokenSource, CancellationToken cancellationToken)
    {
        // 刷新端点按登录时记录的 tokenSource 分派（job=国际站 jobToken / device=国内站设备 token）；
        // 未记录时保守按设备 token 端点。
        var url = string.Equals(tokenSource, "job", StringComparison.OrdinalIgnoreCase)
            ? $"{_site.OpenApi}/api/v1/jobToken/refresh"
            : $"{_site.OpenApi}/api/v1/deviceToken/refresh";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = refreshToken }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 400 or 401)
            {
                throw new DeviceOAuthRefreshRefusedException("Qoder refresh_token 已失效，请重新登录");
            }

            throw new InvalidOperationException($"Qoder 刷新失败：HTTP {(int)response.StatusCode}");
        }

        var body = JsonDocument.Parse(text).RootElement.Clone();
        var access = GetString(body, "token") ?? GetString(body, "access_token");
        if (string.IsNullOrWhiteSpace(access))
        {
            throw new InvalidOperationException("Qoder 刷新响应缺少 token");
        }

        return new DeviceOAuthTokenSet
        {
            AccessToken = access,
            RefreshToken = GetString(body, "refresh_token") ?? refreshToken,
            ExpiresAt = ParseExpiry(GetNumber(body, "expired_at"), GetNumber(body, "expires_at")),
        };
    }

    public async Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // 额度端点用设备 token（extraJson.deviceToken），无设备 token 时退回聊天 token。
        var deviceToken = TryGetExtraString(extraJson, "deviceToken") ?? token.AccessToken;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_site.OpenApi}/sash/api/v2/me/usage");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + deviceToken.Trim());
            request.Headers.TryAddWithoutValidation("Cosy-ClientType", "10");
            request.Headers.TryAddWithoutValidation("User-Agent", "Qoder");
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

            var parsed = QoderUsageParser.Parse(text);
            if (parsed is null)
            {
                return new DeviceOAuthQuotaResult { Success = false, Error = "额度响应无法解析" };
            }

            return parsed with { RawJson = text };
        }
        catch (Exception ex)
        {
            return new DeviceOAuthQuotaResult { Success = false, Error = "额度查询失败：" + ex.Message };
        }
    }

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => QoderUsageParser.Parse(rawJson);

    private static DateTimeOffset? ParseExpiry(params double?[] values)
    {
        foreach (var value in values)
        {
            if (value is > 0)
            {
                var millis = value < 1e12 ? value.Value * 1000 : value.Value;
                if (millis > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
                }
            }
        }

        // 缺失/已过期的时间戳兜底 2 小时：返回 null 会让刷新循环把该账号当成「永不过期」跳过。
        return DateTimeOffset.UtcNow.AddHours(2);
    }

    private static string? TryGetExtraString(string? extraJson, string name)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            var root = JsonDocument.Parse(extraJson).RootElement;
            return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
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

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class QoderSession
    {
        public string Nonce { get; set; } = string.Empty;
        public string Verifier { get; set; } = string.Empty;
        public string MachineId { get; set; } = string.Empty;
        public DateTimeOffset DeadlineUtc { get; set; }
    }
}

/// <summary>
/// Qoder 额度响应解析：{displayMode, qoderUsage} 信封 → 套餐名 + 积分包窗口
///（userQuota 主积分、addOnQuota 增包、orgResourcePackage 共享包、dedicatedResourcePackages 专属包）。
/// </summary>
public static class QoderUsageParser
{
    public static DeviceOAuthQuotaResult? Parse(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var mode = QoderOAuthVendor.GetString(root, "displayMode");
            if (string.Equals(mode, "enterprise", StringComparison.OrdinalIgnoreCase))
            {
                return new DeviceOAuthQuotaResult { Success = true, PlanType = "Enterprise" };
            }

            if (!string.Equals(mode, "qoder", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("qoderUsage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var plan = GetField(usage, "userType", "user_type")?.GetString();
            var windows = new List<AccountQuotaWindow>();
            AddBucket(GetField(usage, "userQuota", "user_quota"), "Credits", windows);
            AddBucket(GetField(usage, "addOnQuota", "add_on_quota"), "Add-on credits", windows);
            AddBucket(GetField(usage, "orgResourcePackage", "org_resource_package"), "Shared credits", windows);
            if (GetField(usage, "dedicatedResourcePackages", "dedicated_resource_packages") is { ValueKind: JsonValueKind.Array } dedicated)
            {
                foreach (var bucket in dedicated.EnumerateArray())
                {
                    AddBucket(bucket, "Dedicated credits", windows);
                }
            }

            return new DeviceOAuthQuotaResult
            {
                Success = true,
                PlanType = plan,
                Windows = windows,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? GetField(JsonElement element, string a, string b)
    {
        if (element.TryGetProperty(a, out var first) && first.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return first;
        }

        if (element.TryGetProperty(b, out var second) && second.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return second;
        }

        return null;
    }

    private static void AddBucket(JsonElement? bucket, string fallbackName, List<AccountQuotaWindow> windows)
    {
        if (bucket is not { ValueKind: JsonValueKind.Object } b) return;
        var total = GetNumber(b, "total") ?? GetNumber(b, "cap");
        var used = GetNumber(b, "used") ?? (total is { } t ? t - GetNumber(b, "remaining") : null);
        if (total is not > 0 || used is null || used < 0) return;

        var name = QoderOAuthVendor.GetString(b, "name") ?? fallbackName;
        var unit = QoderOAuthVendor.GetString(b, "unit") ?? "credits";
        windows.Add(new AccountQuotaWindow
        {
            Id = "qoder_" + new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(),
            Label = $"{name}（{used:0.##}/{total:0.##} {unit}）",
            UsedPercent = Math.Min(100, 100 * used.Value / total.Value),
        });
    }

    private static double? GetNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;
}
