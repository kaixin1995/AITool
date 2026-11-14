using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.UsageLogs;

namespace AITool.Web.Services;

public sealed class GatewayRequestContext
{
    public bool Enabled { get; set; }
    public string SessionDigest { get; set; } = "";
    public string ParentSessionDigest { get; set; } = "";
    public long Tokens { get; private set; }
    public decimal CostUsd { get; private set; }
    public bool Unpriced { get; private set; }
    public bool Failed { get; private set; }
    public long ResponseBytes { get; set; }
    public void Reset()
    {
        Tokens = 0; CostUsd = 0; Unpriced = false; Failed = false; ResponseBytes = 0;
    }

    /// <summary>从原始请求中提取明确提供的会话标识；不根据正文或随机值推测会话。</summary>
    public void CaptureSession(Guid key, IHeaderDictionary headers, string? body, string? fallbackDigest = null)
    {
        if (!Enabled) return;
        static string? Header(IHeaderDictionary h, string name) => h.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value.FirstOrDefault()) ? value.FirstOrDefault() : null;
        static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
        static string? JsonSession(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return Text(doc.RootElement, "session_id") ?? Text(doc.RootElement, "sessionId");
            }
            catch (JsonException) { return null; }
        }
        var session = Header(headers, "Session-Id") ?? Header(headers, "Thread-Id")
            ?? Header(headers, "X-Session-Id") ?? Header(headers, "X-Conversation-Id")
            ?? Header(headers, "Conversation_id") ?? Header(headers, "X-Claude-Code-Session-Id")
            ?? JsonSession(Header(headers, "X-Codex-Turn-Metadata"));
        if (session is null && !string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                session = Text(root, "prompt_cache_key");
                if (session is null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("metadata", out var metadata))
                    session = JsonSession(Text(metadata, "user_id"));
            }
            catch (JsonException) { /* 请求体校验由协议入口负责。 */ }
        }
        SessionDigest = session is null ? fallbackDigest ?? "" : Digest(key, session);
        ParentSessionDigest = Digest(key, Header(headers, "X-Parent-Session-Id"));
    }
    public void Record(UsageLogEntry entry, AITool.Application.Pricing.IModelPricingService pricing)
    {
        if (!Enabled) return;
        entry.SessionDigest = SessionDigest;
        entry.ParentSessionDigest = ParentSessionDigest;
        if (entry.IsFinalResult && entry.Status == "fail") Failed = true;
        Tokens += (long)entry.InputTokens + entry.CachedTokens + entry.OutputTokens;
        decimal? cost;
        try { cost = pricing.CalculateCostUsd(entry.AttemptedModel, entry.RequestedAt, entry.InputTokens, entry.CachedTokens, entry.OutputTokens).CostUsd; }
        catch { Unpriced = true; return; }
        if (cost is { } value) CostUsd += value;
        else if (entry.InputTokens + entry.CachedTokens + entry.OutputTokens > 0) Unpriced = true;
    }
    public static string Digest(Guid key, string? value) => string.IsNullOrWhiteSpace(value) ? ""
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString("D") + "\n" + value))).ToLowerInvariant();
}
