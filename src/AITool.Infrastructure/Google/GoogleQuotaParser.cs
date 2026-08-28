using System.Text.Json;
using AITool.Infrastructure.Common;

namespace AITool.Infrastructure.Google;

/// <summary>
/// Antigravity fetchAvailableModels 响应解析：models[].quotaInfo {remainingFraction, resetTime}
/// 转换为额度窗口（对齐 gcli2api fetch_quota_info）。remainingFraction 为剩余比例 0~1。
/// </summary>
public static class GoogleQuotaParser
{
    /// <summary>
    /// 单个模型的额度窗口。
    /// </summary>
    public sealed record Window(string Id, string Label, double UsedPercent, string ResetLabel, DateTimeOffset? ResetAtUtc);

    /// <summary>
    /// 解析 fetchAvailableModels 原始响应。返回 null 表示响应中没有可用额度数据。
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

            var windows = new List<Window>();
            foreach (var property in models.EnumerateObject())
            {
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
                var usedPercent = Math.Clamp((1d - remaining) * 100d, 0d, 100d);

                string resetLabel = string.Empty;
                DateTimeOffset? resetAt = null;
                if (quota.TryGetProperty("resetTime", out var resetTime)
                    && resetTime.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(resetTime.GetString(), out var parsed))
                {
                    resetAt = parsed.ToUniversalTime();
                    resetLabel = QuotaResetLabelFormatter.Format(parsed.ToUniversalTime() - DateTimeOffset.UtcNow);
                }

                windows.Add(new Window(property.Name, property.Name, usedPercent, resetLabel, resetAt));
            }

            return windows.Count > 0 ? windows : null;
        }
        catch
        {
            return null;
        }
    }
}
