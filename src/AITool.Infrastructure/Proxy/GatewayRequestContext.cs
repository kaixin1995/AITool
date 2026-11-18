using AITool.Application.UsageLogs;

namespace AITool.Infrastructure.Proxy;

public sealed class GatewayRequestContext
{
    public bool Enabled { get; set; }
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
        if (entry.IsFinalResult && entry.Status == "fail") Failed = true;
        Tokens += (long)entry.InputTokens + entry.CachedTokens + entry.OutputTokens;
        decimal? cost;
        try { cost = pricing.CalculateCostUsd(entry.AttemptedModel, entry.RequestedAt, entry.InputTokens, entry.CachedTokens, entry.OutputTokens).CostUsd; }
        catch { Unpriced = true; return; }
        if (cost is { } value) CostUsd += value;
        else if (entry.InputTokens + entry.CachedTokens + entry.OutputTokens > 0) Unpriced = true;
    }
}
