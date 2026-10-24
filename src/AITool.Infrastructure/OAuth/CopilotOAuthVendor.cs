using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// GitHub Copilot 厂商定义（VendorKey=copilot）。
/// <para>
/// 端点对齐 Copilot 编辑器官方客户端（参照 magpie internal/provider/copilot_*.go）：
/// GitHub 设备码流（client_id=Iv1.b507a08c87ecfe98，scope=read:user）→ 长期 GitHub OAuth token；
/// 聊天前经 GET api.github.com/copilot_internal/v2/token 换约 30 分钟的 Copilot 会话令牌
///（本垂直把「换会话令牌」建模为 token 刷新：RefreshToken=GitHub token，AccessToken=Copilot token，
/// 后台刷新服务按过期时间自动重换，隐藏 Site 始终持有可用 Bearer）。
/// 聊天为 OpenAI 兼容 /chat/completions（api.githubcopilot.com；个人版端点以 v2/token 返回为准，
/// 存入 ExtraJson.chatBaseUrl 供供给器覆盖站点地址）。
/// </para>
/// </summary>
public sealed class CopilotOAuthVendor : IDeviceOAuthVendor
{
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string DefaultChatBase = "https://api.githubcopilot.com";

    private readonly HttpClient _httpClient;

    public CopilotOAuthVendor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string VendorKey => "copilot";

    public string Label => "GitHub Copilot";

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; } =
    [
        ("gpt-5.2", "GPT 5.2 (Copilot)"),
        ("claude-sonnet-4.5", "Claude Sonnet 4.5 (Copilot)"),
        ("gemini-3-pro", "Gemini 3 Pro (Copilot)"),
    ];

    /// <summary>
/// Copilot 会话令牌约 30 分钟。刷新扫描每 5 分钟一轮，提前量必须大于扫描间隔，
/// 否则会出现「上一轮未到点、下一轮已过期」的空档（T-7 分钟刷新是安全下限）。
/// </summary>
    public TimeSpan RefreshLead => TimeSpan.FromMinutes(8);

    public DeviceOAuthChatSiteConfig? ChatSiteConfig => new(
        DefaultChatBase,
        "OpenAI",
        SupportsOpenAi: true,
        SupportsAnthropic: false,
        SupportsResponses: false,
        new Dictionary<string, string>
        {
            ["Editor-Version"] = "vscode/1.140.0",
            ["Editor-Plugin-Version"] = "copilot-chat/0.68.0",
            ["Copilot-Integration-Id"] = "vscode-chat",
            ["User-Agent"] = "GitHubCopilotChat/0.68.0",
            ["X-GitHub-Api-Version"] = "2026-01-09",
        });

    public async Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var body = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user",
        }, cancellationToken);
        var userCode = GetString(body, "user_code");
        var deviceCode = GetString(body, "device_code");
        var uri = GetString(body, "verification_uri");
        if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(uri))
        {
            throw new InvalidOperationException("GitHub 设备码响应不完整");
        }

        var session = JsonSerializer.Serialize(new CopilotSession
        {
            DeviceCode = deviceCode,
            IntervalMs = (int?)GetNumber(body, "interval") ?? 5000,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds((int?)GetNumber(body, "expires_in") ?? 900),
        });
        return new DeviceOAuthStartResult(uri, userCode, $"请在 github.com/login/device 输入用户码 {userCode}", session);
    }

    public async Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<CopilotSession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录码已过期，请重新发起" };
        }

        var body = await PostFormAsync(TokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["device_code"] = session.DeviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        }, cancellationToken);
        var error = GetString(body, "error");
        if (error == "authorization_pending" || error == "slow_down")
        {
            return new DeviceOAuthPollResult { IsPending = true };
        }

        if (error == "access_denied")
        {
            return new DeviceOAuthPollResult { IsDenied = true, Error = "用户拒绝了授权" };
        }

        if (error == "expired_token")
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录码已过期，请重新发起" };
        }

        var githubToken = GetString(body, "access_token");
        if (string.IsNullOrEmpty(githubToken))
        {
            return new DeviceOAuthPollResult { Error = "GitHub 响应缺少 access_token" };
        }

        // 换一次 Copilot 会话令牌，拿到账号名与个人版聊天端点。
        var exchange = await ExchangeAsync(githubToken, cancellationToken);
        if (exchange is not { } ex)
        {
            return new DeviceOAuthPollResult { Error = "Copilot 会话令牌交换失败（该 GitHub 账号可能没有 Copilot 订阅）" };
        }

        return new DeviceOAuthPollResult
        {
            Tokens = new DeviceOAuthTokenSet
            {
                AccessToken = ex.Token,
                RefreshToken = githubToken,
                ExpiresAt = ex.ExpiresAt,
            },
            ExternalUserId = ex.Login,
            ExtraJson = JsonSerializer.Serialize(new { chatBaseUrl = ex.Api }),
        };
    }

    /// <summary>「刷新」= 用 GitHub token 重换 Copilot 会话令牌（会话令牌约 30 分钟）。</summary>
    public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var exchange = await ExchangeAsync(refreshToken, cancellationToken)
            ?? throw new DeviceOAuthRefreshRefusedException("Copilot 会话令牌交换被拒绝，请重新登录");
        return new DeviceOAuthTokenSet
        {
            AccessToken = exchange.Token,
            RefreshToken = refreshToken,
            ExpiresAt = exchange.ExpiresAt,
        };
    }

    public async Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // 额度走 GitHub 侧 copilot_internal/user（GitHub token = RefreshToken；无则用会话令牌尝试）。
        var githubToken = token.RefreshToken ?? token.AccessToken;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + githubToken.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.68.0");
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

            var parsed = CopilotUsageParser.Parse(text);
            return parsed ?? new DeviceOAuthQuotaResult { Success = false, Error = "额度响应无法解析" };
        }
        catch (Exception ex)
        {
            return new DeviceOAuthQuotaResult { Success = false, Error = "额度查询失败：" + ex.Message };
        }
    }

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => CopilotUsageParser.Parse(rawJson);

    /// <summary>用 GitHub token 换 Copilot 会话令牌（含个人版聊天端点与登录名）。</summary>
    private async Task<(string Token, DateTimeOffset ExpiresAt, string? Login, string? Api)?> ExchangeAsync(
        string githubToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + githubToken.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.68.0");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 401 or 403)
                {
                    // 只有明确的鉴权拒绝才算「需重登」；5xx/限流是暂时性错误，重试可恢复。
                    throw new DeviceOAuthRefreshRefusedException("GitHub 拒绝了该 token，请重新登录");
                }

                throw new InvalidOperationException($"Copilot 令牌交换暂时失败：HTTP {(int)response.StatusCode}");
            }

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            var copilotToken = GetString(body, "token");
            if (string.IsNullOrEmpty(copilotToken))
            {
                throw new InvalidOperationException("Copilot 交换响应缺少 token");
            }

            var expiresAt = GetNumber(body, "expires_at") is { } seconds
                ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
                : DateTimeOffset.UtcNow.AddMinutes(25);
            string? api = null;
            if (body.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Object)
            {
                api = GetString(endpoints, "api");
            }

            var login = GetString(body, "login");
            return (copilotToken, expiresAt, login, api);
        }
        catch (DeviceOAuthRefreshRefusedException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // 暂时性错误（5xx/缺 token）必须上抛为可重试——被下面的兜底吞掉会转换成
            // RefreshRefused，把账号错标 RequiresReauth 砖掉。
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Copilot 令牌交换请求失败：" + ex.Message, ex);
        }
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
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"GitHub OAuth 响应无法解析：{Truncate(text)}");
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

    private sealed class CopilotSession
    {
        public string DeviceCode { get; set; } = string.Empty;
        public int IntervalMs { get; set; } = 5000;
        public DateTimeOffset DeadlineUtc { get; set; }
    }
}

/// <summary>Copilot 额度解析：quota_snapshots 各桶（chat / premium_interactions / completions）剩余百分比。</summary>
public static class CopilotUsageParser
{
    public static DeviceOAuthQuotaResult? Parse(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("quota_snapshots", out var snapshots)
                || snapshots.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var windows = new List<AccountQuotaWindow>();
            foreach (var bucket in snapshots.EnumerateObject())
            {
                if (bucket.Value.ValueKind != JsonValueKind.Object
                    || !bucket.Value.TryGetProperty("percent_remaining", out var remaining)
                    || !remaining.TryGetDouble(out var percent))
                {
                    continue;
                }

                var label = bucket.Name switch
                {
                    "chat" => "Chat 额度",
                    "premium_interactions" => "Premium 交互额度",
                    "completions" => "补全额度",
                    _ => bucket.Name,
                };
                windows.Add(new AccountQuotaWindow
                {
                    Id = "copilot_" + bucket.Name,
                    Label = label,
                    UsedPercent = Math.Clamp(100 - percent, 0, 100),
                });
            }

            if (windows.Count == 0)
            {
                return null;
            }

            return new DeviceOAuthQuotaResult
            {
                Success = true,
                PlanType = "GitHub Copilot",
                Windows = windows,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
