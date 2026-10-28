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
    private sealed record SiteSpec(string Key, string Label, string ClientId, string Web, string OpenApi, string Api, bool DeviceChat)
    {
        public static SiteSpec International { get; } = new(
            "qoder", "Qoder", "732aef47-9cf2-46a2-95fe-4cebb5d0d1fa",
            "https://qoder.com", "https://openapi.qoder.sh", "https://api3.qoder.sh", DeviceChat: false);

        public static SiteSpec China { get; } = new(
            "qoder-cn", "Qoder CN", "e883ade2-e6e3-4d6d-adf7-f92ceff5fdcb",
            "https://qoder.cn", "https://openapi.qoder.com.cn", "https://gateway.qoder.com.cn", DeviceChat: true);
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

    public bool SupportsModelFetch => true;

    /// <summary>
    /// 拉取账号可用模型目录：GET {api}/algo/api/v2/model/list?Encode=1，COSY 签名头
    ///（对齐 qoder 插件 cosyHeaders/fetchListing）。响应为普通 JSON（app/chat 两个场景数组），
    /// app 场景优先、chat 补缺；只保留 enable 且 key 非 auto/default/空的条目。
    /// </summary>
    public async Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(
        DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        var uid = TryGetExtraString(extraJson, "uid");
        var name = TryGetExtraString(extraJson, "name");
        var email = TryGetExtraString(extraJson, "email");
        var machineId = TryGetExtraString(extraJson, "machineId");
        if (string.IsNullOrWhiteSpace(uid))
        {
            throw new InvalidOperationException("Qoder 账号缺少 uid（旧登录未记录，请重新登录）");
        }

        if (string.IsNullOrWhiteSpace(machineId))
        {
            throw new InvalidOperationException("Qoder 账号缺少 machineId（请重新登录）");
        }

        var url = _site.Api + ModelsPath;
        var headers = CosyHeaders(
            url, uid!, name ?? string.Empty, email ?? string.Empty, token.AccessToken.Trim(), machineId!,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (headerName, headerValue) in headers)
        {
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }

        string text;
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Qoder 模型目录请求失败：HTTP {(int)response.StatusCode}: {Truncate(text)}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("Qoder 模型目录请求失败：" + ex.Message, ex);
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Qoder 模型目录响应无法解析为 JSON", ex);
        }

        return ParseModelListing(root);
    }

    /// <summary>
    /// 场景合并 + 过滤（对齐插件 sceneModels/modelInfos）：app 场景优先原序，chat 独有条目
    /// 按其原相对位置补在 app 对应条目之后；过滤 enable=false 与 key 为 auto/default/空。
    /// </summary>
    internal static IReadOnlyList<DeviceOAuthRemoteModel> ParseModelListing(JsonElement root)
    {
        var merged = new List<JsonElement>();
        var app = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("app", out var appValue) && appValue.ValueKind == JsonValueKind.Array
            ? appValue
            : default;
        var chat = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("chat", out var chatValue) && chatValue.ValueKind == JsonValueKind.Array
            ? chatValue
            : default;
        if (app.ValueKind == JsonValueKind.Array)
        {
            merged.AddRange(app.EnumerateArray());
        }

        var insertedAfter = -1;
        if (chat.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in chat.EnumerateArray())
            {
                var key = GetString(entry, "key");
                var index = merged.FindIndex(existing => GetString(existing, "key") == key);
                if (index >= 0)
                {
                    insertedAfter = index;
                    continue;
                }

                merged.Insert(++insertedAfter, entry.Clone());
            }
        }

        var models = new List<DeviceOAuthRemoteModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in merged)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("enable", out var enableValue) && enableValue.ValueKind != JsonValueKind.True) continue;
            var key = GetString(entry, "key");
            if (string.IsNullOrWhiteSpace(key) || key is "auto" or "default") continue;
            if (!seen.Add(key!)) continue;
            var displayName = GetString(entry, "display_name") ?? key!;
            models.Add(new DeviceOAuthRemoteModel(key!, displayName));
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException("Qoder 模型目录未返回任何可用模型");
        }

        return models;
    }

    private const string ModelsPath = "/algo/api/v2/model/list?Encode=1";

    /// <summary>信封声称的 cosy 构建（必须与桌面 app 对话用的版本一致，见 qoder 插件 COSY_VERSION）。</summary>
    private const string CosyVersion = "1.1.69";

    /// <summary>
    /// Qoder 客户端内置 RSA 公钥（SPKI，来自 cosy 源码），包装每次请求的 AES 密钥。
    /// 逐字取自 magpie qoder 插件 index.mjs 的 RSA_KEY，勿改动。
    /// </summary>
    private const string RsaPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDA8iMH5c02LilrsERw9t6Pv5Nc
        4k6Pz1EaDicBMpdpxKduSZu5OANqUq8er4GM95omAGIOPOh+Nx0spthYA2BqGz+l
        6HRkPJ7S236FZz73In/KVuLnwI8JJ2CbuJap8kvheCCZpmAWpb/cPx/3Vr/J6I17
        XcW+ML9FoCI6AOvOzwIDAQAB
        -----END PUBLIC KEY-----
        """;

    /// <summary>COSY 签名头：随机 AES-128-CBC 密钥（16 位 ASCII hex，自身即 IV）加密账号信息，
    /// RSA PKCS#1 包装密钥，MD5(payload\nkey\nts\nbody\npath) 签名（对齐插件 cosyHeaders/userBlob）。</summary>
    private static IReadOnlyDictionary<string, string> CosyHeaders(
        string url, string uid, string name, string email, string access, string machineId, long timestamp)
    {
        var raw = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["uid"] = uid,
            ["aid"] = string.Empty,
            ["name"] = name ?? string.Empty,
            ["email"] = email ?? string.Empty,
            ["security_oauth_token"] = access,
        });
        var aesKey = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")[..16]);
        string info;
        using (var aes = Aes.Create())
        {
            aes.KeySize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey;
            aes.IV = aesKey;
            using var encryptor = aes.CreateEncryptor();
            info = Convert.ToBase64String(encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(raw), 0, raw.Length));
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(RsaPublicKeyPem);
        var wrappedKey = Convert.ToBase64String(rsa.Encrypt(aesKey, RSAEncryptionPadding.Pkcs1));

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = "v1",
            ["requestId"] = Guid.NewGuid().ToString("N"),
            ["info"] = info,
            ["cosyVersion"] = CosyVersion,
            ["ideVersion"] = string.Empty,
        });
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var path = new Uri(url).AbsolutePath;
        if (path.StartsWith("/algo", StringComparison.Ordinal)) path = path[5..];
        var wireBody = string.Empty;
        var signature = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes($"{payload}\n{wrappedKey}\n{timestamp}\n{wireBody}\n{path}"))).ToLowerInvariant();

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
            ["Accept-Encoding"] = "identity",
            ["Content-Type"] = "application/json",
            ["Authorization"] = $"Bearer COSY.{payload}.{signature}",
            ["Cosy-Business-Product"] = "app",
            ["Cosy-Business-Type"] = "agent",
            ["Cosy-ClientIp"] = machineId,
            ["Cosy-ClientType"] = "10",
            ["Cosy-Data-Policy"] = "disagree",
            ["Cosy-Date"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cosy-Key"] = wrappedKey,
            ["Cosy-MachineId"] = machineId,
            ["Cosy-MachineToken"] = machineId,
            ["Cosy-MachineType"] = "5",
            ["Cosy-MachineOS"] = MachineOs(),
            ["Cosy-Scene"] = "app",
            ["Cosy-User"] = uid,
            ["Cosy-Version"] = CosyVersion,
            ["Login-Version"] = "v2",
        };
    }

    /// <summary>机器名形态（对齐插件 machineOS：{arch}_{platform}，如 x86_64_windows）。</summary>
    private static string MachineOs()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            _ => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
        var platform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        return arch + "_" + platform;
    }

    private static string Truncate(string text, int max = 512)
        => text.Length <= max ? text : text[..max] + "…";

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
        // uid/name/email 随账号持久化：COSY 签名信封要加密携带它们（模型目录/聊天必需）。
        var tokenSource = chatToken == deviceToken ? "device" : "job";
        var extra = JsonSerializer.Serialize(new
        {
            deviceToken,
            machineId = session.MachineId,
            tokenSource,
            uid = userId ?? string.Empty,
            name = name ?? string.Empty,
            email = email ?? string.Empty,
        });
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
