namespace ProtocolSyncCheck;

/// <summary>
/// 将 new-api 的 Go struct 按协议接口分组，并与当前项目实际处理的字段做对比。
/// </summary>
internal static class FieldDiffEngine
{
    /// <summary>
    /// 将 new-api 的 Go struct 映射到协议接口分组。
    /// 每个分组代表一个 API 接口的请求体或响应体。
    /// </summary>
    public static List<ProtocolStructGroup> BuildGroups(List<GoStructDefinition> structs)
    {
        var structIndex = structs.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var groups = new List<ProtocolStructGroup>();

        // OpenAI Chat Completions 请求
        AddGroup(groups, structIndex, "OpenAI Chat Completions 请求",
            ["GeneralOpenAIRequest"],
            "OpenAI Chat/Completions/Embeddings 统一请求体（model, messages, stream, tools 等）");

        // OpenAI Chat Completions 非流式响应
        AddGroup(groups, structIndex, "OpenAI Chat Completions 响应",
            ["OpenAITextResponse", "OpenAITextResponseChoice"],
            "Chat Completions 非流式响应（id, choices, usage 等）");

        // OpenAI Chat Completions 流式 chunk
        AddGroup(groups, structIndex, "OpenAI Chat Completions 流式 chunk",
            ["ChatCompletionsStreamResponse", "ChatCompletionsStreamResponseChoice", "ChatCompletionsStreamResponseChoiceDelta"],
            "Chat Completions SSE 流式响应 chunk");

        // OpenAI Chat Completions 消息内容
        AddGroup(groups, structIndex, "OpenAI 消息体",
            ["Message", "MediaContent"],
            "请求中的 messages 数组元素及其多媒体内容块");

        // OpenAI 工具调用
        AddGroup(groups, structIndex, "OpenAI 工具调用",
            ["ToolCallRequest", "ToolCallResponse", "FunctionRequest", "FunctionResponse"],
            "请求和响应中的工具定义与调用");

        // OpenAI Usage
        AddGroup(groups, structIndex, "OpenAI Usage",
            ["Usage", "InputTokenDetails", "OutputTokenDetails"],
            "Token 用量统计（含缓存和分类详情）");

        // OpenAI Responses 请求
        AddGroup(groups, structIndex, "OpenAI Responses 请求",
            ["OpenAIResponsesRequest"],
            "Responses API 请求体");

        // OpenAI Responses 响应
        AddGroup(groups, structIndex, "OpenAI Responses 响应",
            ["OpenAIResponsesResponse", "ResponsesOutput", "ResponsesOutputContent"],
            "Responses API 非流式响应");

        // OpenAI Responses 流式事件
        AddGroup(groups, structIndex, "OpenAI Responses 流式事件",
            ["ResponsesStreamResponse"],
            "Responses API SSE 流式事件");

        // OpenAI Embeddings 请求
        AddGroup(groups, structIndex, "OpenAI Embeddings 请求",
            ["EmbeddingRequest"],
            "Embeddings API 请求体");

        // OpenAI Embeddings 响应
        AddGroup(groups, structIndex, "OpenAI Embeddings 响应",
            ["EmbeddingResponse", "EmbeddingResponseItem"],
            "Embeddings API 响应体");

        // Anthropic Messages 请求
        AddGroup(groups, structIndex, "Anthropic Messages 请求",
            ["ClaudeRequest", "ClaudeMessage"],
            "Anthropic Messages API 请求体");

        // Anthropic Messages 响应
        AddGroup(groups, structIndex, "Anthropic Messages 响应",
            ["ClaudeResponse", "ClaudeMediaMessage"],
            "Anthropic Messages API 响应和 SSE 事件");

        // Anthropic Usage
        AddGroup(groups, structIndex, "Anthropic Usage",
            ["ClaudeUsage", "ClaudeCacheCreationUsage"],
            "Anthropic token 用量统计");

        // Anthropic 工具
        AddGroup(groups, structIndex, "Anthropic 工具",
            ["Tool", "ClaudeWebSearchTool", "ClaudeToolChoice"],
            "Anthropic 工具定义与选择");

        // Anthropic Thinking
        AddGroup(groups, structIndex, "Anthropic Thinking",
            ["Thinking"],
            "Anthropic thinking 配置（budget_tokens 等）");

        // Legacy Completions 流式响应
        AddGroup(groups, structIndex, "Legacy Completions 流式响应",
            ["CompletionsStreamResponse"],
            "Legacy Completions SSE 流式响应");

        return groups;
    }

    /// <summary>
    /// 计算每个分组中 new-api 有但当前项目未处理的字段。
    /// </summary>
    public static List<FieldDiffResult> ComputeDiffs(
        List<ProtocolStructGroup> groups,
        HashSet<string> currentProjectFields)
    {
        var results = new List<FieldDiffResult>();

        foreach (var group in groups)
        {
            var allRefFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in group.Fields)
            {
                allRefFields.Add(field.JsonName);
            }

            var missing = allRefFields
                .Where(f => !currentProjectFields.Contains(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var matched = allRefFields
                .Where(f => currentProjectFields.Contains(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            results.Add(new FieldDiffResult(group, matched, missing));
        }

        return results;
    }

    private static void AddGroup(
        List<ProtocolStructGroup> groups,
        Dictionary<string, GoStructDefinition> structIndex,
        string label,
        string[] structNames,
        string description)
    {
        var fields = new List<GoStructField>();
        var foundStructs = new List<string>();

        foreach (var name in structNames)
        {
            if (structIndex.TryGetValue(name, out var def))
            {
                fields.AddRange(def.Fields);
                foundStructs.Add(name);
            }
        }

        if (fields.Count > 0)
        {
            groups.Add(new ProtocolStructGroup(label, description, foundStructs, fields));
        }
    }
}

/// <summary>
/// 一个协议接口对应的 struct 字段分组。
/// </summary>
internal sealed class ProtocolStructGroup(
    string label,
    string description,
    List<string> structNames,
    List<GoStructField> fields)
{
    public string Label { get; } = label;
    public string Description { get; } = description;
    public List<string> StructNames { get; } = structNames;
    public List<GoStructField> Fields { get; } = fields;
}

/// <summary>
/// 字段对比结果。
/// </summary>
internal sealed class FieldDiffResult(
    ProtocolStructGroup group,
    List<string> matchedFields,
    List<string> missingFields)
{
    public ProtocolStructGroup Group { get; } = group;
    public List<string> MatchedFields { get; } = matchedFields;
    public List<string> MissingFields { get; } = missingFields;
    public bool HasMissing => MissingFields.Count > 0;
}
