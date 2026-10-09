using System.Text.Json;
using AITool.Application.Sites;

namespace AITool.Infrastructure.Zhipu;

/// <summary>
/// 智谱 GLM 编程套餐额度响应解析器（GET /api/monitor/usage/quota/limit，裸 API key 鉴权）。
/// <para>
/// 响应结构（逆向自 bigmodel.cn 官网控制台监控接口，国内站 open.bigmodel.cn 与国际站
/// api.z.ai 共用同一后端，字段一致）：
/// <code>
/// { "success": true,
///   "data": {
///     "level": "GLM Coding Plan ...",
///     "limits": [
///       { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 1.0,  "nextResetTime": 1780000000000 },
///       { "type": "TOKENS_LIMIT", "unit": 6, "number": 7, "percentage": 42.0, "nextResetTime": 1779999000000 },
///       { "type": "TIME_LIMIT",   "percentage": 7.0 }
///     ] } }
/// </code>
/// 窗口分类必须优先看 <c>unit</c> 字段（3=5 小时滚动窗，6=每周窗）：周期末尾每周窗口会比
/// 5 小时窗口更早重置，按 nextResetTime 排序必然把两桶标反（cc-switch issue #3036 实例）。
/// <c>unit</c> 缺失或不识别时走兜底启发式：无重置时间的条目优先归 5 小时桶（5 小时桶在
/// 0% 用量时可能没有重置时间），其余按重置时间升序填入空缺槽位。
/// </para>
/// <para>老套餐（2026-02-12 前订阅）只回 1 条 TOKENS_LIMIT，自然降级为仅 5 小时窗口。</para>
/// </summary>
public static class ZhipuQuotaParser
{
    /// <summary>解析结果：至少解析出窗口或业务错误之一时非 null；JSON 无效或结构不识别时 null。</summary>
    public sealed record ParseResult
    {
        public string? Level { get; init; }
        public IReadOnlyList<SiteQuotaWindow> Windows { get; init; } = [];
        /// <summary>业务级错误（响应 success=false 的 msg），有值时表示确定性失败。</summary>
        public string? Error { get; init; }
    }

    /// <summary>智谱额度窗口标识与标签。</summary>
    public const string FiveHourWindowId = "five_hour";
    public const string WeeklyWindowId = "weekly_limit";

    /// <summary>
    /// 解析智谱额度响应原始报文。返回 null 表示报文不可识别（非 JSON 或缺少 data 字段）。
    /// </summary>
    public static ParseResult? Parse(string rawJson)
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

            // 业务级错误：success=false 时 msg 为面向用户的错误信息。
            if (root.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.False)
            {
                var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                return new ParseResult { Error = string.IsNullOrWhiteSpace(msg) ? "未知错误" : msg };
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var level = data.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String
                ? lv.GetString()
                : null;

            return new ParseResult
            {
                Level = string.IsNullOrWhiteSpace(level) ? null : level,
                Windows = ParseTokenTiers(data),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把智谱 data.limits[] 里的 TOKENS_LIMIT/CREDIT_LIMIT 条目解析成窗口列表（5 小时在前）。
    /// </summary>
    /// <param name="data">响应的 data 对象。</param>
    public static IReadOnlyList<SiteQuotaWindow> ParseTokenTiers(JsonElement data)
    {
        var fiveHour = default(Entry?);
        var weekly = default(Entry?);
        var unclassified = new List<Entry>();

        if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var limitType = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : string.Empty;
                // 大小写不敏感：上游若把 TOKENS_LIMIT 改成小写或驼峰仍能识别。
                if (!string.Equals(limitType, "TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(limitType, "CREDIT_LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entry = new Entry(
                    ReadPercentage(item),
                    ReadResetTime(item));
                switch (ClassifyWindow(item))
                {
                    case WindowKind.FiveHour when fiveHour is null:
                        fiveHour = entry;
                        break;
                    case WindowKind.Weekly when weekly is null:
                        weekly = entry;
                        break;
                    default:
                        unclassified.Add(entry);
                        break;
                }
            }
        }

        // 兜底：unit 缺失或不认识时，无重置时间的优先归 5 小时桶，其余按重置时间升序填空缺槽位。
        // 用 LINQ 稳定排序：同为「无重置时间」的条目保持原始顺序（List.Sort 不稳定）。
        foreach (var entry in unclassified
            .OrderBy(e => e.ResetAtUtc is null ? 0 : 1)
            .ThenBy(e => e.ResetAtUtc ?? DateTimeOffset.MinValue))
        {
            if (fiveHour is null)
            {
                fiveHour = entry;
            }
            else if (weekly is null)
            {
                weekly = entry;
            }
            // 智谱当前最多两条 TOKENS_LIMIT，多余的忽略。
        }

        var windows = new List<SiteQuotaWindow>(2);
        if (fiveHour is { } fh)
        {
            windows.Add(ToWindow(FiveHourWindowId, "5 小时窗口", fh));
        }

        if (weekly is { } wk)
        {
            windows.Add(ToWindow(WeeklyWindowId, "每周额度", wk));
        }

        return windows;
    }

    /// <summary>智谱 TOKENS_LIMIT 条目按 unit 字段的显式窗口分类。</summary>
    private enum WindowKind
    {
        FiveHour,
        Weekly,
    }

    private sealed record Entry(double Percentage, DateTimeOffset? ResetAtUtc);

    /// <summary>
    /// 按 unit 字段判定条目所属窗口：
    /// <para>unit:3 → 5 小时滚动窗口（number:5）；unit:6 → 每周窗口（number 实测 1 与 7
    /// 两种取值，故只锚定 unit、不绑 number）。缺失或不认识返回 null（走兜底启发式）。</para>
    /// </summary>
    private static WindowKind? ClassifyWindow(JsonElement item)
    {
        if (!item.TryGetProperty("unit", out var unit) || !unit.TryGetInt32(out var value))
        {
            return null;
        }

        return value switch
        {
            3 => WindowKind.FiveHour,
            6 => WindowKind.Weekly,
            _ => null,
        };
    }

    /// <summary>读取已用百分比：数字优先，兼容字符串（"42"）；无效或缺失按 0（仍展示窗口）。</summary>
    private static double ReadPercentage(JsonElement item)
    {
        if (!item.TryGetProperty("percentage", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(value.GetString(), out var parsed) ? parsed : 0;
        }

        return value.TryGetDouble(out var number) ? number : 0;
    }

    /// <summary>读取 nextResetTime（毫秒 Unix 时间戳）；缺失、非正数或溢出视为无重置时间。</summary>
    private static DateTimeOffset? ReadResetTime(JsonElement item)
    {
        if (!item.TryGetProperty("nextResetTime", out var value)
            || !value.TryGetInt64(out var millis)
            || millis <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
    }

    private static SiteQuotaWindow ToWindow(string id, string label, Entry entry) => new(
        id,
        label,
        entry.Percentage,
        entry.ResetAtUtc?.ToLocalTime().ToString("MM-dd HH:mm"),
        entry.ResetAtUtc);

    /// <summary>
    /// 站点 base_url 是否属于智谱（host 级匹配 bigmodel.cn / z.ai 及其子域）。
    /// </summary>
    public static bool MatchesBaseUrl(string baseUrl)
        => TryGetHost(baseUrl, out var host)
            && (HostEndsWith(host, "bigmodel.cn") || HostEndsWith(host, "z.ai"));

    /// <summary>
    /// 按站点 base_url 解析额度查询端点所在主机：bigmodel.cn 域走国内站
    /// open.bigmodel.cn，其余（z.ai 域）走国际站 api.z.ai。无跨主机回退——用户既然
    /// 用该主机做推理，额度查询也走同一可达性。
    /// </summary>
    public static string ResolveQuotaBase(string baseUrl)
        => TryGetHost(baseUrl, out var host) && HostEndsWith(host, "bigmodel.cn")
            ? "https://open.bigmodel.cn"
            : "https://api.z.ai";

    /// <summary>智谱套餐额度查询路径（个人版；团队版同路径加 ?type=2 与组织/项目头，暂未支持）。</summary>
    public const string QuotaPath = "/api/monitor/usage/quota/limit";

    private static bool TryGetHost(string baseUrl, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        host = uri.Host.ToLowerInvariant();
        return true;
    }

    private static bool HostEndsWith(string host, string suffix)
        => host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal);
}
