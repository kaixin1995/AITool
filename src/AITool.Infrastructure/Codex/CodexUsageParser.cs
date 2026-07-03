using System.Text.Json;

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
    /// 解析后的额度窗口（供前端画进度条）。
    /// </summary>
    public sealed record Window(string Id, string Label, double? UsedPercent, string ResetLabel);

    /// <summary>
    /// 解析 wham/usage 响应体，返回所有额度窗口（5 小时 / 周 / 代码审查 / 额外）。
    /// </summary>
    public static (string? PlanType, List<Window> Windows) Parse(string body)
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
            var (fiveHour, weekly) = ClassifyWindows(rateLimit);
            var limitReached = rateLimit.Limit_Reached ?? rateLimit.LimitReached;
            var allowed = rateLimit.Allowed;
            if (fiveHour is not null)
                windows.Add(BuildWindow("five-hour", "5 小时限额", fiveHour, limitReached, allowed));
            if (weekly is not null)
                windows.Add(BuildWindow("weekly", "周限额", weekly, limitReached, allowed));
        }

        var codeReview = payload.Code_Review_Rate_Limit ?? payload.CodeReviewRateLimit;
        if (codeReview is not null)
        {
            var (fiveHour, weekly) = ClassifyWindows(codeReview);
            var limitReached = codeReview.Limit_Reached ?? codeReview.LimitReached;
            var allowed = codeReview.Allowed;
            if (fiveHour is not null)
                windows.Add(BuildWindow("code-review-five-hour", "代码审查 5 小时限额", fiveHour, limitReached, allowed));
            if (weekly is not null)
                windows.Add(BuildWindow("code-review-weekly", "代码审查周限额", weekly, limitReached, allowed));
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
                var (fiveHour, weekly) = ClassifyWindows(rateInfo);
                var limitReached = rateInfo.Limit_Reached ?? rateInfo.LimitReached;
                var allowed = rateInfo.Allowed;
                if (fiveHour is not null)
                    windows.Add(BuildWindow($"additional-five-hour-{i}", $"{limitName} 5 小时限额", fiveHour, limitReached, allowed));
                if (weekly is not null)
                    windows.Add(BuildWindow($"additional-weekly-{i}", $"{limitName} 周限额", weekly, limitReached, allowed));
            }
        }

        return (planType, windows);
    }

    private static (CodexUsageWindow? fiveHour, CodexUsageWindow? weekly) ClassifyWindows(CodexRateLimitInfo info)
    {
        var primary = info.Primary_Window ?? info.PrimaryWindow;
        var secondary = info.Secondary_Window ?? info.SecondaryWindow;

        CodexUsageWindow? fiveHour = null;
        CodexUsageWindow? weekly = null;

        foreach (var window in new[] { primary, secondary })
        {
            if (window is null) continue;
            var seconds = GetSeconds(window);
            if (seconds == FiveHourSeconds && fiveHour is null) fiveHour = window;
            else if (seconds == WeekSeconds && weekly is null) weekly = window;
        }

        // 回退：按顺序假设 primary=5h, secondary=weekly
        if (fiveHour is null && primary is not null && primary != weekly) fiveHour = primary;
        if (weekly is null && secondary is not null && secondary != fiveHour) weekly = secondary;

        return (fiveHour, weekly);
    }

    private static Window BuildWindow(string id, string label, CodexUsageWindow window, bool? limitReached, bool? allowed)
    {
        var isLimitReached = limitReached == true || allowed == false;
        var usedPercent = GetUsedPercent(window) ?? (isLimitReached ? 100 : (double?)null);
        return new Window(id, label, usedPercent, BuildResetLabel(window));
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

    private static string BuildResetLabel(CodexUsageWindow window)
    {
        var resetAt = window.Reset_At ?? window.ResetAt;
        if (resetAt.HasValue && resetAt.Value > 0 && double.IsFinite(resetAt.Value))
        {
            return FormatRemaining(DateTimeOffset.FromUnixTimeSeconds((long)resetAt.Value).UtcDateTime - DateTime.UtcNow);
        }
        var resetAfter = window.Reset_After_Seconds ?? window.ResetAfterSeconds;
        if (resetAfter.HasValue && resetAfter.Value > 0 && double.IsFinite(resetAfter.Value))
        {
            return FormatRemaining(TimeSpan.FromSeconds(resetAfter.Value));
        }
        return "-";
    }

    private static string FormatRemaining(TimeSpan span)
    {
        if (span.TotalSeconds <= 0) return "已重置";
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days}天");
        if (span.Hours > 0) parts.Add($"{span.Hours}小时");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}分");
        return parts.Count > 0 ? string.Join("", parts) + "后重置" : "<1分钟后重置";
    }

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
