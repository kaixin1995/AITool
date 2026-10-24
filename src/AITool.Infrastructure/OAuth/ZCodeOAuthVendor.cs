using System.Security.Cryptography;
using System.Text.Json;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// ZCode（智谱 Z.ai / BigModel GLM Coding Plan 账号版）厂商定义，VendorKey=zcode。
/// <para>
/// 授权对齐 ZCode CLI（参照 magpie-community/plugins/packages/zcode）：
/// POST zcode.z.ai/api/v1/oauth/cli/init（provider=zai，随机 Bearer）→ 浏览器打开 authorize_url →
/// GET /api/v1/oauth/cli/poll/{flow_id} 轮询至 ready，取 zai.access_token（JWT，长期有效）。
/// 聊天转发与配额窗口需真实账号验证（mintKey/bizAuth 深链路待接入），
/// v1 先以「登录 + 账号托管」形态接入，ChatSiteConfig=null。
/// </para>
/// </summary>
public sealed class ZCodeOAuthVendor : IDeviceOAuthVendor
{
    private const string ZCodeRoot = "https://zcode.z.ai";

    private readonly HttpClient _httpClient;

    public ZCodeOAuthVendor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string VendorKey => "zcode";

    public string Label => "ZCode（GLM Coding Plan 账号版）";

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; } =
    [
        ("glm-5.3", "GLM 5.3"),
        ("glm-5.3-air", "GLM 5.3 Air"),
    ];

    /// <summary>聊天转发与配额窗口待真实账号验证（mintKey 深链路），暂不支持。</summary>
    public DeviceOAuthChatSiteConfig? ChatSiteConfig => null;

    public async Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var pollBearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var device = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, ZCodeRoot + "/api/v1/oauth/cli/init")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { provider = "zai" }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + pollBearer);
        request.Headers.TryAddWithoutValidation("X-Device", device);

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

        // 回跳地址按 ZCode 客户端的固定形态补齐。
        var url = authorizeUrl;
        const string appVersion = "3.14.0";
        var back = $"{ZCodeRoot}/app/oauth/login?redirect={Uri.EscapeDataString("zcode://oauth/callback")}&app_version={appVersion}";
        var separator = url.Contains('?') ? "&" : "?";
        url += separator + "redirect_uri=" + Uri.EscapeDataString(back);

        return new DeviceOAuthStartResult(url, null, "请在浏览器中完成 Z.ai / BigModel 登录并授权 ZCode", session);
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
            request.Headers.TryAddWithoutValidation("X-Device", session.Device);
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

        string? userId = null;
        string? displayName = null;
        if (body.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            userId = GetString(user, "id") ?? GetString(user, "user_id");
            displayName = GetString(user, "name") ?? GetString(user, "email");
        }

        // zai JWT 长期有效：ExpiresAt 置空表示无需周期刷新。
        return new DeviceOAuthPollResult
        {
            Tokens = new DeviceOAuthTokenSet { AccessToken = accessToken },
            ExternalUserId = userId,
            DisplayName = displayName,
        };
    }

    public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        => throw new DeviceOAuthRefreshRefusedException("ZCode 会话 token 长期有效，无需刷新；若已失效请重新登录");

    public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // 配额窗口需走 mintKey/bizAuth 深链路（待真实账号验证后接入），
        // v1 返回账号在线状态快照，保持巡检链路可用。
        return Task.FromResult(new DeviceOAuthQuotaResult
        {
            Success = true,
            PlanType = "ZCode 已登录",
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
