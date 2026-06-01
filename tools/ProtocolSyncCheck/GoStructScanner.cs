namespace ProtocolSyncCheck;

using System.Text.RegularExpressions;

/// <summary>
/// 从 Go 源码文件中自动提取 struct 定义及其 json tag 字段。
/// </summary>
internal static class GoStructScanner
{
    private static readonly Regex StructHeaderRegex = new(
        @"type\s+(?<name>\w+)\s+struct\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex FieldRegex = new(
        @"^\s+(?<name>\w+)\s+(?<type>[^\s]+)\s+.*json:""(?<tag>[^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// 从指定 Go 源码文件中提取所有 struct 及其 json tag 字段。
    /// </summary>
    public static List<GoStructDefinition> ScanFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var structs = new List<GoStructDefinition>();
        var lines = File.ReadAllLines(filePath);
        GoStructDefinition? current = null;

        foreach (var line in lines)
        {
            var headerMatch = StructHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                current = new GoStructDefinition(headerMatch.Groups["name"].Value, filePath);
                structs.Add(current);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            // struct 结束
            if (line.TrimStart().StartsWith('}') && !line.Contains("struct", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }

            // 提取字段
            var fieldMatch = FieldRegex.Match(line);
            if (fieldMatch.Success)
            {
                var tag = fieldMatch.Groups["tag"].Value;
                var jsonName = ExtractJsonName(tag);
                if (jsonName is not null && jsonName != "-")
                {
                    current.Fields.Add(new GoStructField(
                        fieldMatch.Groups["name"].Value,
                        fieldMatch.Groups["type"].Value,
                        jsonName,
                        tag.Contains(",omitempty")));
                }
            }
        }

        return structs;
    }

    /// <summary>
    /// 从目录下的所有 .go 文件中扫描 struct 定义。
    /// </summary>
    public static List<GoStructDefinition> ScanDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        var allStructs = new List<GoStructDefinition>();
        foreach (var goFile in Directory.GetFiles(directoryPath, "*.go"))
        {
            allStructs.AddRange(ScanFile(goFile));
        }

        return allStructs;
    }

    /// <summary>
    /// 从 json tag 中提取 JSON 字段名。
    /// </summary>
    private static string? ExtractJsonName(string tag)
    {
        var parts = tag.Split(',');
        return parts.Length > 0 ? parts[0] : null;
    }
}

/// <summary>
/// 一个 Go struct 的完整定义。
/// </summary>
internal sealed class GoStructDefinition(string name, string sourceFile)
{
    public string Name { get; } = name;
    public string SourceFile { get; } = sourceFile;
    public List<GoStructField> Fields { get; } = [];

    public string SourceFileName => Path.GetFileName(SourceFile);
}

/// <summary>
/// Go struct 中的一个字段。
/// </summary>
internal sealed record GoStructField(
    string GoName,
    string GoType,
    string JsonName,
    bool OmitEmpty);
