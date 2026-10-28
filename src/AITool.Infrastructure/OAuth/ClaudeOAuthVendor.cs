using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// Claude Code 订阅账号厂商定义（VendorKey=claude，PKCE 授权码 + 粘贴回调）。
/// <para>
/// 端点为 Claude Code 官方客户端的公开 OAuth（生态内 claude-relay-service 等广泛验证）：
/// authorize https://claude.ai/oauth/authorize（client_id=9d1c250a-…，redirect=localhost:54545/callback），
/// token https://console.anthropic.com/v1/oauth/token（授权码与 refresh 交换；refresh_token 每次轮换）。
/// 聊天走 https://api.anthropic.com 的 Messages（Bearer access_token + oauth beta 头）。
/// 额度窗口为被动信号（429 usage_limit 响应头），主动查询接口不公开——v1 额度返回套餐占位，
/// 自动禁用由代理侧「配额类 429 分级冷却（词表命中 usage_limit_reached）」承接。
/// </para>
/// </summary>
public sealed class ClaudeOAuthVendor : IDeviceOAuthVendor
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string RedirectUri = "http://localhost:54545/callback";
    private const string Scopes = "oauth:openid profile offline_access";

    private readonly HttpClient _httpClient;

    public ClaudeOAuthVendor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string VendorKey => "claude";

    public string Label => "Claude Code 订阅";

    public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; } =
    [
        ("claude-sonnet-4-5", "Claude Sonnet 4.5"),
        ("claude-opus-4-5", "Claude Opus 4.5"),
        ("claude-haiku-4-5", "Claude Haiku 4.5"),
    ];

    public bool SupportsModelFetch => true;

    public DeviceOAuthChatSiteConfig? ChatSiteConfig => new(
        "https://api.anthropic.com",
        "Anthropic",
        SupportsOpenAi: false,
        SupportsAnthropic: true,
        SupportsResponses: false,
        new Dictionary<string, string>
        {
            ["anthropic-version"] = "2023-06-01",
            ["anthropic-beta"] = "oauth-2025-04-20",
            ["X-AITool-Auth-Mode"] = "bearer",
            ["User-Agent"] = "claude-cli/2.0.14 (external, cli)",
            ["x-app"] = "cli",
        });

    public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = $"{AuthorizeUrl}?client_id={ClientId}&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                  + $"&scope={Uri.EscapeDataString(Scopes)}&code_challenge={challenge}&code_challenge_method=S256&state={state}";
        var session = JsonSerializer.Serialize(new ClaudeSession { Verifier = verifier, State = state, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(15) });
        return Task.FromResult(new DeviceOAuthStartResult(
            url, null,
            "在浏览器完成 Claude 登录授权后，把回跳（localhost:54545/callback?code=…）的完整地址粘贴回来",
            session));
    }

    public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
        => Task.FromResult(new DeviceOAuthPollResult { IsPending = true, Error = "请粘贴回调地址完成登录" });

    public async Task<DeviceOAuthPollResult> CompleteWithCallbackAsync(string sessionJson, string callbackUrl, CancellationToken cancellationToken)
    {
        var session = JsonSerializer.Deserialize<ClaudeSession>(sessionJson)
            ?? throw new InvalidOperationException("登录会话无效，请重新发起");
        if (DateTimeOffset.UtcNow >= session.DeadlineUtc)
        {
            return new DeviceOAuthPollResult { IsExpired = true, Error = "登录已超时，请重新发起" };
        }

        if (!Uri.TryCreate(callbackUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return new DeviceOAuthPollResult { Error = "回调地址无法解析，请粘贴完整的 localhost:54545/callback?code=… 地址" };
        }

        var code = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("code");
        var state = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("state");
        if (string.IsNullOrWhiteSpace(code))
        {
            return new DeviceOAuthPollResult { Error = "回调地址缺少 code 参数" };
        }

        if (!string.IsNullOrWhiteSpace(session.State) && !string.Equals(state, session.State, StringComparison.Ordinal))
        {
            return new DeviceOAuthPollResult { Error = "回调 state 不匹配，请重新发起登录" };
        }

        var tokens = await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = session.Verifier,
            ["redirect_uri"] = RedirectUri,
        }, cancellationToken);
        if (tokens is null)
        {
            return new DeviceOAuthPollResult { Error = "授权码换 token 失败（可能已过期，请重新发起）" };
        }

        return new DeviceOAuthPollResult { Tokens = tokens.Value.Item1, Email = tokens.Value.Email, DisplayName = tokens.Value.Email };
    }

    public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        // Claude refresh 每次轮换：发出后必须完整拿回新 refresh_token（本垂直的不可取消关键段保障）。
        var tokens = await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        }, cancellationToken);
        if (tokens is null)
        {
            throw new DeviceOAuthRefreshRefusedException("Claude refresh_token 已失效，请重新登录");
        }

        return tokens.Value.Item1;
    }

    /// <summary>
    /// Reads magpie's actual Claude catalog source (models.dev) without sending the
    /// subscription token. Anthropic does not expose an account-scoped Claude
    /// subscription model list; this is the public vendor catalog, not an account
    /// entitlement check.
    /// </summary>
    public async Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(
        DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://models.dev/api.json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "AITool");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Claude 公共模型目录 HTTP {(int)response.StatusCode}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Claude 公共模型目录 JSON 无法解析", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("anthropic", out var anthropic)
                || anthropic.ValueKind != JsonValueKind.Object
                || !anthropic.TryGetProperty("models", out var catalogModels)
                || catalogModels.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Claude 公共模型目录缺少 anthropic.models 对象");
            }

            var result = new List<DeviceOAuthRemoteModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in catalogModels.EnumerateObject())
            {
                var model = property.Value;
                if (model.ValueKind != JsonValueKind.Object) continue;
                var id = GetString(model, "id") ?? property.Name.Trim();
                if (id.Length == 0 || !seen.Add(id)) continue;
                if (model.TryGetProperty("modalities", out var modalities)
                    && modalities.ValueKind == JsonValueKind.Object
                    && modalities.TryGetProperty("output", out var output)
                    && output.ValueKind == JsonValueKind.Array
                    && !output.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "text"))
                {
                    continue;
                }

                var lowered = id.ToLowerInvariant();
                if (new[] { "embed", "-tts", "image", "audio", "-live", "robotics", "computer-use", "deep-research", "transcribe", "realtime", "moderation", "whisper", "dall-e", "sora" }
                    .Any(excluded => lowered.Contains(excluded, StringComparison.Ordinal)))
                {
                    continue;
                }

                result.Add(new DeviceOAuthRemoteModel(id, GetString(model, "name") ?? id));
            }

            if (result.Count == 0)
            {
                throw new InvalidOperationException("Claude 公共模型目录没有可用文本模型");
            }

            return result;
        }
    }

    public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
    {
        // Claude 订阅额度窗口只在上游 429 响应头中被动给出；主动查询返回占位套餐，
        // 自动禁用由代理侧「配额类 429 分级冷却」承接（usage_limit_reached 命中词表）。
        return Task.FromResult(new DeviceOAuthQuotaResult
        {
            Success = true,
            PlanType = PassivePlanLabel,
        });
    }

    /// <summary>被动额度占位文案（缓存解析只认这一个值，防止误吞其他厂商的快照）。</summary>
    public const string PassivePlanLabel = "Claude 订阅（额度随响应被动采集）";

    public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson)
    {
        try
        {
            var root = JsonDocument.Parse(rawJson).RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && GetString(root, "planType") == PassivePlanLabel
                ? new DeviceOAuthQuotaResult { Success = true, PlanType = PassivePlanLabel }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(DeviceOAuthTokenSet TokenSet, string? Email)?> ExchangeAsync(
        Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.0.14 (external, cli)");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 400 or 401)
                {
                    return null;
                }

                throw new InvalidOperationException($"Claude OAuth HTTP {(int)response.StatusCode}");
            }

            var body = JsonDocument.Parse(text).RootElement.Clone();
            var access = GetString(body, "access_token");
            var refresh = GetString(body, "refresh_token");
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            {
                return null;
            }

            string? email = null;
            if (body.TryGetProperty("id_token", out var idToken) && idToken.ValueKind == JsonValueKind.String)
            {
                email = TryExtractEmailFromJwt(idToken.GetString()!);
            }

            return (new DeviceOAuthTokenSet
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetNumber(body, "expires_in") ?? 3600),
            }, email);
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
            throw new InvalidOperationException("Claude OAuth 请求失败：" + ex.Message, ex);
        }
    }

    private static string? TryExtractEmailFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                ? email.GetString()
                : null;
        }
        catch
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

    private static string Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private sealed class ClaudeSession
    {
        public string Verifier { get; set; } = string.Empty;
        public string? State { get; set; }
        public DateTimeOffset DeadlineUtc { get; set; }
    }
}
