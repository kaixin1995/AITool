using System.Text.RegularExpressions;

namespace ProtocolSyncCheck;

/// <summary>
/// 从 C# 源码文件中提取实际读写的 JSON 字段名。
/// 匹配 JsonObject / JsonNode 的索引器访问和初始化器赋值模式。
/// </summary>
internal static class CSharpFieldScanner
{
    // 匹配 jsonNode["field_name"] 读取和写入模式
    private static readonly Regex IndexerRegex = new(
        @"\[""(?<field>[a-z_][a-z0-9_]*)""\]",
        RegexOptions.Compiled);

    // 匹配 .Add("field_name", 模式
    private static readonly Regex AddMethodRegex = new(
        @"\.Add\(""(?<field>[a-z_][a-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// 从一组 C# 源码文件中提取所有出现过的 JSON 字段名。
    /// </summary>
    public static HashSet<string> ScanFiles(IEnumerable<string> filePaths)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in filePaths)
        {
            if (!File.Exists(filePath))
            {
                continue;
            }

            var content = File.ReadAllText(filePath);

            foreach (Match match in IndexerRegex.Matches(content))
            {
                var field = match.Groups["field"].Value;
                // 过滤掉明显不是协议字段的通用词
                if (!IsCommonNonProtocolField(field))
                {
                    fields.Add(field);
                }
            }

            foreach (Match match in AddMethodRegex.Matches(content))
            {
                var field = match.Groups["field"].Value;
                if (!IsCommonNonProtocolField(field))
                {
                    fields.Add(field);
                }
            }
        }

        return fields;
    }

    /// <summary>
    /// 判断是否为非协议特定字段的通用名称（如 role、type 等过于通用的词不单独计）。
    /// </summary>
    private static bool IsCommonNonProtocolField(string field)
    {
        return false;
    }
}
