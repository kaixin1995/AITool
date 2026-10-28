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
        int DeviceIdDigits,
        string DefaultModelHost,
        string AppId,
        string ClientVersion,
        string ClientVersionCode)
    {
        public static SiteSpec China { get; } = new(
            "trae-cn", "Trae CN",
            "https://www.trae.cn", "https://api.trae.cn",
            "ono9krqynydwx5", "2.3.62834", "3.3.99", 16,
            "https://trae-api-cn.mchost.guru", "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8", "0.1.69", "20260917");

        public static SiteSpec Global { get; } = new(
            "trae-global", "Trae Global",
            "https://www.trae.ai", "https://growsg-normal.trae.ai",
            "ono9krqynydwx5", "2.3.62834", "3.5.51", 19,
            "https://coresg-normal.trae.ai", "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8", "0.1.69", "20260917");

        public bool IsGlobal => Key == "trae-global";
        public IReadOnlyList<string> ModelFunctions { get; } =
            ["chat_v3", "solo_work_lite", "solo_agent", "solo_agent_lite", "solo_coder"];
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

    public bool SupportsModelFetch => true;

    /// <summary>
    /// 拉取账号可用模型目录（对齐 magpie trae 插件 liveModels）：IDE API
    /// /api/ide/v1/batch_get_detail_param 一次取全部 chat function 的列表，
    /// 批量端点没给出任何列表时逐 function /api/ide/v1/get_detail_param 兜底。
    /// 限制：DeviceOAuthRemoteModel 只有 Slug/DisplayName，无法携带「经由哪个
    /// function 请求」与上游 __dev/__max 别名（model_detail_list），这些维度丢弃。
    /// </summary>
    public async Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(
        DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        var extra = TryParseExtra(extraJson);
        var host = ReadString(extra, "modelHost");
        if (string.IsNullOrWhiteSpace(host) || !IsModelHost(host!, _site.IsGlobal))
        {
            host = _site.DefaultModelHost;
        }

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Trae 模型目录请求缺少 access token（Cloud-IDE-JWT）");
        }

        var headers = IdeHeaders(token.AccessToken.Trim(), extra);
        List<(string Fn, List<JsonElement> Entries)> lists;
        try
        {
            lists = await FetchBatchListsAsync(host!, headers, cancellationToken)
                    ?? await FetchPerFunctionListsAsync(host!, headers, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TraeExchangeRefusedException ex)
        {
            throw new InvalidOperationException("Trae 模型目录获取失败：" + ex.Message, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new InvalidOperationException("Trae 模型目录获取失败：" + ex.Message, ex);
        }

        // 逐 function 过滤（对齐插件 chatModel）：custom_model 槽位、
        // usage != chat_completion、config_switch=false、Global 隐藏 is_invisible_to_user。
        var models = new List<DeviceOAuthRemoteModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, entries) in lists)
        {
            foreach (var entry in entries)
            {
                if (!IsChatModel(entry)) continue;
                var id = GetString(entry, "config_name");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id!)) continue;
                var name = EntryName(entry);
                models.Add(new DeviceOAuthRemoteModel(id!, string.IsNullOrWhiteSpace(name) ? id! : name!));
            }
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException("Trae 模型目录接口未返回任何可用模型");
        }

        return models;
    }

    /// <summary>展示名优先级（对齐插件 nameOf）：display_config.display_name → display_name → display_model_name。</summary>
    private static string? EntryName(JsonElement entry)
        => GetString(TryGet(entry, "display_config") ?? default, "display_name")
           ?? GetString(entry, "display_name")
           ?? GetString(entry, "display_model_name");

    /// <summary>批量端点（站点全套 function 一次返回）；任何列表都没有时返回 null 走逐 function 兜底。</summary>
    private async Task<List<(string Fn, List<JsonElement> Entries)>?> FetchBatchListsAsync(
        string host, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["functions"] = _site.ModelFunctions,
            ["agent_type"] = string.Empty,
            ["current_config_info"] = new Dictionary<string, object?> { ["config_name"] = string.Empty, ["is_custom_model"] = false },
            ["mode_type"] = 0,
            ["access_type"] = 0,
            ["ab_force_vids"] = string.Empty,
            ["ab_autotest_advanced_mode"] = 0,
            ["show_custom_model"] = true,
        });
        var text = await SendIdePostAsync(host + "/api/ide/v1/batch_get_detail_param", headers, body, cancellationToken);
        var root = ParseJson(text, "Trae batch_get_detail_param");
        var groups = root.ValueKind == JsonValueKind.Object
                     && (TryGet(root, "function_configs") ?? TryGet(root, "data.function_configs")) is { ValueKind: JsonValueKind.Array } configs
            ? configs
            : default;
        if (groups.ValueKind != JsonValueKind.Array) return null;

        var lists = new List<(string Fn, List<JsonElement> Entries)>();
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            var fn = GetString(group, "function");
            if (string.IsNullOrWhiteSpace(fn) || !_site.ModelFunctions.Contains(fn!, StringComparer.Ordinal)) continue;
            if ((TryGet(group, "config_info_list") ?? TryGet(group, "data.config_info_list")) is not { ValueKind: JsonValueKind.Array } entries) continue;
            var list = entries.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(GetString(item, "config_name")))
                .Select(item => item.Clone())
                .ToList();
            if (list.Count > 0)
            {
                lists.Add((fn!, list));
            }
        }

        return lists;
    }

    /// <summary>逐 function 兜底（IDE 老形态 /api/ide/v1/get_detail_param）。</summary>
    private async Task<List<(string Fn, List<JsonElement> Entries)>> FetchPerFunctionListsAsync(
        string host, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var lists = new List<(string Fn, List<JsonElement> Entries)>();
        var lastError = "无任何 function 返回列表";
        foreach (var fn in _site.ModelFunctions)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["function"] = fn,
                ["config_names"] = null,
                ["need_prompt"] = false,
                ["current_config_info"] = null,
                ["poly_prompt"] = true,
                ["mode_type"] = null,
                ["agent_type"] = null,
            });
            try
            {
                var text = await SendIdePostAsync(host + "/api/ide/v1/get_detail_param", headers, body, cancellationToken);
                var root = ParseJson(text, "Trae get_detail_param");
                if (root.ValueKind != JsonValueKind.Object
                    || (TryGet(root, "config_info_list") ?? TryGet(root, "data.config_info_list")) is not { ValueKind: JsonValueKind.Array } list)
                {
                    continue;
                }

                var entries = list.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(GetString(item, "config_name")))
                    .Select(item => item.Clone())
                    .ToList();
                if (entries.Count > 0)
                {
                    lists.Add((fn, entries));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TraeExchangeRefusedException)
            {
                lastError = ex.Message;
            }
        }

        if (lists.Count == 0)
        {
            throw new InvalidOperationException("Trae get_detail_param 兜底失败：" + lastError);
        }

        return lists;
    }

    private async Task<string> SendIdePostAsync(
        string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 401 or 403
                || text.Contains("\"code\":1001", StringComparison.Ordinal)
                || text.Contains("\"code\":\"1001\"", StringComparison.Ordinal))
            {
                throw new TraeExchangeRefusedException($"Trae 登录已失效，请重新登录（HTTP {(int)response.StatusCode}）");
            }

            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        return text;
    }

    /// <summary>IDE API 请求头（对齐插件 ideHeaders：JWT 三态 + 设备/版本；uid 缺失时置空）。</summary>
    private IReadOnlyDictionary<string, string> IdeHeaders(string token, JsonElement? extra)
    {
        var uid = ReadString(extra, "uid") ?? string.Empty;
        var deviceId = ReadString(extra, "deviceId") ?? string.Empty;
        var machineId = ReadString(extra, "machineId") ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Cloud-IDE-JWT " + token,
            ["X-Cloudide-Token"] = token,
            ["x-ide-token"] = token,
            ["x-uid"] = uid,
            ["x-app-id"] = _site.AppId,
            ["x-device-id"] = deviceId,
            ["x-machine-id"] = machineId,
            ["x-app-version"] = _site.ClientVersion,
            ["x-app-version-code"] = _site.ClientVersionCode,
            ["x-ide-version"] = _site.ClientVersion,
            ["x-ide-version-code"] = _site.ClientVersionCode,
            ["x-ide-version-type"] = "stable",
            ["x-device-cpu"] = "AMD",
            ["x-device-brand"] = "ASUS TUF Gaming A15 FA507RM_FA507RM",
            ["x-device-type"] = "windows",
            ["x-os-version"] = "Windows 10",
            ["x-system-type"] = "Windows",
        };
    }

    /// <summary>模型条目是否为可对话模型（对齐插件 chatModel）。</summary>
    private bool IsChatModel(JsonElement entry)
    {
        var id = GetString(entry, "config_name");
        if (string.IsNullOrWhiteSpace(id) || id!.StartsWith("custom_model", StringComparison.OrdinalIgnoreCase)) return false;
        var usage = GetString(entry, "usage");
        if (!string.IsNullOrWhiteSpace(usage) && !string.Equals(usage, "chat_completion", StringComparison.OrdinalIgnoreCase)) return false;
        if (entry.TryGetProperty("config_switch", out var switchValue) && switchValue.ValueKind == JsonValueKind.False) return false;
        if (entry.TryGetProperty("is_custom_model", out var customValue) && customValue.ValueKind == JsonValueKind.True) return false;
        if (TryGet(entry, "display_config.is_custom_model") is { ValueKind: JsonValueKind.True }) return false;
        // Global 的列表把 Auto 通道与 pay-as-you-go 双子标记为对用户不可见（IDE 选择器同样隐藏）；
        // CN 的列表整单呈现。
        if (entry.TryGetProperty("is_invisible_to_user", out var invisible) && invisible.ValueKind == JsonValueKind.True) return !_site.IsGlobal;
        if (TryGet(entry, "display_config.is_invisible_to_user") is { ValueKind: JsonValueKind.True }) return !_site.IsGlobal;
        return true;
    }

    /// <summary>对象按路径取子元素（"a.b" 形式；缺失/Null 返回 null）。</summary>
    private static JsonElement? TryGet(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next)
                || next.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static JsonElement ParseJson(string text, string what)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{what} 响应无法解析为 JSON", ex);
        }
    }

    /// <summary>模型 host 识别（对齐插件 modelHost：trae.ai/mchost.guru/trae-api- 前缀；CN 另认非 www/api/grow 的 trae.cn）。</summary>
    private static bool IsModelHost(string url, bool global)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        if (global)
        {
            return host == "trae.ai" || host.EndsWith(".trae.ai", StringComparison.OrdinalIgnoreCase);
        }

        if (host == "mchost.guru" || host.EndsWith(".mchost.guru", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("trae-api-", StringComparison.Ordinal))
        {
            return true;
        }

        if (host == "trae.cn" || host.EndsWith(".trae.cn", StringComparison.OrdinalIgnoreCase))
        {
            return !host.StartsWith("www.", StringComparison.Ordinal)
                   && !host.StartsWith("api.", StringComparison.Ordinal)
                   && !host.StartsWith("grow", StringComparison.Ordinal);
        }

        return false;
    }

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

        // 模型 host / region（如有）：userInfo.Host 可能是账号所属网关（如企业租户或
        // US 部署 api-us-east.trae.ai），region（AIRegion）选择 Global 的 US 模型部署。
        // 只收Trae 自己域名下的 host，登录回调当前通常不带——缺失时按 realm 默认网关。
        var host = ReadCallbackHost(query.Get("host") ?? query.Get("api"));
        var region = query.Get("region") ?? query.Get("aiRegion") ?? query.Get("AIRegion");
        var extraMap = new Dictionary<string, string>
        {
            ["deviceId"] = session.DeviceId,
            ["machineId"] = session.MachineId,
            ["devicePrivateKey"] = session.PrivateKeyPem,
            ["devicePublicKey"] = session.PublicKeyPem,
            ["uid"] = query.Get("uid") ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(host)) extraMap["modelHost"] = host!;
        if (!string.IsNullOrWhiteSpace(region)) extraMap["region"] = region!.Trim();
        var extra = JsonSerializer.Serialize(extraMap);
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

    /// <summary>回调里 host/api 参数的trae 域校验（防串入任意地址）。</summary>
    private static string? ReadCallbackHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.Host.ToLowerInvariant();
        var ok = host == "trae.cn" || host.EndsWith(".trae.cn", StringComparison.OrdinalIgnoreCase)
                 || host == "trae.ai" || host.EndsWith(".trae.ai", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".mchost.guru", StringComparison.OrdinalIgnoreCase) || host == "mchost.guru";
        return ok ? uri.GetLeftPart(UriPartial.Authority) : null;
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
            // 只认自己的占位文案（含站点名），防止误吞其他厂商（如 MiniMax）的快照。
            var label = $"{_site.Label}（已登录）";
            return root.ValueKind == JsonValueKind.Object
                   && GetString(root, "planType") == label
                ? new DeviceOAuthQuotaResult { Success = true, PlanType = label }
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
        var signature = Convert.ToBase64String(ecdsa.SignData(
            Encoding.UTF8.GetBytes(proofText),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

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
            // 缺 Token 的 200 可能已消费旧 refresh_token：直接中止（不再试第二个身份），按暂时性错误上报。
            throw new InvalidOperationException("Trae 续期响应缺少 Token");
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
