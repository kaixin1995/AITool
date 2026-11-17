using System.Text.Json;
using AITool.Infrastructure.Common;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// 解析 chatgpt.com/backend-api/wham/usage 响应，区分 5 小时窗口(18000s)和周窗口(604800s)。
/// 移植自 codex-patrol Services/CodexQuotaParser.cs。
/// </summary>
public static class CodexUsageParser
{
    private const int FiveHourSeconds = 18_000;
    private const int WeekSeconds = 604_800;

    /// <summary>
    /// 解析后的额度窗口（供前端画进度条 + 巡检缓存策略判断）。
    /// </summary>
    public sealed record Window(string Id, string Label, double? UsedPercent, string ResetLabel, DateTimeOffset? ResetAtUtc, double? LimitWindowSeconds);

    /// <summary>
    /// 解析 wham/usage 响应体，返回所有额度窗口（5 小时 / 周 / 代码审查 / 额外）。
    /// </summary>
    public static (string? PlanType, List<Window> Windows) Parse(string body, DateTimeOffset? observedAt = null)
    {
        var windows = new List<Window>();

        CodexUsagePayload? payload;
        try { payload = JsonSerializer.Deserialize<CodexUsagePayload>(body); }
        catch { return (null, windows); }
        if (payload is null) return (null, windows);

        var planType = NormalizePlanType(payload.Plan_Type ?? payload.PlanType);

        var rateLimit = payload.Rate_Limit ?? payload.RateLimit;
        if (rateLimit is not null)
        {
            AddWindows(windows, rateLimit, "", "", observedAt);
        }

        var codeReview = payload.Code_Review_Rate_Limit ?? payload.CodeReviewRateLimit;
        if (codeReview is not null)
        {
            AddWindows(windows, codeReview, "code-review-", "代码审查 ", observedAt);
        }

        var additional = payload.Additional_Rate_Limits ?? payload.AdditionalRateLimits;
        if (additional is { Count: > 0 })
        {
            for (var i = 0; i < additional.Count; i++)
            {
                var item = additional[i];
                var rateInfo = item?.Rate_Limit ?? item?.RateLimit;
                if (rateInfo is null) continue;
                var limitName = item?.Limit_Name ?? item?.LimitName
                                ?? item?.Metered_Feature ?? item?.MeteredFeature
                                ?? $"additional-{i + 1}";
                AddWindows(windows, rateInfo, $"additional-{i}-", $"{limitName} ", observedAt);
            }
        }

        return (planType, windows);
    }

    private static void AddWindows(List<Window> result, CodexRateLimitInfo info, string prefix, string labelPrefix, DateTimeOffset? observedAt)
    {
        var primary = info.Primary_Window ?? info.PrimaryWindow;
        var secondary = info.Secondary_Window ?? info.SecondaryWindow;
        var index = 0;
        foreach (var window in new[] { primary, secondary })
        {
            index++;
            if (window is null) continue;
            var seconds = GetSeconds(window);
            var (id, label) = seconds switch
            {
                FiveHourSeconds => ("five-hour", "5 小时限额"),
                WeekSeconds => ("weekly", "周限额"),
                28 * 86400 => ("monthly", "4 周限额"),
                >= 29 * 86400 and <= 31 * 86400 => ("monthly", "月度限额"),
                _ => ($"window-{index}", "其他额度窗口")
            };
            if (result.Any(x => x.Id == prefix + id)) id += $"-{index}";
            result.Add(BuildWindow(prefix + id, labelPrefix + label, window, info.Limit_Reached ?? info.LimitReached, info.Allowed, observedAt));
        }
    }

    private static Window BuildWindow(string id, string label, CodexUsageWindow window, bool? limitReached, bool? allowed, DateTimeOffset? observedAt)
    {
        var isLimitReached = limitReached == true || allowed == false;
        var usedPercent = GetUsedPercent(window) ?? (isLimitReached ? 100 : (double?)null);
        var reset = ResolveResetAtUtc(window, observedAt);
        return new Window(id, label, usedPercent, reset is null ? "-" : FormatRemaining(reset.Value - DateTimeOffset.UtcNow), reset, GetSeconds(window));
    }

    /// <summary>
    /// 解析窗口的 UTC 重置时间（优先 reset_at 绝对时间戳，其次 now+reset_after_seconds）；无则为 null。
    /// </summary>
    private static DateTimeOffset? ResolveResetAtUtc(CodexUsageWindow window, DateTimeOffset? observedAt)
    {
        var resetAt = window.Reset_At ?? window.ResetAt;
        if (resetAt.HasValue && resetAt.Value > 0 && double.IsFinite(resetAt.Value))
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)resetAt.Value);
        }
        var resetAfter = window.Reset_After_Seconds ?? window.ResetAfterSeconds;
        if (resetAfter.HasValue && resetAfter.Value > 0 && double.IsFinite(resetAfter.Value))
        {
            return (observedAt ?? DateTimeOffset.UtcNow).AddSeconds(resetAfter.Value);
        }
        return null;
    }

    private static double? GetSeconds(CodexUsageWindow window)
    {
        var v = window.Limit_Window_Seconds ?? window.LimitWindowSeconds;
        return v.HasValue && double.IsFinite(v.Value) ? v.Value : null;
    }

    private static double? GetUsedPercent(CodexUsageWindow window)
    {
        var v = window.Used_Percent ?? window.UsedPercent;
        return v.HasValue && double.IsFinite(v.Value) ? v.Value : null;
    }

    private static string FormatRemaining(TimeSpan span)
        => QuotaResetLabelFormatter.Format(span);

    private static string NormalizePlanType(string? raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "plus" => "Plus",
            "team" => "Team",
            "free" => "Free",
            "pro" => "Pro",
            "prolite" or "pro_lite" or "pro-lite" => "ProLite",
            _ => string.IsNullOrWhiteSpace(raw) ? "Unknown" : raw.Trim()
        };
    }
}
