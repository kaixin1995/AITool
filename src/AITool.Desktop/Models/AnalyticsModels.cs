using System.Globalization;
using System.Text.Json.Serialization;

namespace AITool.Desktop.Models;

public sealed class AnalyticsFilterOptions
{
    public List<AnalyticsSiteOption> Sites { get; set; } = new();
    public List<AnalyticsModelOption> Models { get; set; } = new();
    public List<AnalyticsAccessKeyOption> AccessKeys { get; set; } = new();
}

public sealed class AnalyticsSiteOption
{
    public string SiteId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
}

public sealed class AnalyticsModelOption
{
    public string ModelName { get; set; } = string.Empty;
}

public sealed class AnalyticsAccessKeyOption
{
    public string AccessKeyId { get; set; } = string.Empty;
    public string AccessKeyLabel { get; set; } = string.Empty;
}

public sealed class AnalyticsSelectOption
{
    public AnalyticsSelectOption(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }
    public string Label { get; }
}

public sealed class AnalyticsDashboardResponse
{
    public string? Status { get; set; }
    public int? RetryAfterMs { get; set; }
    public string? Message { get; set; }
    public AnalyticsAppliedFilter? AppliedFilter { get; set; }
    public AnalyticsSummary? Summary { get; set; }
    public List<AnalyticsTrendPoint> RequestTrend { get; set; } = new();
    public List<AnalyticsResultTrendPoint> ResultTrend { get; set; } = new();
    public List<AnalyticsTokenTrendPoint> TokenTrend { get; set; } = new();
    public List<AnalyticsDurationTrendPoint> DurationTrend { get; set; } = new();
    public List<AnalyticsFallbackTrendPoint> FallbackTrend { get; set; } = new();
    public List<AnalyticsDistributionPoint> SiteDistribution { get; set; } = new();
    public List<AnalyticsDistributionPoint> ModelDistribution { get; set; } = new();
    public List<AnalyticsCacheRatioPoint> ModelCacheRatioDistribution { get; set; } = new();
    public List<AnalyticsBreakdownPoint> SourceBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> AccessKeyBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> ProtocolBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> FailureReasonBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> StatusCodeBreakdown { get; set; } = new();
    public List<AnalyticsFallbackChainPoint> FallbackChainDistribution { get; set; } = new();
    public AnalyticsLatencyPercentiles? LatencyPercentiles { get; set; }

    [JsonIgnore]
    public bool IsPending => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsBusy => string.Equals(Status, "busy", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsReady => Summary is not null && !IsPending && !IsBusy;

    public AnalyticsDashboard ToDashboard()
    {
        return new AnalyticsDashboard
        {
            AppliedFilter = AppliedFilter ?? new AnalyticsAppliedFilter(),
            Summary = Summary ?? new AnalyticsSummary(),
            RequestTrend = RequestTrend,
            ResultTrend = ResultTrend,
            TokenTrend = TokenTrend,
            DurationTrend = DurationTrend,
            FallbackTrend = FallbackTrend,
            SiteDistribution = SiteDistribution,
            ModelDistribution = ModelDistribution,
            ModelCacheRatioDistribution = ModelCacheRatioDistribution,
            SourceBreakdown = SourceBreakdown,
            AccessKeyBreakdown = AccessKeyBreakdown,
            ProtocolBreakdown = ProtocolBreakdown,
            FailureReasonBreakdown = FailureReasonBreakdown,
            StatusCodeBreakdown = StatusCodeBreakdown,
            FallbackChainDistribution = FallbackChainDistribution,
            LatencyPercentiles = LatencyPercentiles ?? new AnalyticsLatencyPercentiles()
        };
    }
}

public sealed class AnalyticsDashboard
{
    public AnalyticsAppliedFilter AppliedFilter { get; set; } = new();
    public AnalyticsSummary Summary { get; set; } = new();
    public List<AnalyticsTrendPoint> RequestTrend { get; set; } = new();
    public List<AnalyticsResultTrendPoint> ResultTrend { get; set; } = new();
    public List<AnalyticsTokenTrendPoint> TokenTrend { get; set; } = new();
    public List<AnalyticsDurationTrendPoint> DurationTrend { get; set; } = new();
    public List<AnalyticsFallbackTrendPoint> FallbackTrend { get; set; } = new();
    public List<AnalyticsDistributionPoint> SiteDistribution { get; set; } = new();
    public List<AnalyticsDistributionPoint> ModelDistribution { get; set; } = new();
    public List<AnalyticsCacheRatioPoint> ModelCacheRatioDistribution { get; set; } = new();
    public List<AnalyticsBreakdownPoint> SourceBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> AccessKeyBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> ProtocolBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> FailureReasonBreakdown { get; set; } = new();
    public List<AnalyticsBreakdownPoint> StatusCodeBreakdown { get; set; } = new();
    public List<AnalyticsFallbackChainPoint> FallbackChainDistribution { get; set; } = new();
    public AnalyticsLatencyPercentiles LatencyPercentiles { get; set; } = new();

    public void PrepareDisplayValues()
    {
        var requestMax = RequestTrend.Count == 0 ? 0 : RequestTrend.Max(x => x.RequestCount);
        foreach (var item in RequestTrend) item.ValuePercent = AnalyticsText.Percent(item.RequestCount, requestMax);

        var tokenMax = TokenTrend.Count == 0 ? 0 : TokenTrend.Max(x => x.TotalTokens);
        foreach (var item in TokenTrend) item.ValuePercent = AnalyticsText.Percent(item.TotalTokens, tokenMax);

        var durationMax = DurationTrend.Count == 0 ? 0 : DurationTrend.Max(x => x.AverageTotalDurationMs);
        foreach (var item in DurationTrend) item.ValuePercent = AnalyticsText.Percent(item.AverageTotalDurationMs, durationMax);

        var fallbackMax = FallbackTrend.Count == 0 ? 0 : FallbackTrend.Max(x => x.FallbackCount);
        foreach (var item in FallbackTrend) item.ValuePercent = AnalyticsText.Percent(item.FallbackCount, fallbackMax);

        PrepareDistribution(SiteDistribution);
        PrepareDistribution(ModelDistribution);
        PrepareDistribution(SourceBreakdown);
        PrepareDistribution(AccessKeyBreakdown);
        PrepareDistribution(ProtocolBreakdown);
        PrepareDistribution(FailureReasonBreakdown);
        PrepareDistribution(StatusCodeBreakdown);
        foreach (var item in ModelCacheRatioDistribution)
        {
            item.ValuePercent = Math.Clamp(item.CacheHitRate, 0, 100);
        }
    }

    private static void PrepareDistribution<T>(IReadOnlyList<T> values) where T : AnalyticsCountedPoint
    {
        var max = values.Count == 0 ? 0 : values.Max(x => x.RequestCount);
        foreach (var item in values) item.ValuePercent = AnalyticsText.Percent(item.RequestCount, max);
    }
}

public sealed class AnalyticsAppliedFilter
{
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public string RangeType { get; set; } = string.Empty;
    public string BucketType { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? SiteId { get; set; }
    public string? AccessKeyId { get; set; }
}

public sealed class AnalyticsSummary
{
    public int TotalRequests { get; set; }
    public int SuccessRequests { get; set; }
    public int FailedRequests { get; set; }
    public double SuccessRate { get; set; }
    public double FailureRate { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalCachedTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public double AverageTotalDurationMs { get; set; }
    public double AverageFirstTokenLatencyMs { get; set; }
    public int FallbackRequestCount { get; set; }

    [JsonIgnore] public string TotalRequestsText => TotalRequests.ToString("N0", CultureInfo.InvariantCulture);
    [JsonIgnore] public string SuccessRequestsText => SuccessRequests.ToString("N0", CultureInfo.InvariantCulture);
    [JsonIgnore] public string FailedRequestsText => FailedRequests.ToString("N0", CultureInfo.InvariantCulture);
    [JsonIgnore] public string SuccessRateText => AnalyticsText.PercentText(SuccessRate);
    [JsonIgnore] public string FailureRateText => AnalyticsText.PercentText(FailureRate);
    [JsonIgnore] public string TotalInputTokensText => AnalyticsText.Compact(TotalInputTokens);
    [JsonIgnore] public string TotalOutputTokensText => AnalyticsText.Compact(TotalOutputTokens);
    [JsonIgnore] public string TotalCachedTokensText => AnalyticsText.Compact(TotalCachedTokens);
    [JsonIgnore] public string TotalTokensText => AnalyticsText.Compact(TotalTokens);
    [JsonIgnore] public string SuccessCountText => $"成功 {SuccessRequestsText}";
    [JsonIgnore] public string FailedCountText => $"失败 {FailedRequestsText}";
    [JsonIgnore] public string InputOutputText => $"输入 / 输出 {TotalInputTokensText} / {TotalOutputTokensText}";
    [JsonIgnore] public string AverageTotalDurationText => AnalyticsText.Duration(AverageTotalDurationMs);
    [JsonIgnore] public string AverageFirstTokenLatencyText => AnalyticsText.Duration(AverageFirstTokenLatencyMs);
    [JsonIgnore] public string FallbackRequestCountText => FallbackRequestCount.ToString("N0", CultureInfo.InvariantCulture);
}

public abstract class AnalyticsCountedPoint
{
    public int RequestCount { get; set; }
    [JsonIgnore] public double ValuePercent { get; set; }
    [JsonIgnore] public string RequestCountText => RequestCount.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed class AnalyticsTrendPoint
{
    public string Label { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    [JsonIgnore] public double ValuePercent { get; set; }
    [JsonIgnore] public string RequestCountText => RequestCount.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed class AnalyticsResultTrendPoint
{
    public string Label { get; set; } = string.Empty;
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public double SuccessRate { get; set; }
    public double FailureRate { get; set; }
    [JsonIgnore] public string SuccessText => $"成功 {SuccessCount:N0}";
    [JsonIgnore] public string FailText => $"失败 {FailCount:N0}";
    [JsonIgnore] public string SuccessRateText => AnalyticsText.PercentText(SuccessRate);
}

public sealed class AnalyticsTokenTrendPoint
{
    public string Label { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    [JsonIgnore] public double ValuePercent { get; set; }
    [JsonIgnore] public string TotalTokensText => AnalyticsText.Compact(TotalTokens);
    [JsonIgnore] public string SplitText => $"输入 {AnalyticsText.Compact(InputTokens)} · 缓存 {AnalyticsText.Compact(CachedTokens)} · 输出 {AnalyticsText.Compact(OutputTokens)}";
}

public sealed class AnalyticsDurationTrendPoint
{
    public string Label { get; set; } = string.Empty;
    public double AverageTotalDurationMs { get; set; }
    public double AverageFirstTokenLatencyMs { get; set; }
    [JsonIgnore] public double ValuePercent { get; set; }
    [JsonIgnore] public string DurationText => AnalyticsText.Duration(AverageTotalDurationMs);
    [JsonIgnore] public string FirstTokenText => AnalyticsText.Duration(AverageFirstTokenLatencyMs);
    [JsonIgnore] public string DetailText => $"首 Token {FirstTokenText}";
}

public sealed class AnalyticsFallbackTrendPoint
{
    public string Label { get; set; } = string.Empty;
    public int FallbackCount { get; set; }
    public double FallbackRate { get; set; }
    [JsonIgnore] public double ValuePercent { get; set; }
    [JsonIgnore] public string FallbackText => $"{FallbackCount:N0} · {AnalyticsText.PercentText(FallbackRate)}";
}

public sealed class AnalyticsDistributionPoint : AnalyticsCountedPoint
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long OutputTokens { get; set; }
    public double AverageTotalDurationMs { get; set; }
    [JsonIgnore] public string SuccessRateText => RequestCount == 0 ? "-" : AnalyticsText.PercentText(SuccessCount * 100d / RequestCount);
    [JsonIgnore] public string TotalTokensText => AnalyticsText.Compact(TotalTokens);
    [JsonIgnore] public string DurationText => AnalyticsText.Duration(AverageTotalDurationMs);
    [JsonIgnore] public string DetailText => $"成功率 {SuccessRateText} · Token {TotalTokensText} · 耗时 {DurationText}";
}

public sealed class AnalyticsCacheRatioPoint
{
    public string Label { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long TotalInputScope { get; set; }
    public double CacheHitRate { get; set; }
    [JsonIgnore] public double ValuePercent { get; set; }
    [JsonIgnore] public string RateText => AnalyticsText.PercentText(CacheHitRate);
    [JsonIgnore] public string TokenText => $"缓存 {AnalyticsText.Compact(CachedTokens)} / 输入 {AnalyticsText.Compact(TotalInputScope)}";
}

public sealed class AnalyticsBreakdownPoint : AnalyticsCountedPoint
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public double SuccessRate { get; set; }
    public long TotalTokens { get; set; }
    public double AverageTotalDurationMs { get; set; }
    public int FallbackRequestCount { get; set; }
    [JsonIgnore] public string SuccessRateText => AnalyticsText.PercentText(SuccessRate);
    [JsonIgnore] public string MetricText => $"成功率 {SuccessRateText} · Token {AnalyticsText.Compact(TotalTokens)} · 耗时 {AnalyticsText.Duration(AverageTotalDurationMs)}";
}

public sealed class AnalyticsFallbackChainPoint
{
    public string FirstSiteKey { get; set; } = string.Empty;
    public string FirstSiteLabel { get; set; } = string.Empty;
    public string FinalSiteKey { get; set; } = string.Empty;
    public string FinalSiteLabel { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public double SuccessRate { get; set; }
    public double AverageAttemptCount { get; set; }
    [JsonIgnore] public string ChainText => $"{FirstSiteLabel} → {FinalSiteLabel}";
    [JsonIgnore] public string MetricText => $"请求 {RequestCount:N0} · 成功率 {AnalyticsText.PercentText(SuccessRate)} · 平均 {AverageAttemptCount:0.0} 次尝试";
}

public sealed class AnalyticsLatencyPercentiles
{
    public AnalyticsLatencyPercentileValues TotalDuration { get; set; } = new();
    public AnalyticsLatencyPercentileValues FirstTokenLatency { get; set; } = new();
}

public sealed class AnalyticsLatencyPercentileValues
{
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public int SampleCount { get; set; }
    [JsonIgnore] public string P50Text => AnalyticsText.Duration(P50);
    [JsonIgnore] public string P95Text => AnalyticsText.Duration(P95);
    [JsonIgnore] public string P99Text => AnalyticsText.Duration(P99);
    [JsonIgnore] public string SampleCountText => $"样本 {SampleCount:N0}";
    [JsonIgnore] public string DetailText => $"P95 {P95Text} · P99 {P99Text} · {SampleCountText}";
}

internal static class AnalyticsText
{
    public static double Percent(double value, double max)
    {
        return max <= 0 ? 0 : Math.Clamp(value * 100d / max, 0, 100);
    }

    public static string PercentText(double value)
    {
        return double.IsFinite(value) ? $"{value:0.0}%" : "-";
    }

    public static string Duration(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return "-";
        return value >= 1000 ? $"{value / 1000:0.0}s" : $"{value:0}ms";
    }

    public static string Compact(long value)
    {
        if (Math.Abs(value) >= 1_000_000_000) return $"{value / 1_000_000_000d:0.0}B";
        if (Math.Abs(value) >= 1_000_000) return $"{value / 1_000_000d:0.0}M";
        if (Math.Abs(value) >= 1_000) return $"{value / 1_000d:0.0}K";
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
