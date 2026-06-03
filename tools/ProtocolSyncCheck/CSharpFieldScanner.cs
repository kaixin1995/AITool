using System.Text.RegularExpressions;

namespace ProtocolSyncCheck;

/// <summary>
/// 从 C# 源码文件中提取实际读写的 JSON 字段名，并尽量推断当前项目中的类型线索。
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
    /// 从一组 C# 源码文件中提取所有出现过的 JSON 字段名及类型线索。
    /// </summary>
    public static Dictionary<string, CurrentFieldUsage> ScanFiles(IEnumerable<string> filePaths)
    {
        var fields = new Dictionary<string, CurrentFieldUsage>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in filePaths)
        {
            if (!File.Exists(filePath))
            {
                continue;
            }

            var lines = File.ReadAllLines(filePath);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                AddMatches(fields, filePath, i + 1, line, IndexerRegex);
                AddMatches(fields, filePath, i + 1, line, AddMethodRegex);
            }
        }

        return fields;
    }

    /// <summary>
    /// 将一行中的字段匹配结果加入索引，并记录类型线索。
    /// </summary>
    private static void AddMatches(
        Dictionary<string, CurrentFieldUsage> fields,
        string filePath,
        int lineNumber,
        string line,
        Regex regex)
    {
        foreach (Match match in regex.Matches(line))
        {
            var field = match.Groups["field"].Value;
            if (IsCommonNonProtocolField(field))
            {
                continue;
            }

            if (!fields.TryGetValue(field, out var usage))
            {
                usage = new CurrentFieldUsage(field);
                fields[field] = usage;
            }

            var inferredTypes = InferTypeHints(line, field);
            usage.AddUsage(filePath, lineNumber, inferredTypes);
        }
    }

    /// <summary>
    /// 基于当前代码行推断字段的类型线索。
    /// </summary>
    private static List<string> InferTypeHints(string line, string field)
    {
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var escapedField = Regex.Escape(field);

        CollectMatch(hints, line, $@"\[""{escapedField}""\]\?*\.GetValue<(?<type>[^>]+)>", NormalizeCSharpType);
        CollectMatch(hints, line, $@"\[""{escapedField}""\]\s+is\s+(?<type>JsonArray|JsonObject|JsonValue)", NormalizeJsonNodeType);
        CollectMatch(hints, line, $@"\[""{escapedField}""\]\s*=\s*new\s+(?<type>JsonArray|JsonObject)", NormalizeJsonNodeType);
        CollectMatch(hints, line, $@"\.Add\(""{escapedField}"",\s*new\s+(?<type>JsonArray|JsonObject)", NormalizeJsonNodeType);

        if (Regex.IsMatch(line, $@"\[""{escapedField}""\]\s*=\s*(true|false)\b") ||
            Regex.IsMatch(line, $@"\.Add\(""{escapedField}"",\s*(true|false)\b"))
        {
            hints.Add("bool");
        }

        if (Regex.IsMatch(line, $@"\[""{escapedField}""\]\s*=\s*null\b") ||
            Regex.IsMatch(line, $@"\.Add\(""{escapedField}"",\s*null\b"))
        {
            hints.Add("null");
        }

        if (line.Contains($"[\"{field}\"] = \"", StringComparison.Ordinal) ||
            line.Contains($".Add(\"{field}\", \"", StringComparison.Ordinal) ||
            line.Contains($"[\"{field}\"] = $\"", StringComparison.Ordinal) ||
            line.Contains($".Add(\"{field}\", $\"", StringComparison.Ordinal))
        {
            hints.Add("string");
        }

        if (Regex.IsMatch(line, $@"\[""{escapedField}""\]\s*=\s*-?\d+(\.\d+)?\b") ||
            Regex.IsMatch(line, $@"\.Add\(""{escapedField}"",\s*-?\d+(\.\d+)?\b"))
        {
            hints.Add("number");
        }

        if (line.Contains($"[\"{field}\"] =", StringComparison.Ordinal) ||
            line.Contains($".Add(\"{field}\",", StringComparison.Ordinal) ||
            line.Contains($"[\"{field}\"]?.", StringComparison.Ordinal) ||
            line.Contains($"[\"{field}\"] is", StringComparison.Ordinal))
        {
            hints.Add("json");
        }

        return hints.Count > 0 ? hints.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : ["json"];
    }

    /// <summary>
    /// 根据正则匹配收集类型线索。
    /// </summary>
    private static void CollectMatch(HashSet<string> hints, string line, string pattern, Func<string, string> normalize)
    {
        var match = Regex.Match(line, pattern);
        if (!match.Success)
        {
            return;
        }

        var type = match.Groups["type"].Value;
        if (!string.IsNullOrWhiteSpace(type))
        {
            hints.Add(normalize(type));
        }
    }

    /// <summary>
    /// 规范化 JsonNode 相关类型名称，便于报告展示。
    /// </summary>
    private static string NormalizeJsonNodeType(string type)
    {
        return type switch
        {
            "JsonArray" => "array",
            "JsonObject" => "object",
            "JsonValue" => "scalar",
            _ => type
        };
    }

    /// <summary>
    /// 规范化 C# 泛型类型名称，尽量映射到更直观的 JSON 类型概念。
    /// </summary>
    private static string NormalizeCSharpType(string type)
    {
        var normalized = type.Trim();
        return normalized switch
        {
            "string" => "string",
            "bool" => "bool",
            "int" or "long" or "short" or "uint" or "ulong" or "float" or "double" or "decimal" => "number",
            _ => normalized
        };
    }

    /// <summary>
    /// 判断是否为非协议特定字段的通用名称（如 role、type 等过于通用的词不单独计）。
    /// </summary>
    private static bool IsCommonNonProtocolField(string field)
    {
        return false;
    }
}

/// <summary>
/// 当前项目中某个 JSON 字段的使用线索。
/// </summary>
internal sealed class CurrentFieldUsage(string fieldName)
{
    public string FieldName { get; } = fieldName;
    public HashSet<string> TypeHints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CurrentFieldLocation> Locations { get; } = [];

    /// <summary>
    /// 添加一次字段使用记录。
    /// </summary>
    public void AddUsage(string filePath, int lineNumber, IReadOnlyList<string> typeHints)
    {
        foreach (var typeHint in typeHints)
        {
            TypeHints.Add(typeHint);
        }

        if (Locations.Any(location =>
                string.Equals(location.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
                && location.LineNumber == lineNumber))
        {
            return;
        }

        Locations.Add(new CurrentFieldLocation(filePath, lineNumber));
    }

    /// <summary>
    /// 用于报告展示的当前项目类型线索。
    /// </summary>
    public string DisplayTypeHints => TypeHints.Count == 0
        ? "json"
        : string.Join(" / ", TypeHints.OrderBy(type => type, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// 当前项目某个字段的一处代码位置。
/// </summary>
internal sealed record CurrentFieldLocation(string FilePath, int LineNumber);
