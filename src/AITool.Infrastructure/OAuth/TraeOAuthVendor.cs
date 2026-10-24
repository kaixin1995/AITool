using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// Trae 厂商定义（VendorKey=trae-cn / trae-global，浏览器授权 + 粘贴回调 + P-256 设备密钥签名续期）。
/// <para>
/// 流程对齐 Trae 官方客户端（参照 magpie-community/plugins/packages/trae，原始逆向自
/// wangqi233/trae2api 与 autumnsentiment/Trae2api-cn）：
/// 浏览器打开 {web}/authorization?…（携带 auth_callback_url=127.0.0.1:17895/authorize 与设备指纹），
/// 登录后回跳地址携带 userJwt/refreshToken，用户整体粘贴回来；
/// 续期走设备绑定形态 POST {auth}/trae/api/v3/oauth/ExchangeToken —— 请求带 DeviceInfo
///（含设备公钥）与 DeviceProof（P-256 ECDSA-SHA256 对
/// "POST\n{path}\n{ClientID}\n{RefreshToken}\n{时间戳}\n{Nonce}" 的 DER 签名），
/// Trae 每次续期轮换 refresh_token（本垂直的不可取消关键段保证旧 token 作废前新 token 落盘）。
/// 设备密钥对（P-256）生成于登录发起时，随 ExtraJson 持久化。
/// 聊天转发走 IDE 专属 SSE 协议（/api/agent/v3/llm_utils_chat），需专用协议适配器——暂不开放。
/// </para>
/// </summary>
public sealed class TraeOAuthVendor : IDeviceOAuthVendor
{
    private sealed record SiteSpec(
        string Key,
        string Label,
        string Web,
        string Auth,
        string ClientId,
        string PluginVersion,
        string IdeVersion,
        int DeviceIdDigits)
    {
        public static SiteSpec China { get; } = new(
            "trae-cn", "Trae CN",
            "https://www.trae.cn", "https://api.trae.cn",
            "ono9krqynydwx5", "2.3.62834", "3.3.99", 16);

        public static SiteSpec Global { get; } = new(
            "trae-global", "Trae Global",
            "https://www.trae.ai", "https://growsg-normal.trae.ai",
            "ono9krqynydwx5", "2.3.62834", "3.5.51", 19);
    }

    /// <summary>续期端点相对路径（DeviceProof 签名覆盖它）。</summary>
    private const string DeviceExchangePath = "/trae/api/v3/oauth/ExchangeToken";

    /// <summary>粘贴回调地址（授权页参数；本机无监听，用户粘贴回跳地址）。</summary>
    private const string CallbackUrl = "http://127.0.0.1:17895/authorize";

    /// <summary>设备绑定续期尝试的客户端形态（对齐插件 deviceExchange.as：SOLO 优先，IDE 兜底）。</summary>
    private static readonly (string ClientId, string Platform, string Version)[] ExchangeIdentities =
    [
        ("en1oxy7wnw8j9n", "SOLO_PC", "0.1.64"),
        ("ono9krqynydwx5", "IDE_PC", "3.3.99"),
    ];

    private readonly HttpClient _httpClient;
    private readonly SiteSpec _site;

    public TraeOAuthVendor(HttpClient httpClient, bool global = false)
    {
        _httpClient = httpClient;
        _site = global ? SiteSpec.Global : SiteSpec.China;
    }

    public string VendorKey => _site.Key;

    public string Label => _site.Label;

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; } =
    [
        ("claude-sonnet-4-5", "Claude Sonnet 4.5 (Trae)"),
        ("doubao-seed-1-6", "Doubao Seed 1.6 (Trae)"),
    ];

    /// <summary>聊天走 IDE 专属 SSE 协议，暂不开放。</summary>
    public DeviceOAuthChatSiteConfig? ChatSiteConfig => null;

    public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        // 设备指纹 + P-256 密钥对（对齐插件 newDevice：CN 16 位数字 id、Global 19 位）。
        var deviceId = RandomDigits(_site.DeviceIdDigits);
        var machineId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var trace = Guid.NewGuid().ToString("N");
        var query = new Dictionary<string, string>
        {
            ["login_version"] = "1",
            ["auth_from"] = "solo",
            ["login_channel"] = "native_ide",
            ["plugin_version"] = _site.PluginVersion,
            ["auth_type"] = "local",
            ["client_id"] = _site.ClientId,
            ["redirect"] = "0",
            ["login_trace_id"] = trace,
            ["auth_callback_url"] = CallbackUrl,
            ["machine_id"] = machineId,
            ["device_id"] = deviceId,
            ["x_device_id"] = deviceId,
            ["x_machine_id"] = machineId,
            ["x_device_brand"] = "ASUS TUF Gaming A15 FA507RM_FA507RM",
            ["x_device_type"] = "windows",
            ["x_os_version"] = "Windows 10 Pro",
            ["x_env"] = string.Empty,
            ["x_app_version"] = _site.IdeVersion,
            ["x_app_type"] = "stable",
            ["hide_saas_login"] = "false",
        };
        var url = _site.Web + "/authorization?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var session = JsonSerializer.Serialize(new TraeSession
        {
            DeviceId = deviceId,
            MachineId = machineId,
            PrivateKeyPem = privateKeyPem,
            PublicKeyPem = publicKeyPem,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        return Task.FromResult(new DeviceOAuthStartResult(
            url, null,
            $"在浏览器完成 {_site.Label} 登录并授权，然后把回跳到 127.0.0.1:17895 的完整地址粘贴回来",
            session));
    }

    public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
        => Task.FromResult(new DeviceOAuthPollResult { IsPending = true, Error = "请粘贴回调地址完成登录" });

    public async Task<DeviceOAuthPollResult> CompleteWithCallbackAsync(string sessionJson, string callbackUrl, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<TraeSession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录已超时，请重新发起" };
        }

        if (!Uri.TryCreate(callbackUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return new DeviceOAuthPollResult { Error = "回调地址无法解析，请粘贴完整的 127.0.0.1:17895/authorize 回跳地址" };
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var error = query.Get("error");
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new DeviceOAuthPollResult { Error = query.Get("error_description") ?? error };
        }

        var userJwt = query.Get("userJwt");
        var refreshToken = query.Get("refreshToken");
        if (string.IsNullOrWhiteSpace(userJwt) && string.IsNullOrWhiteSpace(refreshToken))
        {
            return new DeviceOAuthPollResult { Error = "回调缺少 userJwt/refreshToken，请重新发起登录" };
        }

        var extra = JsonSerializer.Serialize(new
        {
            deviceId = session.DeviceId,
            machineId = session.MachineId,
            devicePrivateKey = session.PrivateKeyPem,
            devicePublicKey = session.PublicKeyPem,
            uid = query.Get("uid") ?? string.Empty,
        });
        return new DeviceOAuthPollResult
        {
            Tokens = new DeviceOAuthTokenSet
            {
                AccessToken = userJwt ?? string.Empty,
                RefreshToken = refreshToken,
                ExpiresAt = JwtExpiry(userJwt),
            },
            ExternalUserId = query.Get("uid"),
            ExtraJson = extra,
        };
    }

    public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, string? extraJson, CancellationToken cancellationToken)
    {
        var extra = TryParseExtra(extraJson);
        var deviceId = ReadString(extra, "deviceId");
        var machineId = ReadString(extra, "machineId");
        var privateKeyPem = ReadString(extra, "devicePrivateKey");
        var publicKeyPem = ReadString(extra, "devicePublicKey");
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(machineId)
            || string.IsNullOrEmpty(privateKeyPem) || string.IsNullOrEmpty(publicKeyPem))
        {
            throw new DeviceOAuthRefreshRefusedException("Trae 设备密钥缺失，请重新登录");
        }

        // 逐个客户端形态尝试（对齐插件 boundExchange），全部被拒才算登录失效。
        string? lastError = null;
        foreach (var (clientId, platform, version) in ExchangeIdentities)
        {
            try
            {
                var result = await BoundExchangeAsync(
                    refreshToken, deviceId!, machineId!, privateKeyPem!, publicKeyPem!,
                    clientId, platform, version, cancellationToken);
                return result;
            }
            catch (TraeExchangeRefusedException ex)
            {
                lastError = ex.Message;
            }
        }

        throw new DeviceOAuthRefreshRefusedException(lastError ?? "Trae refresh_token 已失效，请重新登录");
    }

    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        => throw new DeviceOAuthRefreshRefusedException("Trae 续期需要设备密钥（ExtraJson），请重新登录");

    public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // Trae 额度页面端点形态多变（CN 积分/Global 用量分站点），v1 返回登录态占位，
        // 自动禁用由代理侧分级冷却承接；真实窗口端点待真实账号验证后接入。
        return Task.FromResult(new DeviceOAuthQuotaResult
        {
            Success = true,
            PlanType = $"{_site.Label}（已登录）",
        });
    }

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson)
    {
        try
        {
            var root = JsonDocument.Parse(rawJson).RootElement;
            return root.ValueKind == JsonValueKind.Object && GetString(root, "planType") is { } plan
                ? new DeviceOAuthQuotaResult { Success = true, PlanType = plan }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>设备绑定续期（ExchangeToken + DeviceProof），对齐插件 boundExchange。</summary>
    private async Task<DeviceOAuthTokenSet> BoundExchangeAsync(
        string refreshToken,
        string deviceId,
        string machineId,
        string privateKeyPem,
        string publicKeyPem,
        string clientId,
        string platform,
        string version,
        CancellationToken cancellationToken)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var proofText = $"POST\n{DeviceExchangePath}\n{clientId}\n{refreshToken}\n{timestamp}\n{nonce}";
        var signature = Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(proofText), HashAlgorithmName.SHA256));

        var payload = new Dictionary<string, object?>
        {
            ["ClientID"] = clientId,
            ["RefreshToken"] = refreshToken,
            ["ClientSecret"] = "-",
            ["UserID"] = string.Empty,
            ["DeviceInfo"] = new Dictionary<string, object?>
            {
                ["DeviceID"] = deviceId,
                ["MachineID"] = machineId,
                ["PlatformCode"] = platform,
                ["DeviceType"] = "PC",
                ["DeviceName"] = string.Empty,
                ["DeviceModel"] = string.Empty,
                ["ClientVersion"] = version,
                ["DevicePublicKey"] = publicKeyPem,
                ["DeviceBrand"] = "ASUS TUF Gaming A15 FA507RM_FA507RM",
                ["DeviceCPU"] = "AMD",
                ["OSInfo"] = "Windows",
                ["OSVersion"] = "10.0.22631",
            },
            ["DeviceProof"] = new Dictionary<string, object?>
            {
                ["Signature"] = signature,
                ["Timestamp"] = timestamp,
                ["Nonce"] = nonce,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _site.Auth.TrimEnd('/') + DeviceExchangePath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 400 or 401 or 403 || text.Contains("10101", StringComparison.Ordinal))
            {
                throw new TraeExchangeRefusedException($"Trae 续期被拒（HTTP {(int)response.StatusCode}）");
            }

            throw new InvalidOperationException($"Trae 续期 HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        JsonElement body;
        try
        {
            body = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Trae 续期响应无法解析");
        }

        var result = body.TryGetProperty("Result", out var r) && r.ValueKind == JsonValueKind.Object
            ? r
            : body.TryGetProperty("result", out var r2) && r2.ValueKind == JsonValueKind.Object ? r2 : body;
        var newToken = GetString(result, "Token") ?? GetString(result, "token");
        if (string.IsNullOrWhiteSpace(newToken))
        {
            throw new TraeExchangeRefusedException("Trae 续期响应缺少 Token");
        }

        var newRefresh = GetString(result, "RefreshToken") ?? GetString(result, "refreshToken") ?? refreshToken;
        return new DeviceOAuthTokenSet
        {
            AccessToken = newToken,
            // Trae 每次续期轮换 refresh_token 且作废旧值。
            RefreshToken = newRefresh,
            ExpiresAt = ReadWhen(result, "TokenExpireAt") ?? ReadWhen(result, "tokenExpireAt") ?? JwtExpiry(newToken),
        };
    }

    /// <summary>Trae 过期时间字段：秒、毫秒或 ISO 字符串。</summary>
    private static DateTimeOffset? ReadWhen(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (DateTimeOffset.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }

                break;
            case JsonValueKind.Number when value.TryGetDouble(out var number) && number > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(number < 1e11 ? number * 1000 : number));
        }

        return null;
    }

    /// <summary>Cloud-IDE-JWT 的 exp（无/解析失败返回 null）。</summary>
    private static DateTimeOffset? JwtExpiry(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryParseExtra(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            return JsonDocument.Parse(extraJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? element, string name)
        => element is { } e
           && e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string RandomDigits(int length)
    {
        var digits = new char[length];
        for (var i = 0; i < length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(digits);
    }

    private static string Truncate(string text, int max = 200)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>续期被拒（refresh_token 已被作废），区别于网络/服务器错误。</summary>
    private sealed class TraeExchangeRefusedException(string message) : Exception(message);

    private sealed class TraeSession
    {
        public string DeviceId { get; set; } = string.Empty;
        public string MachineId { get; set; } = string.Empty;
        public string PrivateKeyPem { get; set; } = string.Empty;
        public string PublicKeyPem { get; set; } = string.Empty;
        public DateTimeOffset DeadlineUtc { get; set; }
    }
}
