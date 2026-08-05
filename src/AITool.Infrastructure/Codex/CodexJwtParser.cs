using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AITool.Application.Codex;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// 解析 Codex id_token JWT（无验签，token 来自 TLS 直连可信端点）。
/// 对应 CPA 的 ParseJWTToken（reference-projects/CLIProxyAPI/internal/auth/codex/jwt_parser.go）。
/// </summary>
public static class CodexJwtParser
{
    private static readonly ConcurrentDictionary<string, CodexIdTokenClaims?> Cache = new();

    /// <summary>
    /// 解析 id_token，提取 account_id / email / plan_type / 订阅窗口。失败返回 null（不抛异常）。
    /// 结果按 token 内容缓存（同 id_token 只解析一次）。
    /// </summary>
    public static CodexIdTokenClaims? Parse(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        return Cache.GetOrAdd(idToken, static token =>
        {
            try { return ParseInternal(token); }
            catch { return null; }
        });
    }

    private static CodexIdTokenClaims? ParseInternal(string idToken)
    {
        var segments = idToken.Split('.');
        if (segments.Length < 2) return null;

        var payloadJson = Base64UrlDecode(segments[1]);
        using var doc = JsonDocument.Parse(payloadJson);

        var claims = new CodexIdTokenClaims();

        if (doc.RootElement.TryGetProperty("email", out var emailEl)
            && emailEl.ValueKind == JsonValueKind.String)
        {
            claims.Email = emailEl.GetString();
        }

        // Codex 关键 claim 嵌套在 "https://api.openai.com/auth" 对象下
        if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var authEl)
            && authEl.ValueKind == JsonValueKind.Object)
        {
            claims.AccountId = authEl.TryGetProperty("chatgpt_account_id", out var accEl)
                && accEl.ValueKind == JsonValueKind.String ? accEl.GetString() : null;
            claims.PlanType = authEl.TryGetProperty("chatgpt_plan_type", out var planEl)
                && planEl.ValueKind == JsonValueKind.String ? planEl.GetString() : null;
            claims.UserId = authEl.TryGetProperty("chatgpt_user_id", out var uidEl)
                && uidEl.ValueKind == JsonValueKind.String ? uidEl.GetString() : null;

            claims.SubscriptionWindowStart = TryGetSubscriptionBound(authEl, "subscription_window_start");
            claims.SubscriptionWindowEnd = TryGetSubscriptionBound(authEl, "subscription_window_end");
        }

        return claims;
    }

    private static DateTimeOffset? TryGetSubscriptionBound(JsonElement authEl, string name)
    {
        if (!authEl.TryGetProperty(name, out var el)) return null;
        // 上游该字段可能是 unix 秒（数字）或 ISO 字符串
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        if (el.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(el.GetString(), out var dto))
            return dto;
        return null;
    }

    /// <summary>
    /// base64url 解码（自动补 padding）。
    /// </summary>
    public static string Base64UrlDecode(string input)
    {
        var bytes = Base64UrlDecodeBytes(input);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// base64url 解码为字节（自动补 padding）。
    /// </summary>
    public static byte[] Base64UrlDecodeBytes(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
        return Convert.FromBase64String(s);
    }
}
