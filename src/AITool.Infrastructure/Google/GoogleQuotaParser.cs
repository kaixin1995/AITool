using System.Text.Json;
using AITool.Infrastructure.Common;

namespace AITool.Infrastructure.Google;

/// <summary>
/// Antigravity fetchAvailableModels 响应解析：models[].quotaInfo {remainingFraction, resetTime}。
/// <para>
/// 上游虽然按模型返回 quotaInfo，但真实额度只有两桶：Claude/GPT-OSS 系列共享一桶、
/// Gemini 系列共享一桶，其余模型的 quotaInfo 不可信（噪声）。这里按前缀归类聚合为
/// 两个额度窗口，桶内取最大已用百分比（成员间理论上同值，取最大最保守）。
/// remainingFraction 为剩余比例 0~1。
/// </para>
/// </summary>
public static class GoogleQuotaParser
{
    /// <summary>两个真实额度桶的稳定标识（暴露给前端/巡检展示与禁用判定）。</summary>
    public const string ClaudeGptOssBucketId = "claude-gpt-oss";
    public const string GeminiBucketId = "gemini";

    /// <summary>
    /// 单个额度桶窗口。
    /// </summary>
    public sealed record Window(string Id, string Label, double UsedPercent, string ResetLabel, DateTimeOffset? ResetAtUtc);

    private enum QuotaBucket
    {
        ClaudeGptOss,
        Gemini,
    }

    /// <summary>
    /// 解析 fetchAvailableModels 原始响应并聚合为两个额度桶。返回 null 表示响应中没有可用额度数据。
    /// </summary>
    public static IReadOnlyList<Window>? Parse(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var claudeMembers = new List<(double UsedPercent, DateTimeOffset? ResetAt)>();
            var geminiMembers = new List<(double UsedPercent, DateTimeOffset? ResetAt)>();

            foreach (var property in models.EnumerateObject())
            {
                var bucket = ClassifyModel(property.Name);
                if (bucket is null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object
                    || !property.Value.TryGetProperty("quotaInfo", out var quota)
                    || quota.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var remaining = quota.TryGetProperty("remainingFraction", out var fraction)
                    && fraction.ValueKind == JsonValueKind.Number
                    ? fraction.GetDouble()
                    : double.NaN;
                if (double.IsNaN(remaining))
                {
                    // 无 remainingFraction 的 quotaInfo 视为无有效数据（不能默认 100% 触发自动禁用）。
                    continue;
                }

                DateTimeOffset? resetAt = null;
                if (quota.TryGetProperty("resetTime", out var resetTime)
                    && resetTime.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(resetTime.GetString(), out var parsed))
                {
                    resetAt = parsed.ToUniversalTime();
                }

                var member = (Math.Clamp((1d - remaining) * 100d, 0d, 100d), resetAt);
                if (bucket == QuotaBucket.Gemini)
                {
                    geminiMembers.Add(member);
                }
                else
                {
                    claudeMembers.Add(member);
                }
            }

            var windows = new List<Window>();
            AppendBucket(windows, ClaudeGptOssBucketId, "Claude/GPT-OSS 系列", claudeMembers);
            AppendBucket(windows, GeminiBucketId, "Gemini 系列", geminiMembers);
            return windows.Count > 0 ? windows : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 判断额度窗口是否为 Gemini 桶（自动禁用只看 Gemini 额度）。
    /// </summary>
    public static bool IsGeminiBucket(string? windowId)
        => string.Equals(windowId, GeminiBucketId, StringComparison.OrdinalIgnoreCase);

    private static QuotaBucket? ClassifyModel(string modelName)
    {
        if (modelName.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return QuotaBucket.Gemini;
        }

        if (modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            || modelName.StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase))
        {
            return QuotaBucket.ClaudeGptOss;
        }

        // 其余模型的 quotaInfo 属于上游噪声，不参与两桶聚合。
        return null;
    }

    private static void AppendBucket(
        List<Window> windows,
        string id,
        string label,
        List<(double UsedPercent, DateTimeOffset? ResetAt)> members)
    {
        if (members.Count == 0)
        {
            return;
        }

        // 桶内成员共享同一额度，理论上同值；取最大已用（最保守），重置时间优先随该成员，缺省取任一非空值。
        var representative = members[0];
        foreach (var member in members)
        {
            if (member.UsedPercent > representative.UsedPercent
                || (representative.ResetAt is null && member.ResetAt is not null))
            {
                representative = member;
            }
        }

        var resetAt = representative.ResetAt ?? members.FirstOrDefault(m => m.ResetAt is not null).ResetAt;
        var resetLabel = resetAt is not null
            ? QuotaResetLabelFormatter.Format(resetAt.Value - DateTimeOffset.UtcNow)
            : string.Empty;

        windows.Add(new Window(id, label, representative.UsedPercent, resetLabel, resetAt));
    }
}
