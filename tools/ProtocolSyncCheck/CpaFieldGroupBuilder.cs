using System.Text.RegularExpressions;

namespace ProtocolSyncCheck;

/// <summary>
/// 为 CPA / CLIProxyAPI 构建字段级对齐分组。
/// 由于 CPA 大量使用动态 JSON 读写，这里基于关键处理函数中实际出现的字段访问来建立基线。
/// 分组同时覆盖 handler 入口函数和协议转换函数（internal/translator），
/// 转换函数是 AITool 协议桥接（OpenAI ↔ Anthropic）的直接参照。
/// </summary>
internal static class CpaFieldGroupBuilder
{
    /// <summary>
    /// 构建 CPA 的字段分组。
    /// </summary>
    public static List<ProtocolStructGroup> BuildGroups(string repositoryRoot)
    {
        var cpaRoot = Path.Combine(repositoryRoot, "reference-projects", "CLIProxyAPI");
        var groups = new List<ProtocolStructGroup>();

        AddGroup(groups,
            "OpenAI Chat Completions 请求（CPA）",
            "基于 CPA OpenAI Chat/Completions 请求处理与 OpenAI→Claude 请求转换代码扫描得到的字段基线。",
            ["OpenAI:POST /v1/chat/completions"],
            [
                (Path.Combine(cpaRoot, "sdk", "api", "handlers", "openai", "openai_handlers.go"),
                    [
                        "func (h *OpenAIAPIHandler) ChatCompletions(c *gin.Context)",
                        "func shouldTreatAsResponsesFormat(rawJSON []byte) bool"
                    ]),
                (Path.Combine(cpaRoot, "internal", "translator", "claude", "openai", "chat-completions", "claude_openai_request.go"),
                    [])
            ]);

        AddGroup(groups,
            "OpenAI Completions 请求（legacy，CPA）",
            "基于 CPA legacy Completions → Chat Completions 请求转换代码扫描得到的字段基线。",
            ["OpenAI:POST /v1/completions"],
            [
                (Path.Combine(cpaRoot, "sdk", "api", "handlers", "openai", "openai_handlers.go"),
                    [
                        "func convertCompletionsRequestToChatCompletions(rawJSON []byte) []byte"
                    ])
            ]);

        AddGroup(groups,
            "OpenAI Chat Completions 响应（CPA）",
            "基于 CPA OpenAI Chat/Completions 响应转换与 OpenAI→Claude 响应转换代码扫描得到的字段基线。",
            ["OpenAI:POST /v1/chat/completions"],
            [
                (Path.Combine(cpaRoot, "sdk", "api", "handlers", "openai", "openai_handlers.go"),
                    [
                        "func convertChatCompletionsResponseToCompletions(rawJSON []byte) []byte",
                        "func convertChatCompletionsStreamChunkToCompletions(chunkData []byte) []byte"
                    ]),
                (Path.Combine(cpaRoot, "internal", "translator", "openai", "claude", "openai_claude_response.go"),
                    [])
            ]);

        AddGroup(groups,
            "OpenAI Responses 请求（CPA）",
            "基于 CPA OpenAI Responses 请求处理与 Responses→Claude 请求转换代码扫描得到的字段基线。",
            ["OpenAI:POST /v1/responses"],
            [
                (Path.Combine(cpaRoot, "sdk", "api", "handlers", "openai", "openai_responses_handlers.go"),
                    [
                        "func (h *OpenAIResponsesAPIHandler) Responses(c *gin.Context)",
                        "func (h *OpenAIResponsesAPIHandler) Compact(c *gin.Context)"
                    ]),
                (Path.Combine(cpaRoot, "internal", "translator", "claude", "openai", "responses", "claude_openai-responses_request.go"),
                    [])
            ]);

        AddGroup(groups,
            "OpenAI Responses 响应（CPA）",
            "基于 CPA Responses 流式输出与 OpenAI chat 响应→Responses 转换代码扫描得到的字段基线。",
            ["OpenAI:POST /v1/responses"],
            [
                (Path.Combine(cpaRoot, "sdk", "api", "handlers", "openai", "openai_responses_handlers.go"),
                    [
                        "func (h *OpenAIResponsesAPIHandler) OpenAIResponsesModels(c *gin.Context)",
                        "func (h *OpenAIResponsesAPIHandler) forwardResponsesStream(c *gin.Context, flusher http.Flusher, cancel func(error), data <-chan []byte, errs <-chan *interfaces.ErrorMessage, framer *responsesSSEFramer)"
                    ]),
                (Path.Combine(cpaRoot, "internal", "translator", "openai", "openai", "responses", "openai_openai-responses_response.go"),
                    [])
            ]);

        AddGroup(groups,
            "Anthropic Messages 请求（CPA）",
            "基于 CPA Anthropic Messages 请求处理与 Claude→OpenAI 请求转换代码扫描得到的字段基线。",
            ["Anthropic:POST /v1/messages"],
            [
                (Path.Combine(cpaRoot, "sdk", "api", "handlers", "claude", "code_handlers.go"),
                    [
                        "func (h *ClaudeCodeAPIHandler) ClaudeMessages(c *gin.Context)",
                        "func (h *ClaudeCodeAPIHandler) ClaudeCountTokens(c *gin.Context)"
                    ]),
                (Path.Combine(cpaRoot, "internal", "translator", "openai", "claude", "openai_claude_request.go"),
                    [])
            ]);

        AddGroup(groups,
            "Anthropic Messages 响应（CPA）",
            "基于 CPA Claude→OpenAI 响应转换代码扫描得到的字段基线。",
            ["Anthropic:POST /v1/messages"],
            [
                (Path.Combine(cpaRoot, "internal", "translator", "claude", "openai", "chat-completions", "claude_openai_response.go"),
                    [])
            ]);

        AddGroup(groups,
            "Anthropic Models 响应（CPA）",
            "基于 CPA Anthropic Models 响应输出代码扫描得到的字段基线。",
            ["Anthropic:GET /v1/models", "Anthropic:GET /v1/models/:model"],
            [
                (Path.Combine(cpaRoot, "sdk", "api", "handlers", "claude", "code_handlers.go"),
                    [
                        "func (h *ClaudeCodeAPIHandler) ClaudeModels(c *gin.Context)"
                    ])
            ]);

        return groups;
    }

    /// <summary>
    /// 从指定文件和函数列表构建一个字段分组。
    /// </summary>
    private static void AddGroup(
        List<ProtocolStructGroup> groups,
        string label,
        string description,
        string[] routeKeys,
        (string FilePath, string[] FunctionMarkers)[] sources)
    {
        var usages = new Dictionary<string, GoDynamicFieldUsage>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, functionMarkers) in sources)
        {
            var scanned = functionMarkers.Length == 0
                ? GoDynamicFieldScanner.ScanFile(filePath)
                : GoDynamicFieldScanner.ScanFunctions(filePath, functionMarkers);
            foreach (var (field, usage) in scanned)
            {
                if (!usages.TryGetValue(field, out var merged))
                {
                    merged = new GoDynamicFieldUsage(field);
                    usages[field] = merged;
                }

                foreach (var hint in usage.TypeHints)
                {
                    merged.AddTypeHint(hint);
                }
            }
        }

        if (usages.Count == 0)
        {
            return;
        }

        var fields = usages.Values
            .OrderBy(usage => usage.FieldName, StringComparer.OrdinalIgnoreCase)
            .Select(usage => new ProtocolField(
                usage.FieldName,
                usage.ReferenceTypeHint,
                usage.FieldName,
                true))
            .ToList();

        groups.Add(new ProtocolStructGroup(label, description, routeKeys, [], fields));
    }
}

/// <summary>
/// 从 Go 动态 JSON 处理代码中提取字段及类型线索。
/// </summary>
internal static class GoDynamicFieldScanner
{
    private static readonly Regex GjsonAccessorRegex = new(
        @"gjson\.Get(?:Bytes)?\([^,]+,\s*""(?<path>[^""]+)""\)\.(?<accessor>String|Bool|Int|Float)\(\)",
        RegexOptions.Compiled);

    private static readonly Regex GjsonPathRegex = new(
        @"gjson\.Get(?:Bytes)?\([^,]+,\s*""(?<path>[^""]+)""\)",
        RegexOptions.Compiled);

    private static readonly Regex GenericGetAccessorRegex = new(
        @"\.Get\(""(?<path>[^""]+)""\)\.(?<accessor>String|Bool|Int|Float)\(\)",
        RegexOptions.Compiled);

    private static readonly Regex GenericGetRegex = new(
        @"\.Get\(""(?<path>[^""]+)""\)",
        RegexOptions.Compiled);

    private static readonly Regex SjsonSetRegex = new(
        @"sjson\.SetBytes\([^,]+,\s*""(?<path>[^""]+)""\s*,\s*(?<value>[^\n;]+)",
        RegexOptions.Compiled);

    private static readonly Regex SjsonSetRawRegex = new(
        @"sjson\.SetRawBytes\([^,]+,\s*""(?<path>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex MapKeyRegex = new(
        @"""(?<field>[a-z_][a-z0-9_]*)""\s*:\s*(?<value>[^\n,]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// 扫描整个文件中的字段。
    /// </summary>
    public static Dictionary<string, GoDynamicFieldUsage> ScanFile(string filePath)
    {
        var result = new Dictionary<string, GoDynamicFieldUsage>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath))
        {
            return result;
        }

        CollectMatches(result, File.ReadAllText(filePath));
        return result;
    }

    /// <summary>
    /// 扫描指定函数列表中的字段。
    /// </summary>
    public static Dictionary<string, GoDynamicFieldUsage> ScanFunctions(string filePath, IReadOnlyList<string> functionMarkers)
    {
        var result = new Dictionary<string, GoDynamicFieldUsage>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath))
        {
            return result;
        }

        var content = File.ReadAllText(filePath);
        foreach (var marker in functionMarkers)
        {
            var block = ExtractFunctionBlock(content, marker);
            if (string.IsNullOrWhiteSpace(block))
            {
                continue;
            }

            CollectMatches(result, block);
        }

        return result;
    }

    private static void CollectMatches(Dictionary<string, GoDynamicFieldUsage> result, string block)
    {
        CollectMatches(result, block, GjsonAccessorRegex, match =>
            (NormalizePath(match.Groups["path"].Value), NormalizeAccessorType(match.Groups["accessor"].Value)));
        CollectMatches(result, block, GjsonPathRegex, match =>
            (NormalizePath(match.Groups["path"].Value), "json"));
        CollectMatches(result, block, GenericGetAccessorRegex, match =>
            (NormalizePath(match.Groups["path"].Value), NormalizeAccessorType(match.Groups["accessor"].Value)));
        CollectMatches(result, block, GenericGetRegex, match =>
            (NormalizePath(match.Groups["path"].Value), "json"));
        CollectMatches(result, block, SjsonSetRegex, match =>
            (NormalizePath(match.Groups["path"].Value), InferValueType(match.Groups["value"].Value)));
        CollectMatches(result, block, SjsonSetRawRegex, match =>
            (NormalizePath(match.Groups["path"].Value), "json"));
        CollectMatches(result, block, MapKeyRegex, match =>
            (match.Groups["field"].Value, InferValueType(match.Groups["value"].Value)));
    }

    /// <summary>
    /// 收集一次正则扫描命中的字段。
    /// </summary>
    private static void CollectMatches(
        Dictionary<string, GoDynamicFieldUsage> result,
        string block,
        Regex regex,
        Func<Match, (string Field, string TypeHint)> selector)
    {
        foreach (Match match in regex.Matches(block))
        {
            var (field, typeHint) = selector(match);
            if (string.IsNullOrWhiteSpace(field) || IsIgnoredField(field))
            {
                continue;
            }

            if (!result.TryGetValue(field, out var usage))
            {
                usage = new GoDynamicFieldUsage(field);
                result[field] = usage;
            }

            usage.AddTypeHint(typeHint);
        }
    }

    /// <summary>
    /// 从文件全文中提取函数体。
    /// </summary>
    private static string ExtractFunctionBlock(string content, string marker)
    {
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var braceStart = content.IndexOf('{', start);
        if (braceStart < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var i = braceStart; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                depth++;
            }
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return content.Substring(start, i - start + 1);
                }
            }
        }

        return content[start..];
    }

    /// <summary>
    /// 将 JSON path 归一化为首段字段名，用于接口级字段对齐。
    /// </summary>
    private static string NormalizePath(string path)
    {
        var firstSegment = path.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? path;
        var bracketIndex = firstSegment.IndexOf('[');
        if (bracketIndex >= 0)
        {
            firstSegment = firstSegment[..bracketIndex];
        }

        return firstSegment.Trim();
    }

    /// <summary>
    /// 根据 gjson 访问器推断类型。
    /// </summary>
    private static string NormalizeAccessorType(string accessor)
    {
        return accessor switch
        {
            "String" => "string",
            "Bool" => "bool",
            "Int" or "Float" => "number",
            _ => "json"
        };
    }

    /// <summary>
    /// 根据赋值表达式推断类型。
    /// </summary>
    private static string InferValueType(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains(".String()", StringComparison.Ordinal))
        {
            return "string";
        }

        if (trimmed.Contains(".Bool()", StringComparison.Ordinal) || trimmed is "true" or "false")
        {
            return "bool";
        }

        if (trimmed.Contains(".Int()", StringComparison.Ordinal)
            || trimmed.Contains(".Float()", StringComparison.Ordinal)
            || Regex.IsMatch(trimmed, @"^-?\d+(\.\d+)?$"))
        {
            return "number";
        }

        if (trimmed.StartsWith("[]", StringComparison.Ordinal) || trimmed.Contains("make([]", StringComparison.Ordinal))
        {
            return "array";
        }

        if (trimmed.StartsWith("map[", StringComparison.Ordinal)
            || trimmed.StartsWith("gin.H", StringComparison.Ordinal)
            || trimmed.StartsWith("map[string]", StringComparison.Ordinal))
        {
            return "object";
        }

        if (trimmed.StartsWith("\"", StringComparison.Ordinal) || trimmed.StartsWith("`", StringComparison.Ordinal))
        {
            return "string";
        }

        return "json";
    }

    /// <summary>
    /// 过滤不会单独表达协议字段含义的通用包装键。
    /// </summary>
    private static bool IsIgnoredField(string field)
    {
        // 保留 type、text、index、message 和 data，它们在 Responses/Anthropic 流式事件中具有协议语义。
        // 以下为转换器内部实现键，不属于 OpenAI/Anthropic 协议字段。
        return field is "error" or "object"
            or "arr" or "background" or "custom" or "custom_tool_call" or "custom_tool_call_output"
            or "sequence_number" or "summary" or "summary_index" or "user_id" or "user_location"
            or "allowed_domains" or "computer_use_preview" or "safety_identifier" or "max_uses"
            or "encrypted_content" or "signature" or "signature_delta" or "citations_delta"
            or "incomplete_details" or "any" or "auto" or "none" or "enabled" or "disabled"
            or "required" or "namespace" or "input_file" or "input_image" or "output_text"
            or "base64";
    }
}

/// <summary>
/// Go 动态处理代码中某个字段的使用线索。
/// </summary>
internal sealed class GoDynamicFieldUsage(string fieldName)
{
    public string FieldName { get; } = fieldName;
    public HashSet<string> TypeHints { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 用于报告展示的参考类型线索。
    /// </summary>
    public string ReferenceTypeHint => TypeHints.Count == 0
        ? "json"
        : string.Join(" / ", TypeHints.OrderBy(type => type, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 添加一次类型线索。
    /// </summary>
    public void AddTypeHint(string typeHint)
    {
        if (!string.IsNullOrWhiteSpace(typeHint))
        {
            TypeHints.Add(typeHint);
        }
    }
}
