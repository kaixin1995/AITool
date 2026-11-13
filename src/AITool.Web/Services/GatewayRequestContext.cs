using System.Security.Cryptography;
using System.Text;
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
