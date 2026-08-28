using System.Text.RegularExpressions;

namespace ProtocolSyncCheck;

/// <summary>
/// 为 cc-switch（Rust / serde_json）构建字段级对齐分组。
/// cc-switch 的协议转换集中在 providers/transform*.rs 与 streaming*.rs，
/// 按转换方向分组（每组的字段 = 该方向上被读写的 JSON 键），
/// 与 AITool 协议桥的 C# 实现做同名字段对齐。Gemini 方向 AITool 暂不支持，不纳入基线。
/// </summary>
internal static class CcSwitchFieldGroupBuilder
{
    public static List<ProtocolStructGroup> BuildGroups(string repositoryRoot)
    {
        var providersRoot = Path.Combine(repositoryRoot, "reference-projects", "cc-switch", "src-tauri", "src", "proxy", "providers");

        // 分组按“转换方向”命名：组内字段是该方向两侧协议的 JSON 键并集。
        var definitions = new (string Label, string Description, string[] RouteKeys, string[] Files)[]
        {
            (
                "Anthropic ↔ Chat 转换（cc-switch）",
                "基于 cc-switch transform.rs（Anthropic Messages ↔ OpenAI Chat 请求/响应转换）扫描的字段基线。",
                ["Anthropic:POST /v1/messages", "OpenAI:POST /v1/chat/completions"],
                ["transform.rs"]),
            (
                "Anthropic ↔ Responses 转换（cc-switch）",
                "基于 cc-switch transform_responses.rs（Anthropic Messages ↔ OpenAI Responses 转换）扫描的字段基线。",
                ["Anthropic:POST /v1/messages", "OpenAI:POST /v1/responses"],
                ["transform_responses.rs"]),
            (
                "Responses ↔ Chat 转换（cc-switch）",
                "基于 cc-switch transform_codex_chat.rs（Responses ↔ Chat 请求/响应转换）扫描的字段基线。",
                ["OpenAI:POST /v1/responses", "OpenAI:POST /v1/chat/completions"],
                ["transform_codex_chat.rs"]),
            (
                "Responses ↔ Anthropic 转换（cc-switch）",
                "基于 cc-switch transform_codex_anthropic.rs（Responses ↔ Anthropic 转换，含 thinking 签名桥接）扫描的字段基线。",
                ["OpenAI:POST /v1/responses", "Anthropic:POST /v1/messages"],
                ["transform_codex_anthropic.rs"]),
            (
                "xAI Responses 规范化（cc-switch）",
                "基于 cc-switch namespace flatten / xai sanitize（Responses 请求规范化）扫描的字段基线。",
                ["OpenAI:POST /v1/responses"],
                ["transform_codex_responses_namespace.rs", "transform_codex_responses_xai_sanitize.rs"]),
            (
                "Chat → Anthropic 流式（cc-switch）",
                "基于 cc-switch streaming.rs（OpenAI Chat SSE → Anthropic SSE 状态机）扫描的字段基线。",
                ["Anthropic:POST /v1/messages"],
                ["streaming.rs"]),
            (
                "Responses → Anthropic 流式（cc-switch）",
                "基于 cc-switch streaming_responses.rs（Responses SSE → Anthropic SSE）扫描的字段基线。",
                ["Anthropic:POST /v1/messages"],
                ["streaming_responses.rs"]),
            (
                "Chat → Responses 流式（cc-switch）",
                "基于 cc-switch streaming_codex_chat.rs（Chat SSE → Responses 事件）扫描的字段基线。",
                ["OpenAI:POST /v1/responses"],
                ["streaming_codex_chat.rs"]),
            (
                "Anthropic → Responses 流式（cc-switch）",
                "基于 cc-switch streaming_codex_anthropic.rs（Anthropic SSE → Responses 事件）扫描的字段基线。",
                ["OpenAI:POST /v1/responses"],
                ["streaming_codex_anthropic.rs"])
        };

        var groups = new List<ProtocolStructGroup>();
        foreach (var definition in definitions)
        {
            var usages = new Dictionary<string, RustFieldUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in definition.Files)
            {
                var filePath = Path.Combine(providersRoot, file);
                foreach (var (field, typeHint) in RustJsonFieldScanner.ScanFile(filePath))
                {
                    if (!usages.TryGetValue(field, out var usage))
                    {
                        usage = new RustFieldUsage(field);
                        usages[field] = usage;
                    }

                    usage.AddTypeHint(typeHint);
                }
            }

            if (usages.Count == 0)
            {
                continue;
            }

            var fields = usages.Values
                .OrderBy(usage => usage.FieldName, StringComparer.OrdinalIgnoreCase)
                .Select(usage => new ProtocolField(usage.FieldName, usage.ReferenceTypeHint, usage.FieldName, true))
                .ToList();
            groups.Add(new ProtocolStructGroup(definition.Label, definition.Description, definition.RouteKeys, [], fields));
        }

        return groups;
    }
}

/// <summary>
/// 从 Rust serde_json 动态处理代码中提取 JSON 字段与类型线索。
/// 覆盖 cc-switch 的主要访问形态：索引取值、.get()、json! 宏字面量、.insert()。
/// </summary>
internal static class RustJsonFieldScanner
{
    /// <summary>索引访问带类型：["field"].as_str() / ["field"]?.as_i64()。</summary>
    private static readonly Regex IndexAccessorRegex = new(
        @"\[""(?<field>[a-zA-Z_][a-zA-Z0-9_]*)""\]\s*(?:\?|\.)?\s*as_(?<accessor>str|i64|u64|f64|bool|array|object)\(\)",
        RegexOptions.Compiled);

    /// <summary>索引访问不带类型：["field"]（作为值读取或赋值目标）。</summary>
    private static readonly Regex IndexRegex = new(
        @"\[""(?<field>[a-zA-Z_][a-zA-Z0-9_]*)""\]",
        RegexOptions.Compiled);

    /// <summary>.get("field") 带类型：.get("field").and_then(|v| v.as_str())。</summary>
    private static readonly Regex GetAccessorRegex = new(
        @"\.get\(""(?<field>[a-zA-Z_][a-zA-Z0-9_]*)""\)[^\n;]{0,60}?as_(?<accessor>str|i64|u64|f64|bool|array|object)\(\)",
        RegexOptions.Compiled);

    /// <summary>.get("field") 不带类型。</summary>
    private static readonly Regex GetRegex = new(
        @"\.get\(""(?<field>[a-zA-Z_][a-zA-Z0-9_]*)""\)",
        RegexOptions.Compiled);

    /// <summary>json! 宏 / 结构字面量键值对："field": value。</summary>
    private static readonly Regex MapKeyRegex = new(
        @"""(?<field>[a-z_][a-z0-9_]*)""\s*:\s*(?<value>[^\n,}]+)",
        RegexOptions.Compiled);

    /// <summary>HashMap/JsonObject 插入：.insert("field", value)。</summary>
    private static readonly Regex InsertRegex = new(
        @"\.insert\(\s*""(?<field>[a-zA-Z_][a-zA-Z0-9_]*)""\s*,\s*(?<value>[^\n;)]+)",
        RegexOptions.Compiled);

    public static List<(string Field, string TypeHint)> ScanFile(string filePath)
    {
        var result = new List<(string, string)>();
        if (!File.Exists(filePath))
        {
            return result;
        }

        var content = File.ReadAllText(filePath);
        Collect(result, content, IndexAccessorRegex, m =>
            (m.Groups["field"].Value, NormalizeAccessor(m.Groups["accessor"].Value)));
        Collect(result, content, IndexRegex, m => (m.Groups["field"].Value, "json"));
        Collect(result, content, GetAccessorRegex, m =>
            (m.Groups["field"].Value, NormalizeAccessor(m.Groups["accessor"].Value)));
        Collect(result, content, GetRegex, m => (m.Groups["field"].Value, "json"));
        Collect(result, content, MapKeyRegex, m =>
            (m.Groups["field"].Value, InferValueType(m.Groups["value"].Value)));
        Collect(result, content, InsertRegex, m =>
            (m.Groups["field"].Value, InferValueType(m.Groups["value"].Value)));
        return result;
    }

    private static void Collect(
        List<(string Field, string TypeHint)> result,
        string content,
        Regex regex,
        Func<Match, (string Field, string TypeHint)> selector)
    {
        foreach (Match match in regex.Matches(content))
        {
            var (field, typeHint) = selector(match);
            if (string.IsNullOrWhiteSpace(field) || IsIgnoredField(field))
            {
                continue;
            }

            result.Add((field, typeHint));
        }
    }

    private static string NormalizeAccessor(string accessor) => accessor switch
    {
        "str" => "string",
        "i64" or "u64" or "f64" => "number",
        "bool" => "bool",
        "array" => "array",
        "object" => "object",
        _ => "json"
    };

    private static string InferValueType(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('"') || trimmed.StartsWith('#'))
        {
            return "string";
        }

        if (trimmed is "true" or "false")
        {
            return "bool";
        }

        if (Regex.IsMatch(trimmed, @"^-?\d+(\.\d+)?$"))
        {
            return "number";
        }

        if (trimmed.Contains("json!([") || trimmed.StartsWith("vec![") || trimmed.StartsWith("Vec::new"))
        {
            return "array";
        }

        if (trimmed.Contains("json!({") || trimmed.StartsWith("Map::new") || trimmed.StartsWith("JsonObject"))
        {
            return "object";
        }

        if (trimmed.Contains("Value::Null"))
        {
            return "null";
        }

        return "json";
    }

    /// <summary>
    /// 过滤非协议语义的键：Rust 侧内部状态键与显然的实现细节。
    /// </summary>
    private static bool IsIgnoredField(string field)
    {
        // 与 Go 扫描器同源的内部实现键；type/text/index/message/data 等流式语义键保留。
        return field is "arr" or "background" or "sequence_number" or "summary_index"
            or "computer_use_preview" or "citations_delta"
            or "xhigh" or "ultra" or "cls" or "self" or "id_" or "value_kind";
    }
}

/// <summary>
/// Rust 动态处理代码中某个字段的使用线索。
/// </summary>
internal sealed class RustFieldUsage(string fieldName)
{
    public string FieldName { get; } = fieldName;
    public HashSet<string> TypeHints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ReferenceTypeHint => TypeHints.Count == 0
        ? "json"
        : string.Join(" / ", TypeHints.OrderBy(type => type, StringComparer.OrdinalIgnoreCase));

    public void AddTypeHint(string typeHint)
    {
        if (!string.IsNullOrWhiteSpace(typeHint))
        {
            TypeHints.Add(typeHint);
        }
    }
}
