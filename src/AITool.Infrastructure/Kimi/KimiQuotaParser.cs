using System.Text.Json;

namespace AITool.Infrastructure.Kimi;

/// <summary>
/// Kimi 额度响应解析器（GET {ApiBaseUrl}/v1/usages）。
/// <para>
/// 响应结构（逆向自 Kimi Code CLI /usage，参见 CodexBar docs/kimi.md）：
/// 顶层 usage 为周额度（limit 按订阅档位 1024/2048/7168），limits[] 为滚动窗口
/// （window.duration + timeUnit，如 300 TIME_UNIT_MINUTE = 5 小时窗口）；数值均为字符串。
/// </para>
/// </summary>
public static class KimiQuotaParser
{
    /// <summary>
    /// 单个额度窗口：Id/Label/UsedPercent(0-100)/ResetLabel/ResetAtUtc。
    /// </summary>
    public sealed record Window(string Id, string Label, double UsedPercent, string ResetLabel, DateTimeOffset? ResetAtUtc);

    /// <summary>
    /// 解析 usages 原始响应。返回 null 表示响应中没有可用额度数据。
    /// </summary>
    public static IReadOnlyList<Window>? Parse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var windows = new List<Window>();

            // 顶层 usage：周额度（按订阅档位）。
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (TryCreateWindow(usage, "weekly", "周额度", out var weekly))
                {
                    windows.Add(weekly);
                }
            }

            // limits[]：滚动限流窗口（如 300 分钟 = 5 小时）。
            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in limits.EnumerateArray())
                {
                    var id = $"window-{++index}";
                    if (!item.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var label = DescribeWindow(item, index);
                    if (TryCreateWindow(detail, id, label, out var window))
                    {
                        windows.Add(window);
                    }
                }
            }

            return windows.Count == 0 ? null : windows;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从含 limit/used/resetTime 的对象构建窗口；limit 无效时返回 false。
    /// </summary>
    private static bool TryCreateWindow(JsonElement source, string id, string label, out Window window)
    {
        var limit = ReadNumber(source, "limit");
        var used = ReadNumber(source, "used") ?? 0;
        if (limit is not > 0)
        {
            window = new Window(id, label, 0, "N/A", null);
            return false;
        }

        var percent = Math.Clamp(used / limit.Value * 100d, 0d, 100d);
        var resetAt = ReadResetTime(source);
        window = new Window(id, label, percent, FormatReset(resetAt), resetAt);
        return true;
    }

    /// <summary>把 window.duration + timeUnit 描述为可读标签（300 分钟 → "5 小时窗口"）。</summary>
    private static string DescribeWindow(JsonElement limitEntry, int index)
    {
        double minutes;
        if (limitEntry.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.Object)
        {
            var duration = ReadNumber(window, "duration");
            var unit = window.TryGetProperty("timeUnit", out var unitEl) ? unitEl.GetString() : null;
            var factor = unit switch
            {
                "TIME_UNIT_MINUTE" => 1d,
                "TIME_UNIT_HOUR" => 60d,
                "TIME_UNIT_DAY" => 1440d,
                _ => 1d
            };
            minutes = (duration ?? 0) * factor;
        }
        else
        {
            minutes = 0;
        }

        return minutes <= 0
            ? $"窗口 {index}"
            : minutes % 60 == 0
                ? $"{minutes / 60:0} 小时窗口"
                : $"{minutes:0} 分钟窗口";
    }

    private static double? ReadNumber(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        // 上游数值以字符串下发（如 "2048"），兼容直接给数字的情况。
        if (value.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(value.GetString(), out var parsed) ? parsed : null;
        }

        return value.TryGetDouble(out var number) ? number : null;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement source)
    {
        if (!source.TryGetProperty("resetTime", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static string FormatReset(DateTimeOffset? resetAt)
        => resetAt is null ? "N/A" : resetAt.Value.ToLocalTime().ToString("MM-dd HH:mm");
}
