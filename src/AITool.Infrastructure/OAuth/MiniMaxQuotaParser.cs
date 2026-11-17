using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.OAuth;

namespace AITool.Infrastructure.OAuth;

/// <summary>
/// MiniMax Code 额度解析：M Plan 窗口（platform coding_plan/remains 的 model_remains[]）
/// 与账号会员信息（agent get_membership_info 的套餐/余额）。
/// <para>窗口语义对齐 magpie 插件 planWindows：每个模型组有区间窗与周窗，「general」组
/// 是套餐本身的窗口（参与调度），其余模型组窗口仅展示（非阻塞）；status=2 视为 100% 用尽。</para>
/// </summary>
public static class MiniMaxQuotaParser
{
    /// <summary>解析 M Plan 的 model_remains[] 为窗口列表（general 在前，非阻塞窗口殿后）。</summary>
    public static IReadOnlyList<AccountQuotaWindow> ParsePlanWindows(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("model_remains", out var remains)
                || remains.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var blocking = new List<AccountQuotaWindow>();
            var aside = new List<AccountQuotaWindow>();
            foreach (var group in remains.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(group, "model_name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var isGeneral = string.Equals(name, "general", StringComparison.OrdinalIgnoreCase);
                foreach (var (prefix, labelSuffix, weekly) in new[] { ("interval", "区间", false), ("weekly", "周", true) })
                {
                    var left = GetNumber(group, $"current_{prefix}_remaining_percent");
                    var status = GetNumber(group, $"current_{prefix}_status") ?? 0;
                    var end = GetNumber(group, weekly ? "weekly_end_time" : "end_time");
                    var start = GetNumber(group, weekly ? "weekly_start_time" : "start_time");

                    // status=3 的窗口不存在；status=2 = 用尽（按 100% 计）。
                    if (status == 3 || (left is null && status != 2))
                    {
                        continue;
                    }

                    var used = status == 2 ? 100 : Math.Clamp(100 - (left ?? 0), 0, 100);
                    var resetAt = end is > 0 ? ToUtc(end.Value) : null;
                    var window = new AccountQuotaWindow
                    {
                        Id = isGeneral ? $"minimax_{prefix}" : $"minimax_{Sanitize(name)}_{prefix}",
                        Label = isGeneral ? $"套餐 {labelSuffix}窗口" : $"{Capitalize(name)} · {labelSuffix}窗口",
                        UsedPercent = used,
                        ResetAtUtc = resetAt,
                        ResetLabel = resetAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? string.Empty,
                    };
                    (isGeneral ? blocking : aside).Add(window);
                }
            }

            return blocking.Concat(aside).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>解析 get_membership_info：套餐等级与积分余额。</summary>
    public static (string? PlanType, string? BalanceText) ParseMembership(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var hasPlan = GetBool(root, "has_token_plan") ?? GetBool(root, "hasTokenPlan");
            var tier = GetString(root, "tier");
            var balance = GetNumber(root, "balance");
            var planType = hasPlan == true
                ? string.IsNullOrWhiteSpace(tier) ? "M Plan" : $"M Plan {tier}"
                : hasPlan == false ? "Free" : tier;
            var balanceText = balance is { } value ? $"{value:0.##} 积分" : null;
            return (planType, balanceText);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>解析落库快照（QueryQuotaAsync 的 RawJson 格式）。</summary>
    public static DeviceOAuthQuotaResult? ParseCached(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var windows = new List<AccountQuotaWindow>();
            if (root.TryGetProperty("windows", out var windowsEl) && windowsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in windowsEl.EnumerateArray())
                {
                    if (w.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    windows.Add(new AccountQuotaWindow
                    {
                        Id = GetString(w, "id") ?? string.Empty,
                        Label = GetString(w, "label") ?? string.Empty,
                        UsedPercent = GetNumber(w, "usedPercent"),
                        ResetAtUtc = GetNumber(w, "resetAtUtc") is { } seconds
                            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
                            : null,
                        ResetLabel = GetString(w, "resetLabel") ?? string.Empty,
                    });
                }
            }

            var planType = GetString(root, "planType");
            if (windows.Count == 0 && planType is null)
            {
                return null;
            }

            return new DeviceOAuthQuotaResult { Success = true, PlanType = planType, RawJson = rawJson, Windows = windows };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>快照序列化（AccountQuotaWindow → 可回读 JSON）。</summary>
    public static string SerializeSnapshot(string? planType, IReadOnlyList<AccountQuotaWindow> windows)
        => JsonSerializer.Serialize(new
        {
            planType,
            windows = windows.Select(w => new
            {
                id = w.Id,
                label = w.Label,
                usedPercent = w.UsedPercent,
                resetAtUtc = w.ResetAtUtc?.ToUnixTimeMilliseconds() / 1000.0,
                w.ResetLabel,
            }),
        });

    /// <summary>MiniMax 时间戳（秒或毫秒）→ UTC。</summary>
    private static DateTimeOffset? ToUtc(double value)
    {
        var millis = value < 1e12 ? value * 1000 : value;
        if (millis <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
    }

    private static string Capitalize(string input)
        => input.Length == 0 ? input : char.ToUpperInvariant(input[0]) + input[1..];

    private static string Sanitize(string input)
        => new(input.Where(char.IsLetterOrDigit).ToArray());

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static double? GetNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
