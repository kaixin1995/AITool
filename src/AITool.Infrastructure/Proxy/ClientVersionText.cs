using System.Text.RegularExpressions;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// User-Agent 版本号的提取、比较与替换工具（纯静态、无状态）。
/// 供请求头模板「AI 查最新版」与 Codex 客户端版本解析共用。
/// 兼容 <c>Product/1.2.3</c>、<c>Product 1.2.3</c> 及带 prerelease 后缀的
/// <c>0.149.0-alpha.4.3</c> 等常见格式；取字符串中第一个「名称/版本」段。
/// </summary>
public static partial class ClientVersionText
{
    /// <summary>
    /// 版本号模式：主.次[.修订][‑prerelease]。prerelease 以 - 开头，可含字母、数字、点、连字符。
    /// </summary>
    [GeneratedRegex(@"[/\s]v?(?<version>\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?)")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// 从 User-Agent（或任意含 name/version 段的字符串）提取第一个版本号；识别不到返回 null。
    /// </summary>
    public static string? ExtractVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // 允许字符串以版本段开头（前面没有 / 或空格）时补一个前导分隔再匹配。
        var match = VersionRegex().Match(" " + text.TrimStart());
        return match.Success ? match.Groups["version"].Value : null;
    }

    /// <summary>
    /// 把文本中第一个版本号替换为 <paramref name="newVersion"/>，其余内容原样保留；
    /// 找不到版本号时返回原文。
    /// </summary>
    public static string ReplaceVersion(string text, string newVersion)
    {
        // 统一在去除前导空白的副本上匹配与拼接，避免前导空白造成的偏移错位。
        var trimmed = text.TrimStart();
        var match = VersionRegex().Match(" " + trimmed);
        if (!match.Success) return text;
        var start = match.Groups["version"].Index - 1;
        return trimmed[..start] + newVersion + trimmed[(start + match.Groups["version"].Length)..];
    }

    /// <summary>
    /// 逐段数值比较两个版本号；核心段相等时带 prerelease 后缀的一方更低（1.0.0-beta &lt; 1.0.0）。
    /// 无法解析的段按 0 处理。
    /// </summary>
    /// <returns>负数表示 a&lt;b，0 表示相等，正数表示 a&gt;b。</returns>
    public static int CompareVersions(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        var (coreA, preA) = SplitPrerelease(a.Trim().TrimStart('v'));
        var (coreB, preB) = SplitPrerelease(b.Trim().TrimStart('v'));

        var partsA = coreA.Split('.');
        var partsB = coreB.Split('.');
        var len = Math.Max(partsA.Length, partsB.Length);
        for (var i = 0; i < len; i++)
        {
            var va = i < partsA.Length ? ParseLeadingNumber(partsA[i]) : 0;
            var vb = i < partsB.Length ? ParseLeadingNumber(partsB[i]) : 0;
            if (va != vb) return va < vb ? -1 : 1;
        }

        if (preA is null && preB is null) return 0;
        if (preA is null) return 1;
        if (preB is null) return -1;
        return string.Compare(preA, preB, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Core, string? Prerelease) SplitPrerelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash >= 0
            ? (version[..dash], version[(dash + 1)..])
            : (version, null);
    }

    private static long ParseLeadingNumber(string segment)
    {
        var digits = 0L;
        foreach (var ch in segment)
        {
            if (ch is < '0' or > '9') break;
            digits = digits * 10 + (ch - '0');
        }
        return digits;
    }
}
