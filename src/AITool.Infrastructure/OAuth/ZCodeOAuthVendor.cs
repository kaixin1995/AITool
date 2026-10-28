using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.OAuth;
using AITool.Application.Sites;
using AITool.Infrastructure.Zhipu;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// ZCode（智谱 GLM Coding Plan 账号版）厂商定义，VendorKey=zcode。
/// <para>
/// 全链路对齐 ZCode 官方客户端（参照 magpie-community/plugins/packages/zcode）：
/// 登录 POST zcode.z.ai/api/v1/oauth/cli/init（provider=zai）→ 浏览器授权 → poll 至 ready 取 zai token；
/// 然后走密钥铸造深链路——POST api.z.ai/api/auth/z/login 换业务 Bearer → GET /api/biz/customer/getCustomerInfo
/// 选「默认机构/默认项目」（无则第一个非 type-2 项目）→ 在该项目的 api_keys 里复用或创建名为
/// zcode-api-key 的密钥并取其 secret，得到 <c>{id}.{secret}</c> 形态的<b>标准 GLM API key</b>。
/// 聊天走 api.z.ai/api/anthropic（Anthropic 协议，Bearer 铸造 key）；
/// 额度与站点密钥版完全同源：GET api.z.ai/api/monitor/usage/quota/limit（裸 key 鉴权，复用智谱解析器，
/// 含团队 ?type=2 回退）——「GLM 主线」的账号版与密钥版在转发/额度两侧口径一致。
/// 铸造 key 长效不过期（ExpiresAt=null，后台不周期刷新）；401 时凭证刷新回调重跑铸造链路换新 key。
/// </para>
/// </summary>
public sealed class ZCodeOAuthVendor : IDeviceOAuthVendor
{
    private const string ZCodeRoot = "https://zcode.z.ai";
    private const string ZaiApi = "https://api.z.ai";
    private const string MintedKeyName = "zcode-api-key";
    private const string UserAgent = "ZCode/" + AppVersion;
    private const string AppVersion = "3.16.3";
    private const string ZaiIndividualCodingPlanId = "account:zai-individual-coding-plan";
    private const string ZaiStartPlanId = "account:zai-start-plan";

    private readonly HttpClient _httpClient;

    public ZCodeOAuthVendor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string VendorKey => "zcode";

    public string Label => "ZCode（GLM Coding Plan 账号版）";

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; } =
    [
        ("GLM-5.3", "GLM 5.3"),
        ("GLM-5.3-Flash", "GLM 5.3 Flash"),
        ("GLM-5.2", "GLM 5.2"),
        ("GLM-5-Turbo", "GLM 5 Turbo"),
    ];

    /// <summary>
    /// 拉取该账号<b>真实套餐</b>在 ZCode 远端 provider 配置里的动态模型目录：
    /// ① configs 端点给出 https 配置 URL → 下载 provider 配置；② 订阅端点判定该铸造 key 的套餐
    /// （读不出即明确失败，不猜测）；③ 只取该套餐 providerId（account:zai-individual-coding-plan /
    /// account:zai-start-plan）的 builtinModelIds + builtinProviderModelRules（剔除 enabled=false）；
    /// ④ 追加账号当前有余额的 gift/体验模型（-Trial 别名，对齐参考插件）。任何一步失败都抛出可读异常，
    /// 绝不回退到静态 DefaultModels 冒充远端目录。
    /// </summary>
    public bool SupportsModelFetch => true;

    public async Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(
        DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // ① 远端 provider 配置（失败即失败：配置拉不到就不给目录，不用静态清单顶替）。
        var config = await FetchProviderConfigAsync(cancellationToken);

        // ② 套餐判定：订阅端点可读且有 VALID 订阅 → 自己的 Coding Plan；
        //    可读但无订阅 → 只能按 Start Plan（gift）路径；读不出 → 明确报无法辨识。
        var ownCodingPlan = await HasOwnCodingPlanAsync(token, cancellationToken);
        var planId = ownCodingPlan ? ZaiIndividualCodingPlanId : ZaiStartPlanId;

        // ③ 该套餐在远端配置里的模型 ID（只取该 providerId，不把其他套餐的 GLM 模型混给账号）。
        var planModelIds = PlanModelIds(config, planId);

        // ④ 账号当前有余额的 gift/体验模型（参考行为：余额读不到只是没有 gift，不影响套餐目录）。
        var giftModels = await FetchLiveGiftModelsAsync(ReadStringExtra(extraJson, "zaiToken"), cancellationToken);

        var result = new List<DeviceOAuthRemoteModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                result.Add(new DeviceOAuthRemoteModel(id, id));
            }
        }

        foreach (var id in planModelIds)
        {
            Add(id);
        }

        foreach (var raw in giftModels)
        {
            if (!ownCodingPlan)
            {
                // 纯 Start Plan 账号：gift 的模型按余额里的名字直接可用（Start 配置常缺最新模型）。
                Add(raw);
            }

            // gift 以 -Trial 体验别名追加（对齐参考 giftID：目录拼写 + "-Trial"）。
            Add(TrialId(raw));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException(ownCodingPlan
                ? $"ZCode 远端配置中没有套餐 {planId} 的可用模型，拒绝回退到静态清单"
                : "该账号既没有 GLM Coding Plan 订阅，也没有可用的 Start Plan 余额，无法给出动态模型目录");
        }

        return result;
    }

    /// <summary>configs 端点 → https 配置 URL → 下载 provider 配置（参考 remoteConfig()）。</summary>
    private async Task<JsonElement> FetchProviderConfigAsync(CancellationToken ct)
    {
        JsonElement data;
        try
        {
            data = await CallAsync(HttpMethod.Get,
                $"{ZCodeRoot}/api/v1/client/configs?app_version={AppVersion}&platform={PlatformId()}", device: NewDevice(), ct: ct);
        }
        catch (DeviceOAuthRefreshRefusedException ex)
        {
            // configs 是公开端点（无需账号凭证），401/403 只能说明端点本身拒绝。
            throw new InvalidOperationException("ZCode 配置端点拒绝了请求，无法拉取动态模型目录：" + ex.Message, ex);
        }

        var url = data.ValueKind == JsonValueKind.Object
                  && data.TryGetProperty("configs", out var configs)
                  && configs.ValueKind == JsonValueKind.Object
            ? GetString(configs, "builtin_provider_config_json")
            : null;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ZCode configs 未返回 provider 配置 URL（builtin_provider_config_json），无法拉取动态模型目录");
        }

        // 配置 URL 是直接 JSON（CDN 发布物），不是 {code,msg,data} 信封。
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ZCode provider 配置下载失败：HTTP {(int)response.StatusCode}");
        }

        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ZCode provider 配置不是有效 JSON", ex);
        }
    }

    /// <summary>订阅端点判定铸造 key 是否有自己的 GLM Coding Plan（参考 plan()：有 VALID 订阅即有）。</summary>
    private async Task<bool> HasOwnCodingPlanAsync(DeviceOAuthTokenSet token, CancellationToken ct)
    {
        // 参考以裸铸造 key 鉴权（与额度 monitor 同形态）。
        var data = await CallAsync(HttpMethod.Get, ZaiApi + "/api/biz/subscription/list",
            auth: token.AccessToken.Trim(), ct: ct);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var subscription in data.EnumerateArray())
        {
            if (GetString(subscription, "status")?.Equals("VALID", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 账号当前<b>有余额</b>的 gift/体验模型（参考 startPlan()：active 且未过期的 plan 下、
    /// 未花完的 balance 桶声明的模型）。余额读不到（任何失败）返回空——gift 是增量，不该卡死目录。
    /// </summary>
    private async Task<IReadOnlyList<string>> FetchLiveGiftModelsAsync(string? zaiToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zaiToken))
        {
            return [];
        }

        try
        {
            var data = await CallAsync(HttpMethod.Get,
                $"{ZCodeRoot}/api/v1/zcode-plan/billing/balance?app_version={AppVersion}",
                auth: "Bearer " + zaiToken.Trim(), device: NewDevice(), ct: ct);
            return ParseLiveGiftModels(data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return [];
        }
    }

    /// <summary>billing/balance → active plan 下未花完桶的模型名（对齐参考 startPlan 的 live models）。</summary>
    internal static IReadOnlyList<string> ParseLiveGiftModels(JsonElement data)
    {
        var live = new List<string>();
        if (data.ValueKind != JsonValueKind.Object)
        {
            return live;
        }

        var now = GetNumber(data, "server_time") is > 0 and { } serverTime
            ? serverTime
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var plans = data.TryGetProperty("plans", out var plansElement) && plansElement.ValueKind == JsonValueKind.Array
            ? plansElement
            : default;
        var balances = data.TryGetProperty("balances", out var balancesElement) && balancesElement.ValueKind == JsonValueKind.Array
            ? balancesElement
            : default;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (plans.ValueKind == JsonValueKind.Array && balances.ValueKind == JsonValueKind.Array)
        {
            foreach (var plan in plans.EnumerateArray())
            {
                if (plan.ValueKind != JsonValueKind.Object
                    || !GetString(plan, "status")?.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
                {
                    continue;
                }

                var endsAt = GetNumber(plan, "ends_at");
                if (endsAt is > 0 && endsAt <= now)
                {
                    continue;
                }

                var planUserPlanId = GetString(plan, "user_plan_id");
                var planPlanId = GetString(plan, "plan_id");
                foreach (var balance in balances.EnumerateArray())
                {
                    if (balance.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var balanceUserPlanId = GetString(balance, "user_plan_id");
                    var balancePlanId = GetString(balance, "plan_id");
                    var same = !string.IsNullOrEmpty(balanceUserPlanId) && !string.IsNullOrEmpty(planUserPlanId)
                        ? string.Equals(balanceUserPlanId, planUserPlanId, StringComparison.Ordinal)
                        : string.Equals(balancePlanId, planPlanId, StringComparison.Ordinal);
                    if (!same)
                    {
                        continue;
                    }

                    var expiresAt = GetNumber(balance, "expires_at");
                    if (expiresAt is > 0 && expiresAt <= now)
                    {
                        continue;
                    }

                    // 花完的桶仍声明模型，但只有未花完的桶现在可花（可用 gift 只取 live）。
                    if (IsSpentBucket(balance))
                    {
                        continue;
                    }

                    foreach (var model in BucketModels(balance))
                    {
                        if (seen.Add(model))
                        {
                            live.Add(model);
                        }
                    }
                }
            }
        }

        return live;
    }

    private static bool IsSpentBucket(JsonElement balance)
    {
        var remaining = GetNumber(balance, "remaining_units");
        if (remaining is not null)
        {
            return remaining <= 0;
        }

        var total = GetNumber(balance, "total_units");
        var used = GetNumber(balance, "used_units");
        return total is not null && used is not null && used >= total;
    }

    /// <summary>balance 桶 capabilities 里的模型名（"model:X" 或无冒号的裸名；对齐参考 bucketModels）。</summary>
    private static List<string> BucketModels(JsonElement balance)
    {
        var models = new List<string>();
        if (!balance.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var capability in capabilities.EnumerateArray())
        {
            var raw = capability.ValueKind == JsonValueKind.String ? capability.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            if (raw.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw["model:".Length..].Trim();
            }
            else if (raw.Contains(':'))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(raw))
            {
                models.Add(raw);
            }
        }

        return models;
    }

    /// <summary>gift 模型的体验别名（对齐参考 giftID：目录内按目录拼写，外加 "-Trial" 后缀）。</summary>
    private string TrialId(string model)
        => DefaultModels.FirstOrDefault(entry => string.Equals(entry.Slug, model, StringComparison.OrdinalIgnoreCase)) is { } canonical
            ? canonical.Slug + "-Trial"
            : model + "-Trial";

    /// <summary>
    /// 套餐 → 模型 ID（参考 modelsOf 的 ID 收集部分）：providerRules 里该 providerId 的 builtinModelIds，
    /// 加上 builtinProviderModelRules 里该 providerId 未禁用的 modelId，剔除 enabled=false；
    /// 大小写不敏感去重、保持出现顺序。context/output/effort 等明细字段在目录契约
    /// （DeviceOAuthRemoteModel 只有 ID 与展示名）中没有承载位，故不解析。
    /// </summary>
    internal static IReadOnlyList<string> PlanModelIds(JsonElement config, string planId)
    {
        var ids = new List<string>();
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("config", out var root)
            || root.ValueKind != JsonValueKind.Object)
        {
            return ids;
        }

        var on = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String
                && element.GetString() is { Length: > 0 } id
                && on.Add(id))
            {
                ids.Add(id);
            }
        }

        if (root.TryGetProperty("providerConfigRules", out var providerConfigRules)
            && providerConfigRules.ValueKind == JsonValueKind.Object
            && providerConfigRules.TryGetProperty("providerRules", out var providerRules)
            && providerRules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in providerRules.EnumerateArray())
            {
                if (rule.ValueKind == JsonValueKind.Object
                    && GetString(rule, "providerId") == planId
                    && rule.TryGetProperty("builtinModelIds", out var builtin)
                    && builtin.ValueKind == JsonValueKind.Array)
                {
                    foreach (var id in builtin.EnumerateArray())
                    {
                        Add(id);
                    }
                }
            }
        }

        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("modelConfigRules", out var modelConfigRules)
            && modelConfigRules.ValueKind == JsonValueKind.Object
            && modelConfigRules.TryGetProperty("builtinProviderModelRules", out var modelRules)
            && modelRules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in modelRules.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object || GetString(rule, "providerId") != planId)
                {
                    continue;
                }

                var modelId = GetString(rule, "modelId");
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    continue;
                }

                var explicitlyDisabled = rule.TryGetProperty("config", out var ruleConfig)
                    && ruleConfig.ValueKind == JsonValueKind.Object
                    && ruleConfig.TryGetProperty("enabled", out var enabled)
                    && enabled.ValueKind == JsonValueKind.False;
                if (explicitlyDisabled)
                {
                    off.Add(modelId);
                }
                else if (on.Add(modelId))
                {
                    ids.Add(modelId);
                }
            }
        }

        return ids.Where(id => !off.Contains(id)).ToList();
    }

    /// <summary>平台标识（对齐参考 platform()：win32-x64 / darwin-arm64 / linux-x64 形态）。</summary>
    private static string PlatformId()
    {
        var os = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        return os + "-" + arch;
    }

    private static string NewDevice() => Guid.NewGuid().ToString();

    /// <summary>聊天站点：api.z.ai 的 Anthropic 端点，Bearer 铸造 key（X-AITool-Auth-Mode=bearer）。</summary>
    public DeviceOAuthChatSiteConfig? ChatSiteConfig => new(
        ZaiApi + "/api/anthropic",
        "Anthropic",
        SupportsOpenAi: false,
        SupportsAnthropic: true,
        SupportsResponses: false,
        new Dictionary<string, string>
        {
            ["anthropic-version"] = "2023-06-01",
            ["User-Agent"] = UserAgent,
            ["X-AITool-Auth-Mode"] = "bearer",
        });

    public async Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var pollBearer = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var device = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, ZCodeRoot + "/api/v1/oauth/cli/init")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { provider = "zai" }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + pollBearer);
        request.Headers.TryAddWithoutValidation("X-Device-Mid", device);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ZCode 登录初始化失败：HTTP {(int)response.StatusCode}");
        }

        var body = JsonDocument.Parse(text).RootElement.Clone();
        var flowId = GetString(body, "flow_id");
        var authorizeUrl = GetString(body, "authorize_url");
        if (string.IsNullOrWhiteSpace(flowId) || string.IsNullOrWhiteSpace(authorizeUrl))
        {
            throw new InvalidOperationException("ZCode 登录响应缺少 flow_id/authorize_url");
        }

        var intervalSec = (int?)GetNumber(body, "poll_interval_sec") ?? 2;
        var expiresAt = GetNumber(body, "expires_at");
        var deadline = expiresAt is > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)expiresAt.Value)
            : DateTimeOffset.UtcNow.AddMinutes(5);

        var session = JsonSerializer.Serialize(new ZCodeSession
        {
            FlowId = flowId,
            PollBearer = pollBearer,
            Device = device,
            IntervalMs = Math.Max(1, intervalSec) * 1000,
            DeadlineUtc = deadline,
        });

        var url = authorizeUrl;
        var back = $"{ZCodeRoot}/app/oauth/login?redirect={Uri.EscapeDataString("zcode://oauth/callback")}&app_version=3.16.3";
        var separator = url.Contains('?') ? "&" : "?";
        url += separator + "redirect_uri=" + Uri.EscapeDataString(back);

        return new DeviceOAuthStartResult(url, null, "请在浏览器中完成 Z.ai 登录并授权 ZCode", session);
    }

    public async Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<ZCodeSession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录已超时，请重新发起" };
        }

        JsonElement body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ZCodeRoot}/api/v1/oauth/cli/poll/{Uri.EscapeDataString(session.FlowId)}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.PollBearer);
            request.Headers.TryAddWithoutValidation("X-Device-Mid", session.Device);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode is >= 400 and < 500 and not (408 or 429)
                    ? new DeviceOAuthPollResult { Error = $"ZCode 登录失败：HTTP {(int)response.StatusCode}" }
                    : new DeviceOAuthPollResult { IsPending = true };
            }

            body = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        var status = GetString(body, "status");
        if (string.IsNullOrEmpty(status) || status == "pending")
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        if (status == "failed")
        {
            return new DeviceOAuthPollResult { IsDenied = true, Error = "授权在网页端被拒绝" };
        }

        if (status != "ready")
        {
            return new DeviceOAuthPollResult { Error = $"ZCode 登录异常状态 {status}" };
        }

        string? accessToken = null;
        if (body.TryGetProperty("zai", out var zai) && zai.ValueKind == JsonValueKind.Object)
        {
            accessToken = GetString(zai, "access_token") ?? GetString(zai, "accessToken");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new DeviceOAuthPollResult { Error = "ZCode 响应缺少 access_token" };
        }

        // 登录即铸造：深链路换出标准 GLM API key（失败给出明确指引，不落半成品账号）。
        try
        {
            var minted = await MintKeyAsync(accessToken!, cancellationToken);
            string? userId = null;
            string? displayName = null;
            if (body.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                userId = GetString(user, "id") ?? GetString(user, "user_id");
                displayName = GetString(user, "name") ?? GetString(user, "email");
            }

            return new DeviceOAuthPollResult
            {
                Tokens = new DeviceOAuthTokenSet { AccessToken = minted.Key, RefreshToken = accessToken },
                ExternalUserId = userId,
                DisplayName = displayName,
                ExtraJson = JsonSerializer.Serialize(new { zaiToken = accessToken, org = minted.Org, project = minted.Project }),
            };
        }
        catch (DeviceOAuthRefreshRefusedException ex)
        {
            return new DeviceOAuthPollResult { Error = ex.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeviceOAuthPollResult { Error = "铸造 GLM API key 失败：" + ex.Message };
        }
    }

    public Task<DeviceOAuthPollResult> CompleteWithCallbackAsync(string sessionJson, string callbackUrl, CancellationToken cancellationToken)
        => Task.FromResult(new DeviceOAuthPollResult { Error = "ZCode 使用轮询登录，无需粘贴回调" });

    /// <summary>「刷新」= 用 zai token 重跑铸造链路（原 key 被删/换项目后换新 key）；铸造 key 本身长效不过期。</summary>
    public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, string? extraJson, CancellationToken cancellationToken)
    {
        // ExtraJson 里有登录时的 zai token 优先（refreshToken 即它）；两者其一可用即可。
        var zaiToken = ReadStringExtra(extraJson, "zaiToken") ?? refreshToken;
        var minted = await MintKeyAsync(zaiToken, cancellationToken);
        return new DeviceOAuthTokenSet
        {
            AccessToken = minted.Key,
            RefreshToken = zaiToken,
            // 铸造 key 长效不过期：置一个远期哨兵驱动「极低频」复铸（一年后自然复核一次）。
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
    }

    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        => RefreshTokenAsync(refreshToken, null, cancellationToken);

    public async Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // 额度与站点密钥版完全同源：monitor 端点 + 裸 key 鉴权 + 智谱解析器（含团队 ?type=2 回退）。
        var key = token.AccessToken.Trim();
        var personal = await QueryMonitorAsync(ZaiApi, key, team: false, ct: cancellationToken);
        if (personal is { Success: true } p && p.Windows.Count > 0)
        {
            return p;
        }

        if (personal is { Success: true } empty && empty.Windows.Count == 0)
        {
            // 个人版无窗口 → 团队版（?type=2；组织/项目头在 ExtraJson 有则带上）。
            var org = ReadStringExtra(extraJson, "org");
            var project = ReadStringExtra(extraJson, "project");
            var team = await QueryMonitorAsync(ZaiApi, key, team: true, org, project, ct: cancellationToken);
            if (team is { Success: true } t && t.Windows.Count > 0)
            {
                return t;
            }
        }

        return personal ?? new DeviceOAuthQuotaResult { Success = false, Error = "额度查询失败" };
    }

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson)
    {
        var parsed = ZhipuQuotaParser.Parse(rawJson);
        if (parsed is null)
        {
            return null;
        }

        if (parsed.Error is not null)
        {
            return new DeviceOAuthQuotaResult
            {
                Success = false,
                Error = parsed.IsKeyRefusal ? "铸造的 API key 被拒绝，请重新登录" : parsed.Error,
                CredentialInvalid = parsed.IsKeyRefusal,
            };
        }

        return new DeviceOAuthQuotaResult
        {
            Success = true,
            PlanType = parsed.Level,
            Windows = parsed.Windows.Select(ToAccountWindow).ToList(),
        };
    }

    /// <summary>智谱站点窗口 → 通用账号窗口。</summary>
    private static AccountQuotaWindow ToAccountWindow(SiteQuotaWindow window) => new()
    {
        Id = window.Id,
        Label = window.Label,
        UsedPercent = window.UsedPercent,
        ResetLabel = window.ResetLabel ?? string.Empty,
        ResetAtUtc = window.ResetAtUtc,
    };

    private async Task<DeviceOAuthQuotaResult?> QueryMonitorAsync(
        string root, string key, bool team, string? org = null, string? project = null, CancellationToken ct = default)
    {
        try
        {
            var url = root + ZhipuQuotaParser.QuotaPath + (team ? "?type=2" : string.Empty);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", key);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en");
            if (team && !string.IsNullOrWhiteSpace(org) && !string.IsNullOrWhiteSpace(project))
            {
                request.Headers.TryAddWithoutValidation("Bigmodel-Organization", org!);
                request.Headers.TryAddWithoutValidation("Bigmodel-Project", project!);
            }

            using var response = await _httpClient.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new DeviceOAuthQuotaResult { Success = false, Error = "铸造的 API key 被拒绝，请重新登录", CredentialInvalid = true };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DeviceOAuthQuotaResult { Success = false, Error = $"额度查询 HTTP {(int)response.StatusCode}" };
            }

            return ParseCachedQuota(text);
        }
        catch (Exception ex)
        {
            return new DeviceOAuthQuotaResult { Success = false, Error = "额度查询失败：" + ex.Message };
        }
    }

    /// <summary>
    /// 密钥铸造深链路：zai token → 业务 Bearer → 客户信息（默认机构/默认项目）→ 复用或创建 zcode-api-key
    /// 并取 secret，得到标准 GLM API key（{id}.{secret}）。
    /// </summary>
    private async Task<(string Key, string Org, string Project)> MintKeyAsync(string zaiToken, CancellationToken ct)
    {
        // ① 业务登录。
        var biz = await CallAsync(HttpMethod.Post, ZaiApi + "/api/auth/z/login",
            auth: "Bearer " + zaiToken,
            body: JsonSerializer.Serialize(new { token = zaiToken }), ct: ct);
        var bizToken = GetString(biz, "access_token");
        if (string.IsNullOrWhiteSpace(bizToken))
        {
            throw new DeviceOAuthRefreshRefusedException("Z.ai 业务登录失败（zai token 已失效，请重新登录）");
        }

        var bizAuth = "Bearer " + bizToken!;

        // ② 客户信息 → 默认机构/默认项目。
        var info = await CallAsync(HttpMethod.Get, ZaiApi + "/api/biz/customer/getCustomerInfo",
            auth: bizAuth, ct: ct);
        string? org = null;
        string? project = null;
        var namedOrg = false;
        if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("organizations", out var organizations)
            && organizations.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in organizations.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object
                    || !o.TryGetProperty("organizationId", out var orgId)
                    || orgId.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? defaultProject = null;
                if (o.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Array)
                {
                    string? firstProject = null;
                    foreach (var p in projects.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Object
                            || !p.TryGetProperty("projectId", out var pid)
                            || pid.ValueKind != JsonValueKind.String
                            || string.IsNullOrEmpty(pid.GetString()))
                        {
                            continue;
                        }

                        if (GetNumber(p, "projectType") is 2)
                        {
                            continue;
                        }

                        firstProject ??= pid.GetString();
                        var pname = GetString(p, "projectName");
                        if (pname?.Contains("默认项目", StringComparison.Ordinal) == true)
                        {
                            defaultProject = pid.GetString();
                        }
                    }

                    defaultProject ??= firstProject;
                }

                if (string.IsNullOrEmpty(defaultProject))
                {
                    continue;
                }

                var isNamed = GetString(o, "organizationName")?.Contains("默认机构", StringComparison.Ordinal) == true;
                if (org is null || (isNamed && !namedOrg))
                {
                    org = orgId.GetString();
                    project = defaultProject;
                    namedOrg = isNamed;
                    if (isNamed)
                    {
                        break;
                    }
                }
            }
        }

        if (org is null || project is null)
        {
            throw new DeviceOAuthRefreshRefusedException("该 Z.ai 账号没有可用于铸造 API key 的项目（无 GLM Coding Plan？）");
        }

        // ③ 项目密钥：复用或创建 zcode-api-key，取 secret。
        var keysUrl = $"{ZaiApi}/api/biz/v1/organization/{Uri.EscapeDataString(org!)}/projects/{Uri.EscapeDataString(project!)}/api_keys";
        var list = await CallAsync(HttpMethod.Get, keysUrl, auth: bizAuth, ct: ct);
        string? keyId = null;
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var k in list.EnumerateArray())
            {
                if (GetString(k, "name") == MintedKeyName
                    && k.TryGetProperty("apiKey", out var apiKey) && apiKey.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(apiKey.GetString()))
                {
                    keyId = apiKey.GetString();
                    break;
                }
            }
        }

        if (keyId is null)
        {
            var created = await CallAsync(HttpMethod.Post, keysUrl,
                auth: bizAuth, body: JsonSerializer.Serialize(new { name = MintedKeyName }), ct: ct);
            keyId = GetString(created, "apiKey");
            if (string.IsNullOrWhiteSpace(keyId))
            {
                throw new DeviceOAuthRefreshRefusedException("Z.ai 拒绝创建 zcode-api-key（账号可能没有 Coding Plan）");
            }
        }

        var copied = await CallAsync(HttpMethod.Get, $"{keysUrl}/copy/{Uri.EscapeDataString(keyId!)}", auth: bizAuth, ct: ct);
        var secret = GetString(copied, "secretKey");
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new DeviceOAuthRefreshRefusedException("Z.ai 未返回 API key 的 secret，请重试登录");
        }

        return ($"{keyId}.{secret}", org!, project!);
    }

    /// <summary>Z.ai 业务 API 调用（{code,msg,data} 信封；非 0 code 抛业务错误）。
    /// zcode.z.ai 域名下的调用带 X-Device-Mid（对齐参考 call()）。</summary>
    private async Task<JsonElement> CallAsync(
        HttpMethod method,
        string url,
        string? auth = null,
        string? body = null,
        string? device = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (auth is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }

        if (url.StartsWith(ZCodeRoot + "/", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Mid", device ?? NewDevice());
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement envelope;
        try
        {
            envelope = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Z.ai 响应不是 JSON：HTTP {(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 401 or 403)
            {
                throw new DeviceOAuthRefreshRefusedException($"Z.ai 拒绝了请求（HTTP {(int)response.StatusCode}，请重新登录）");
            }

            throw new InvalidOperationException($"Z.ai HTTP {(int)response.StatusCode}: {GetString(envelope, "msg")}");
        }

        if (envelope.ValueKind == JsonValueKind.Object
            && envelope.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var codeValue)
            && codeValue is not 0 and not 200)
        {
            throw new InvalidOperationException($"Z.ai 业务错误 code {codeValue}：{GetString(envelope, "msg")}");
        }

        return envelope.TryGetProperty("data", out var data) ? data : default;
    }

    private static string? ReadStringExtra(string? extraJson, string key)
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
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;

    private sealed class ZCodeSession
    {
        public string FlowId { get; set; } = string.Empty;
        public string PollBearer { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public int IntervalMs { get; set; } = 2000;
        public DateTimeOffset DeadlineUtc { get; set; }
    }
}
