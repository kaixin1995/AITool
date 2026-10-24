using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// WorkBuddy（腾讯 CodeBuddy 桌面版 Agent 计划）厂商定义（VendorKey=workbuddy-cn / workbuddy-global）。
/// <para>
/// 端点与风控头对齐 workbuddy-manager（ithtelab）与 magpie 社区插件的逆向（同源 workbuddy2api）：
/// 登录为<b>纯轮询式</b>——POST {chat}/v2/plugin/auth/state?platform=CLI 发码（返回 authUrl 扫码页），
/// 轮询 GET {chat}/v2/plugin/auth/token?state= 拿令牌，GET {chat}/v2/plugin/login/account 取账号信息
///（无需本地回调端口，服务器部署天然适配）；刷新走 POST {chat}/v2/plugin/auth/token/refresh，
/// refresh_token 放 <c>X-Refresh-Token</c> 头（Authorization 仍带旧 access，<c>X-Auth-Refresh-Source: plugin</c>
/// 为官方客户端渠道标识）；额度走 billing 域 get-user-resource（个人）与 get-enterprise-user-usage（企业），
/// 余额为积分制（无百分比，窗口只展示套餐到期时刻，不参与自动禁用——空 UsedPercent 的窗口天然被巡检忽略）。
/// 聊天为 OpenAI 兼容（{chat}/v2，X-Agent-Type: main）；每账号稳定设备指纹头
/// （X-Machine-ID/X-Session-ID = sha256("wb2a:{purpose}:{uid}")[:36]）在登录时算好并写入站点头。
/// 已知限制：部分 Agent 的身份行会被 11128「未授权渠道」拒绝（需请求体改写的协议适配器，暂不覆盖）。
/// </para>
/// </summary>
public sealed class WorkBuddyOAuthVendor : IDeviceOAuthVendor
{
    private const string ClientVersion = "5.5.4";
    private const string CliVersion = "2.137.1";

    private sealed record SiteSpec(string Key, string Label, string ChatBase, string BillingBase, string Origin, string Language, string PlatformSegment)
    {
        public static SiteSpec China { get; } = new(
            "workbuddy-cn", "WorkBuddy（腾讯 CodeBuddy）",
            "https://copilot.tencent.com", "https://www.codebuddy.cn",
            "https://www.codebuddy.cn", "zh-CN", "WorkBuddy");

        public static SiteSpec Global { get; } = new(
            "workbuddy-global", "WorkBuddy AI（国际版）",
            "https://www.workbuddy.ai", "https://www.workbuddy.ai",
            "https://www.workbuddy.ai", "en-US", "WorkBuddy AI");
    }

    private readonly HttpClient _httpClient;
    private readonly SiteSpec _site;

    public WorkBuddyOAuthVendor(HttpClient httpClient, bool global = false)
    {
        _httpClient = httpClient;
        _site = global ? SiteSpec.Global : SiteSpec.China;
    }

    public string VendorKey => _site.Key;

    public string Label => _site.Label;

    /// <summary>聊天 UA 三段式（WorkBuddy/&lt;ver&gt; &lt;平台&gt;/&lt;ver&gt; CLI/&lt;cli&gt;，平台段随版本变）。</summary>
    private string ChatUserAgent => $"WorkBuddy/{ClientVersion} {_site.PlatformSegment}/{ClientVersion} CLI/{CliVersion}";

    /// <summary>billing 域 UA 单段式（官方客户端在白名单接口显式覆写）。</summary>
    private const string BillingUserAgent = "WorkBuddy/" + ClientVersion;

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; } =
    [
        ("deepseek-v4.1", "DeepSeek V4.1 (WorkBuddy)"),
        ("kimi-k2.8-preview", "Kimi K2.8 Preview (WorkBuddy)"),
        ("hy4-preview", "Hunyuan HY4 Preview (WorkBuddy)"),
    ];

    /// <summary>聊天站点：OpenAI 兼容 + 官方客户端静态头；每账号头（指纹/身份）由供给器从 ExtraJson.chatHeaders 合并。</summary>
    public DeviceOAuthChatSiteConfig? ChatSiteConfig => new(
        _site.ChatBase + "/v2",
        "OpenAI",
        SupportsOpenAi: true,
        SupportsAnthropic: false,
        SupportsResponses: false,
        new Dictionary<string, string>
        {
            ["User-Agent"] = ChatUserAgent,
            ["X-Agent-Type"] = "main",
            ["X-CodeBuddy-Request"] = "1",
            ["Origin"] = _site.Origin,
            ["Referer"] = _site.Origin + "/",
        });

    public async Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(HttpMethod.Post, _site.ChatBase + "/v2/plugin/auth/state?platform=CLI",
            headers: ChatHeaders(), body: "{}", cancellationToken: cancellationToken);
        if (status != 200)
        {
            throw new InvalidOperationException($"WorkBuddy 发码失败：HTTP {status}");
        }

        var envelope = JsonDocument.Parse(body).RootElement.Clone();
        var (code, data) = UnwrapEnvelope(envelope);
        var state = data.ValueKind == JsonValueKind.Object ? GetString(data, "state") : null;
        var authUrl = data.ValueKind == JsonValueKind.Object ? GetString(data, "authUrl") : null;
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(authUrl))
        {
            throw new InvalidOperationException($"WorkBuddy 发码响应不完整（code={code}）");
        }

        var session = JsonSerializer.Serialize(new WorkBuddySession
        {
            State = state!,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        return new DeviceOAuthStartResult(authUrl!, null, "请在打开的页面扫码授权 WorkBuddy（CodeBuddy）账号", session);
    }

    public async Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<WorkBuddySession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "二维码已过期，请重新发起" };
        }

        // ① 轮询令牌：未授权前业务码非 0 / 无 accessToken → pending。
        var (status, body) = await CallAsync(HttpMethod.Get,
            _site.ChatBase + "/v2/plugin/auth/token?state=" + Uri.EscapeDataString(session.State),
            headers: ChatHeaders(), cancellationToken: cancellationToken);
        if (status != 200)
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        var (_, data) = UnwrapEnvelope(JsonDocument.Parse(body).RootElement.Clone());
        var accessToken = data.ValueKind == JsonValueKind.Object ? GetString(data, "accessToken") : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        // ② 账号信息（uid/昵称/企业 id）。
        string uid;
        string? nickname = null;
        string? enterpriseId = null;
        try
        {
            var (acctStatus, acctBody) = await CallAsync(HttpMethod.Get,
                _site.ChatBase + "/v2/plugin/login/account?state=" + Uri.EscapeDataString(session.State),
                headers: ChatHeaders(accessToken!), cancellationToken: cancellationToken);
            var (_, acct) = acctStatus == 200
                ? UnwrapEnvelope(JsonDocument.Parse(acctBody).RootElement.Clone())
                : (0, default(JsonElement));
            uid = acct.ValueKind == JsonValueKind.Object ? GetString(acct, "uid") ?? string.Empty : string.Empty;
            nickname = acct.ValueKind == JsonValueKind.Object ? GetString(acct, "nickname") : null;
            enterpriseId = acct.ValueKind == JsonValueKind.Object ? GetString(acct, "enterpriseId") : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            uid = string.Empty; // 账号信息失败不阻断登录（uid 影响指纹头，缺失时上游按匿名处理）
        }

        if (string.IsNullOrEmpty(uid))
        {
            return new DeviceOAuthPollResult { IsPending = true, Error = "已授权，正在获取账号信息" };
        }

        // ③ 每账号稳定设备指纹（sha256("wb2a:{purpose}:{uid}")[:36]，跨重启恒定、账号间互异）+ 身份头。
        var chatHeaders = new Dictionary<string, string>
        {
            ["X-Machine-ID"] = DeriveFingerprint("machine", uid),
            ["X-Session-ID"] = DeriveFingerprint("session", uid),
            ["X-User-Id"] = uid,
        };
        var extra = new Dictionary<string, object>
        {
            ["realm"] = _site.Key,
            ["uid"] = uid,
            ["chatHeaders"] = chatHeaders,
        };
        if (!string.IsNullOrWhiteSpace(enterpriseId))
        {
            extra["enterpriseId"] = enterpriseId!;
        }

        var domain = data.ValueKind == JsonValueKind.Object ? GetString(data, "domain") : null;
        if (!string.IsNullOrWhiteSpace(domain))
        {
            extra["domain"] = domain!;
        }

        return new DeviceOAuthPollResult
        {
            Tokens = new DeviceOAuthTokenSet
            {
                AccessToken = accessToken!,
                RefreshToken = GetString(data, "refreshToken"),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetNumber(data, "expiresIn") is > 0 and { } loginSeconds ? loginSeconds : 3600),
            },
            ExternalUserId = uid,
            DisplayName = nickname,
            ExtraJson = JsonSerializer.Serialize(extra),
        };
    }

    /// <summary>刷新：refresh_token 走 X-Refresh-Token 头，Authorization 仍带旧 access（官方客户端形态）。</summary>
    public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(DeviceOAuthTokenSet current, string? extraJson, CancellationToken cancellationToken)
    {
        var refresh = current.RefreshToken?.Trim();
        if (string.IsNullOrWhiteSpace(refresh))
        {
            throw new DeviceOAuthRefreshRefusedException("该账号没有 refreshToken（只能重新扫码登录）");
        }

        var headers = ChatHeaders(string.IsNullOrWhiteSpace(current.AccessToken) ? null : current.AccessToken!.Trim());
        headers["X-Refresh-Token"] = refresh!;
        headers["X-Auth-Refresh-Source"] = "plugin";
        var uid = ReadStringExtra(extraJson, "uid");
        if (!string.IsNullOrWhiteSpace(uid))
        {
            // 指纹头与身份头同发（对齐参考实现 _hdr(realm, token, uid)：uid 已知却不带指纹是风控关联信号）。
            headers["X-User-Id"] = uid!;
            headers["X-Machine-ID"] = DeriveFingerprint("machine", uid!);
            headers["X-Session-ID"] = DeriveFingerprint("session", uid!);
        }

        var enterpriseId = ReadStringExtra(extraJson, "enterpriseId");
        if (!string.IsNullOrWhiteSpace(enterpriseId))
        {
            headers["X-Enterprise-Id"] = enterpriseId!;
        }

        var (status, body) = await CallAsync(HttpMethod.Post,
            _site.ChatBase + "/v2/plugin/auth/token/refresh", headers: headers, body: "{}", cancellationToken: cancellationToken);
        if (status != 200)
        {
            if (status is >= 400 and < 500)
            {
                // 12153/会话死亡等：登录态已失效，重试无意义。
                throw new DeviceOAuthRefreshRefusedException("WorkBuddy 登录态已失效，请重新扫码登录");
            }

            throw new InvalidOperationException($"WorkBuddy 刷新暂时失败：HTTP {status}");
        }

        var envelope = JsonDocument.Parse(body).RootElement.Clone();
        var (code, data) = UnwrapEnvelope(envelope);
        // 两种形态都认：{code,data} 信封或裸 token 对象（对齐参考实现）。
        if (data.ValueKind is not JsonValueKind.Object)
        {
            data = envelope;
        }

        var newAccess = GetString(data, "accessToken");
        if (string.IsNullOrWhiteSpace(newAccess))
        {
            // 业务码 12153（会话死亡）：登录态失效，重试无意义 → 需重登而非无限退避重试。
            if (code == 12153)
            {
                throw new DeviceOAuthRefreshRefusedException("WorkBuddy 登录态已失效（code 12153），请重新扫码登录");
            }

            throw new InvalidOperationException("WorkBuddy 刷新响应缺少 accessToken");
        }

        return new DeviceOAuthTokenSet
        {
            AccessToken = newAccess!,
            // 上游可能不回传新 refreshToken/expiresIn：缺省保留旧值。
            RefreshToken = (data.ValueKind == JsonValueKind.Object ? GetString(data, "refreshToken") : null) ?? refresh,
            ExpiresAt = GetNumber(data, "expiresIn") is > 0 and < 315_360_000 and { } seconds
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : current.ExpiresAt,
        };
    }

    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, string? extraJson, CancellationToken cancellationToken)
        => RefreshTokenAsync(new DeviceOAuthTokenSet { AccessToken = string.Empty, RefreshToken = refreshToken }, extraJson, cancellationToken);

    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        => RefreshTokenAsync(refreshToken, null, cancellationToken);

    public async Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        var enterpriseId = ReadStringExtra(extraJson, "enterpriseId");
        try
        {
            // billing 域头：单段 UA + 身份头（X-User-Id / X-Domain / 指纹）。
            var headers = BillingHeaders(token.AccessToken.Trim(), extraJson);
            if (!string.IsNullOrWhiteSpace(enterpriseId))
            {
                return await QueryEnterpriseQuotaAsync(headers, cancellationToken);
            }

            var body = JsonSerializer.Serialize(new
            {
                PageNumber = 1,
                PageSize = 100,
                ProductCode = "p_tcaca",
                Status = new[] { 0, 3 },
                PackageEndTimeRangeBegin = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                PackageEndTimeRangeEnd = DateTimeOffset.Now.AddYears(101).ToString("yyyy-MM-dd HH:mm:ss"),
            });

            // 国内版只有 /v2 前缀；国际版无前缀优先、404 回落 /v2（对齐 workbuddy-manager billing_paths）。
            string[] paths = _site == SiteSpec.China
                ? ["/v2/billing/meter/get-user-resource"]
                : ["/billing/meter/get-user-resource", "/v2/billing/meter/get-user-resource"];

            foreach (var path in paths)
            {
                var (status, responseBody) = await CallAsync(HttpMethod.Post, _site.BillingBase + path,
                    headers: headers, body: body, cancellationToken: cancellationToken);
                if (status == 404 && path != paths[^1])
                {
                    continue;
                }

                if (status is 401 or 403)
                {
                    return new DeviceOAuthQuotaResult { Success = false, Error = "登录态已失效，请重新扫码", CredentialInvalid = true };
                }

                if (status != 200)
                {
                    return new DeviceOAuthQuotaResult { Success = false, Error = $"额度查询 HTTP {status}" };
                }

                var parsed = WorkBuddyQuotaParser.ParsePersonal(responseBody);
                if (parsed is null)
                {
                    return new DeviceOAuthQuotaResult { Success = false, Error = "额度响应无法解析" };
                }

                return parsed with
                {
                    RawJson = WorkBuddyQuotaParser.SerializeSnapshot(_site.Key, parsed.PlanType, parsed.Windows),
                };
            }

            return new DeviceOAuthQuotaResult { Success = false, Error = "额度端点不可达" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeviceOAuthQuotaResult { Success = false, Error = "额度查询失败：" + ex.Message };
        }
    }

    private async Task<DeviceOAuthQuotaResult> QueryEnterpriseQuotaAsync(Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(HttpMethod.Post,
            _site.BillingBase + "/v2/billing/meter/get-enterprise-user-usage",
            headers: headers, body: "{}", cancellationToken: cancellationToken);
        if (status != 200)
        {
            return status is 401 or 403
                ? new DeviceOAuthQuotaResult { Success = false, Error = "登录态已失效，请重新扫码", CredentialInvalid = true }
                : new DeviceOAuthQuotaResult { Success = false, Error = $"企业额度查询 HTTP {status}" };
        }

        var parsed = WorkBuddyQuotaParser.ParseEnterprise(body);
        if (parsed is null)
        {
            return new DeviceOAuthQuotaResult { Success = false, Error = "企业额度响应无法解析" };
        }

        return parsed with
        {
            RawJson = WorkBuddyQuotaParser.SerializeSnapshot(_site.Key, parsed.PlanType, parsed.Windows),
        };
    }

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson)
        => WorkBuddyQuotaParser.ParseSnapshot(rawJson, _site.Key);

    /// <summary>chat 域通用头（含风控闸门头 X-CodeBuddy-Request 与官方 UA 三段式）。</summary>
    private Dictionary<string, string> ChatHeaders(string? accessToken = null)
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
            ["Accept-Language"] = _site.Language,
            ["X-Requested-With"] = "XMLHttpRequest",
            ["X-CodeBuddy-Request"] = "1",
            ["User-Agent"] = ChatUserAgent,
            ["Origin"] = _site.Origin,
            ["Referer"] = _site.Origin + "/",
        };
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            headers["Authorization"] = "Bearer " + accessToken;
        }

        return headers;
    }

    /// <summary>billing 域头：单段 UA + 账号身份头（指纹/uid/domain）。</summary>
    private Dictionary<string, string> BillingHeaders(string accessToken, string? extraJson)
    {
        var headers = ChatHeaders(accessToken);
        headers["User-Agent"] = BillingUserAgent;
        var uid = ReadStringExtra(extraJson, "uid");
        if (!string.IsNullOrWhiteSpace(uid))
        {
            headers["X-Machine-ID"] = DeriveFingerprint("machine", uid!);
            headers["X-Session-ID"] = DeriveFingerprint("session", uid!);
            headers["X-User-Id"] = uid!;
        }

        var domain = ReadStringExtra(extraJson, "domain");
        if (!string.IsNullOrWhiteSpace(domain))
        {
            headers["X-Domain"] = domain!;
        }

        var enterpriseId = ReadStringExtra(extraJson, "enterpriseId");
        if (!string.IsNullOrWhiteSpace(enterpriseId))
        {
            headers["X-Enterprise-Id"] = enterpriseId!;
            headers["X-Tenant-Id"] = enterpriseId!;
        }

        return headers;
    }

    private async Task<(int Status, string Body)> CallAsync(
        HttpMethod method, string url, Dictionary<string, string> headers, string? body = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return ((int)response.StatusCode, text);
    }

    /// <summary>腾讯统一信封 {code, msg, data}：返回 (code, data)；无信封时按 HTTP 状态处理。</summary>
    private static (int Code, JsonElement Data) UnwrapEnvelope(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var codeEl))
        {
            var code = codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out var c) ? c : -1;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            return (code, data);
        }

        return (-1, default);
    }

    /// <summary>每账号稳定设备指纹（对齐 workbuddy2api deriveAccountStableID：sha256("wb2a:{purpose}:{uid}") 前 36 字符）。</summary>
    private static string DeriveFingerprint(string purpose, string uid)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"wb2a:{purpose}:{uid}"))).ToLowerInvariant()[..36];

    internal static string? ReadStringExtra(string? extraJson, string key)
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
           && (value.ValueKind == JsonValueKind.Number || (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out _)))
            ? value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d
                : double.TryParse(value.GetString(), out var s) ? s : null
            : null;

    private sealed class WorkBuddySession
    {
        public string State { get; set; } = string.Empty;
        public DateTimeOffset DeadlineUtc { get; set; }
    }
}

/// <summary>WorkBuddy 额度解析：个人资源包（Cycle/Capacity 双口径钳位）与企业周期（limit-credit）。</summary>
public static class WorkBuddyQuotaParser
{
    /// <summary>个人套餐：remain 逐项钳位再累加（Cycle 优先、Capacity 回退），到期时刻按 UTC+8 墙钟解析。</summary>
    public static DeviceOAuthQuotaResult? ParsePersonal(string rawJson)
    {
        try
        {
            var root = JsonDocument.Parse(rawJson).RootElement;
            var accounts = FindAccounts(root);
            if (accounts is not { } accountList)
            {
                return null;
            }

            var total = 0.0;
            var windows = new List<AccountQuotaWindow>();
            var index = 0;
            foreach (var item in accountList.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var remain = PackageRemain(item);
                if (remain <= 0) continue;
                total += remain;
                var expiry = ParseUtc8(item, "CycleEndTime") ?? ParseUtc8(item, "EndTime") ?? ParseUtc8(item, "PackageEndTime");
                windows.Add(new AccountQuotaWindow
                {
                    // 积分制无百分比：UsedPercent 留空 → 不参与自动禁用（巡检对空值不判定）。
                    Id = $"wb_pkg_{index}",
                    Label = $"资源包（剩余 {remain:0.##} 积分）",
                    UsedPercent = null,
                    ResetAtUtc = expiry,
                    ResetLabel = expiry?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                });
                index++;
            }

            return new DeviceOAuthQuotaResult
            {
                Success = true,
                PlanType = total > 0 ? $"余额 {total:0.##} 积分" : "无剩余积分",
                Windows = windows,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>企业周期：credit=本周期已用、limitNum=分配额度（-1 不限量），remain = limit - credit。</summary>
    public static DeviceOAuthQuotaResult? ParseEnterprise(string rawJson)
    {
        try
        {
            var root = JsonDocument.Parse(rawJson).RootElement;
            // 严格读信封 data 层（对齐参考实现 data.credit/limitNum；深度搜索会误吞无关嵌套对象的同名字段）。
            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                ? d
                : root;
            if (data.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var used = GetNumber(data, "credit") ?? GetNumber(data, "Credit");
            var limit = GetNumber(data, "limitNum") ?? GetNumber(data, "LimitNum");
            if (used is null || limit is null)
            {
                return null;
            }

            if (limit < 0)
            {
                return new DeviceOAuthQuotaResult { Success = true, PlanType = "企业版（不限量）" };
            }

            var remain = Math.Max(0, limit.Value - used.Value);
            return new DeviceOAuthQuotaResult
            {
                Success = true,
                PlanType = $"企业版（剩余 {remain:0.##} 积分）",
                Windows =
                [
                    new AccountQuotaWindow
                    {
                        Id = "wb_enterprise",
                        Label = $"企业周期（已用 {used:0.##}/{limit:0.##}）",
                        UsedPercent = limit > 0 ? Math.Clamp(100 * used.Value / limit.Value, 0, 100) : null,
                    },
                ],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>落库快照解析（严格校验 vendorKey 标记，防跨厂商误吞）。</summary>
    public static DeviceOAuthQuotaResult? ParseSnapshot(string rawJson, string vendorKey)
    {
        try
        {
            var root = JsonDocument.Parse(rawJson).RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || GetString(root, "vendor") != vendorKey
                || !root.TryGetProperty("windows", out var windowsEl) || windowsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var windows = new List<AccountQuotaWindow>();
            foreach (var w in windowsEl.EnumerateArray())
            {
                if (w.ValueKind != JsonValueKind.Object) continue;
                windows.Add(new AccountQuotaWindow
                {
                    Id = GetString(w, "id") ?? string.Empty,
                    Label = GetString(w, "label") ?? string.Empty,
                    UsedPercent = GetNullableNumber(w, "usedPercent"),
                    ResetAtUtc = GetNullableNumber(w, "resetAtUtc") is { } seconds
                        ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
                        : null,
                    ResetLabel = GetString(w, "resetLabel"),
                });
            }

            return new DeviceOAuthQuotaResult
            {
                Success = true,
                PlanType = GetString(root, "planType"),
                Windows = windows,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>快照序列化（带 vendor 标记）。</summary>
    public static string SerializeSnapshot(string vendorKey, string? planType, IReadOnlyList<AccountQuotaWindow> windows)
        => JsonSerializer.Serialize(new
        {
            vendor = vendorKey,
            planType,
            windows = windows.Select(w => new
            {
                id = w.Id,
                label = w.Label,
                usedPercent = w.UsedPercent,
                resetAtUtc = w.ResetAtUtc?.ToUnixTimeSeconds(),
                w.ResetLabel,
            }),
        });

    private static JsonElement? FindAccounts(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.Clone();
        if (root.ValueKind != JsonValueKind.Object) return null;
        // 对齐参考实现 _extract_resource_accounts：沿 Response/Data/data/response 最多四层下钻，
        // 每层都尝试 Accounts/ResourceAccounts/AccountsList/List；腾讯云风格 data.Response.Accounts 也认。
        var current = root;
        for (var depth = 0; depth < 4; depth++)
        {
            foreach (var name in new[] { "Accounts", "ResourceAccounts", "AccountsList", "List" })
            {
                if (current.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    return list.Clone();
                }
            }

            JsonElement? next = null;
            foreach (var key in new[] { "Response", "Data", "data", "response" })
            {
                if (current.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object)
                {
                    next = nested;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            current = next.Value;
        }

        return null;
    }

    /// <summary>套餐剩余（对齐 workbuddy-manager _package_remain：Cycle 三字段优先并双向钳位，Capacity 回退）。</summary>
    internal static double PackageRemain(JsonElement item)
    {
        var size = GetNumber(item, "CycleCapacitySize") ?? 0;
        if (size > 0)
        {
            var remain = Math.Clamp(GetNumber(item, "CycleCapacityRemain") ?? 0, 0, size);
            var usedFromRemain = size - remain;
            var cycleUsed = GetNumber(item, "CycleCapacityUsed") ?? 0;
            if (cycleUsed > usedFromRemain && size >= cycleUsed)
            {
                remain = size - cycleUsed;
            }

            return remain;
        }

        return Math.Max(0, GetNumber(item, "CapacityRemain") ?? 0);
    }

    /// <summary>UTC+8 墙钟解析（"yyyy-MM-dd HH:mm:ss"，腾讯固定东八区，与本机时区无关）：
    /// 墙钟按 +08:00 偏移构造再转 UTC，得到绝对时刻（西半球/UTC 容器上不漂移）。</summary>
    private static DateTimeOffset? ParseUtc8(JsonElement item, string name)
    {
        var raw = GetString(item, name);
        if (raw is null) return null;
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var wall))
        {
            return new DateTimeOffset(wall, TimeSpan.FromHours(8)).ToUniversalTime();
        }

        return null;
    }

    private static double? FindNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.Number && direct.TryGetDouble(out var d))
        {
            return d;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindNumber(property.Value, name);
                if (nested.HasValue) return nested;
            }
        }

        return null;
    }

    internal static double? GetNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.Number || (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out _)))
            ? value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d
                : double.TryParse(value.GetString(), out var s) ? s : null
            : null;

    private static double? GetNullableNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var d)
            ? d
            : null;

    internal static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
