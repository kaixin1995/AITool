using System.Text.Json;
using AITool.Application.Sites;

namespace AITool.Infrastructure.DeepSeek;

/// <summary>
/// DeepSeek 账户余额响应解析器（GET /user/balance，Bearer 鉴权，官方文档化接口）。
/// <para>
/// 响应结构（https://api-docs.deepseek.com/zh-cn/api/get-user-balance）：
/// <code>
/// {
///   "is_available": true,
///   "balance_infos": [
///     { "currency": "CNY", "total_balance": "110.00",
///       "granted_balance": "10.00", "topped_up_balance": "100.00" }
///   ]
/// }
/// </code>
/// 数值均为字符串（兼容直接给数字的情况）。
/// </para>
/// </summary>
public static class DeepSeekBalanceParser
{
    /// <summary>解析结果：窗口/余额模型中的余额列表 + 业务错误。</summary>
    public sealed record ParseResult
    {
        public IReadOnlyList<SiteQuotaBalanceInfo> Balances { get; init; } = [];
        /// <summary>业务级错误（非 2xx 的错误报文 / 结构不识别），有值时表示确定性失败。</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// 解析余额响应原始报文。无法识别（非 JSON、缺 balance_infos）返回带 Error 的结果；
    /// balance_infos 为空数组视为成功但无数据（调用方据此给「暂无数据」）。
    /// </summary>
    public static ParseResult Parse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new ParseResult { Error = "响应为空" };
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("balance_infos", out var infos)
                || infos.ValueKind != JsonValueKind.Array)
            {
                return new ParseResult { Error = "响应无法解析为余额数据" };
            }

            var balances = new List<SiteQuotaBalanceInfo>();
            foreach (var info in infos.EnumerateArray())
            {
                if (info.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var currency = info.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                var total = ReadDecimal(info, "total_balance");
                if (string.IsNullOrWhiteSpace(currency) || total is null)
                {
                    continue;
                }

                balances.Add(new SiteQuotaBalanceInfo(
                    currency,
                    total.Value,
                    ReadDecimal(info, "granted_balance"),
                    ReadDecimal(info, "topped_up_balance")));
            }

            return new ParseResult { Balances = balances };
        }
        catch (JsonException)
        {
            return new ParseResult { Error = "响应无法解析为余额数据" };
        }
    }

    /// <summary>站点 base_url 是否属于 DeepSeek（host 级匹配 deepseek.com 及其子域）。</summary>
    public static bool MatchesBaseUrl(string baseUrl)
        => TryGetHost(baseUrl, out var host) && HostEndsWith(host, "deepseek.com");

    /// <summary>余额查询端点（固定官方地址，与推理 base_url 同域）。</summary>
    public const string BalancePath = "https://api.deepseek.com/user/balance";

    /// <summary>读取字符串型十进制数值（官方以字符串下发，兼容数字）；缺失/非法返回 null。</summary>
    private static decimal? ReadDecimal(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return decimal.TryParse(value.GetString(), out var parsed) ? parsed : null;
        }

        return value.TryGetDecimal(out var number) ? number : null;
    }

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
