using System.Text.RegularExpressions;

namespace ProtocolSyncCheck;

/// <summary>
/// 为 new-api 构建接口级字段分组。
/// 请求类分组基于实际处理函数中的字段访问动态提取，避免再用写死字段白名单。
/// 响应类分组仍直接基于对应 DTO 结构体，因为这些结构本身就是明确的接口响应模型。
/// </summary>
internal static class NewApiFieldGroupBuilder
{
    /// <summary>
    /// 构建 new-api 的接口字段分组。
    /// </summary>
    public static List<ProtocolStructGroup> BuildGroups(string repositoryRoot, List<GoStructDefinition> structs)
    {
        var structIndex = structs.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var newApiRoot = Path.Combine(repositoryRoot, "reference-projects", "new-api");
        var groups = new List<ProtocolStructGroup>();

        AddDynamicRequestGroup(
            groups,
            structIndex,
            "OpenAI Chat Completions 请求",
            "Chat Completions 请求体",
            ["GeneralOpenAIRequest"],
            [
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "channel", "openai", "adaptor.go"),
                    ["func (a *Adaptor) ConvertOpenAIRequest(c *gin.Context, info *relaycommon.RelayInfo, request *dto.GeneralOpenAIRequest) (any, error)"],
                    ["request"]),
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "channel", "claude", "relay-claude.go"),
                    ["func RequestOpenAI2ClaudeMessage(c *gin.Context, textRequest dto.GeneralOpenAIRequest) (*dto.ClaudeRequest, error)"],
                    ["textRequest"])
            ]);

        AddStructGroup(groups, structIndex, "OpenAI Chat Completions 响应", "Chat Completions 非流式响应", ["OpenAITextResponse", "OpenAITextResponseChoice"]);
        AddStructGroup(groups, structIndex, "OpenAI Chat Completions 流式响应", "Chat Completions SSE 流式响应", ["ChatCompletionsStreamResponse", "ChatCompletionsStreamResponseChoice", "ChatCompletionsStreamResponseChoiceDelta"]);

        AddDynamicRequestGroup(
            groups,
            structIndex,
            "OpenAI Responses 请求",
            "Responses API 请求体",
            ["OpenAIResponsesRequest"],
            [
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "helper", "valid_request.go"),
                    ["func GetAndValidateResponsesRequest(c *gin.Context) (*dto.OpenAIResponsesRequest, error)"],
                    ["request"]),
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "channel", "openai", "adaptor.go"),
                    ["func (a *Adaptor) ConvertOpenAIResponsesRequest(c *gin.Context, info *relaycommon.RelayInfo, request dto.OpenAIResponsesRequest) (any, error)"],
                    ["request"])
            ]);

        AddStructGroup(groups, structIndex, "OpenAI Responses 响应", "Responses API 非流式响应", ["OpenAIResponsesResponse", "ResponsesOutput", "ResponsesOutputContent"]);
        AddStructGroup(groups, structIndex, "OpenAI Responses 流式事件", "Responses API SSE 流式事件", ["ResponsesStreamResponse"]);

        AddDynamicRequestGroup(
            groups,
            structIndex,
            "OpenAI Embeddings 请求",
            "Embeddings API 请求体",
            ["EmbeddingRequest"],
            [
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "helper", "valid_request.go"),
                    ["func GetAndValidateEmbeddingRequest(c *gin.Context, relayMode int) (*dto.EmbeddingRequest, error)"],
                    ["embeddingRequest"]),
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "channel", "openai", "adaptor.go"),
                    ["func (a *Adaptor) ConvertEmbeddingRequest(c *gin.Context, info *relaycommon.RelayInfo, request dto.EmbeddingRequest) (any, error)"],
                    ["request"])
            ]);

        AddStructGroup(groups, structIndex, "OpenAI Embeddings 响应", "Embeddings API 响应体", ["EmbeddingResponse", "EmbeddingResponseItem"]);

        AddDynamicRequestGroup(
            groups,
            structIndex,
            "Anthropic Messages 请求",
            "Anthropic Messages API 请求体",
            ["ClaudeRequest", "ClaudeMessage"],
            [
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "helper", "valid_request.go"),
                    ["func GetAndValidateClaudeRequest(c *gin.Context) (textRequest *dto.ClaudeRequest, err error)"],
                    ["textRequest"]),
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "dto", "claude.go"),
                    [
                        "func (c *ClaudeRequest) GetTokenCountMeta() *types.TokenCountMeta",
                        "func (c *ClaudeRequest) IsStream(ctx *gin.Context) bool",
                        "func (c *ClaudeRequest) SetModelName(modelName string)",
                        "func (c *ClaudeRequest) SearchToolNameByToolCallId(toolCallId string) string",
                        "func (c *ClaudeRequest) AddTool(tool any)",
                        "func (c *ClaudeRequest) GetTools() []any",
                        "func (c *ClaudeRequest) GetEfforts() string",
                        "func (c *ClaudeRequest) IsStringSystem() bool",
                        "func (c *ClaudeRequest) GetStringSystem() string",
                        "func (c *ClaudeRequest) SetStringSystem(system string)",
                        "func (c *ClaudeRequest) ParseSystem() []ClaudeMediaMessage"
                    ],
                    ["c"])
            ]);

        AddStructGroup(groups, structIndex, "Anthropic Messages 响应", "Anthropic Messages API 响应和 SSE 事件", ["ClaudeResponse", "ClaudeMediaMessage"]);

        AddDynamicRequestGroup(
            groups,
            structIndex,
            "Legacy Completions 请求",
            "Legacy Completions 请求体",
            ["GeneralOpenAIRequest"],
            [
                new NewApiScanSource(
                    Path.Combine(newApiRoot, "relay", "channel", "cloudflare", "relay_cloudflare.go"),
                    ["func convertCf2CompletionsRequest(textRequest dto.GeneralOpenAIRequest) *CfRequest"],
                    ["textRequest"])
            ]);

        AddStructGroup(groups, structIndex, "Legacy Completions 流式响应", "Legacy Completions SSE 流式响应", ["CompletionsStreamResponse"]);
        return groups;
    }

    /// <summary>
    /// 基于实际代码访问动态提取请求字段。
    /// </summary>
    private static void AddDynamicRequestGroup(
        List<ProtocolStructGroup> groups,
        Dictionary<string, GoStructDefinition> structIndex,
        string label,
        string description,
        string[] structNames,
        IReadOnlyList<NewApiScanSource> sources)
    {
        var fields = CollectDynamicFields(structIndex, structNames, sources);
        if (fields.Count == 0)
        {
            return;
        }

        groups.Add(new ProtocolStructGroup(label, description, structNames.ToList(), fields));
    }

    /// <summary>
    /// 直接基于 DTO 结构体构建分组，适用于边界明确的响应模型。
    /// </summary>
    private static void AddStructGroup(
        List<ProtocolStructGroup> groups,
        Dictionary<string, GoStructDefinition> structIndex,
        string label,
        string description,
        string[] structNames)
    {
        var fields = new List<GoStructField>();
        var foundStructs = new List<string>();
        foreach (var structName in structNames)
        {
            if (!structIndex.TryGetValue(structName, out var definition))
            {
                continue;
            }

            fields.AddRange(definition.Fields);
            foundStructs.Add(structName);
        }

        if (fields.Count > 0)
        {
            groups.Add(new ProtocolStructGroup(label, description, foundStructs, fields));
        }
    }

    /// <summary>
    /// 从扫描源中收集字段，并映射回 DTO 字段定义。
    /// </summary>
    private static List<GoStructField> CollectDynamicFields(
        Dictionary<string, GoStructDefinition> structIndex,
        IReadOnlyList<string> structNames,
        IReadOnlyList<NewApiScanSource> sources)
    {
        var matchedFields = new Dictionary<string, GoStructField>(StringComparer.OrdinalIgnoreCase);
        var memberToField = new Dictionary<string, GoStructField>(StringComparer.OrdinalIgnoreCase);

        foreach (var structName in structNames)
        {
            if (!structIndex.TryGetValue(structName, out var definition))
            {
                continue;
            }

            foreach (var field in definition.Fields)
            {
                memberToField[field.GoName] = field;
            }
        }

        foreach (var source in sources)
        {
            var members = NewApiMemberAccessScanner.ScanMembers(source.FilePath, source.FunctionMarkers, source.VariableNames);
            foreach (var member in members)
            {
                if (memberToField.TryGetValue(member, out var field))
                {
                    matchedFields[field.JsonName] = field;
                    continue;
                }

                foreach (var mappedMember in NewApiMemberAccessScanner.MapMethodToFields(member))
                {
                    if (memberToField.TryGetValue(mappedMember, out field))
                    {
                        matchedFields[field.JsonName] = field;
                    }
                }
            }
        }

        return matchedFields.Values
            .OrderBy(field => field.JsonName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// new-api 动态扫描源。
/// </summary>
internal sealed record NewApiScanSource(string FilePath, string[] FunctionMarkers, string[] VariableNames);

/// <summary>
/// 从 new-api 的 Go 代码中提取指定变量的字段/方法访问。
/// </summary>
internal static class NewApiMemberAccessScanner
{
    /// <summary>
    /// 扫描多个函数块中的成员访问。
    /// </summary>
    public static HashSet<string> ScanMembers(string filePath, IReadOnlyList<string> functionMarkers, IReadOnlyList<string> variableNames)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath))
        {
            return members;
        }

        var content = File.ReadAllText(filePath);
        var regex = BuildMemberRegex(variableNames);
        foreach (var marker in functionMarkers)
        {
            var block = ExtractFunctionBlock(content, marker);
            if (string.IsNullOrWhiteSpace(block))
            {
                continue;
            }

            foreach (Match match in regex.Matches(block))
            {
                var member = match.Groups["member"].Value;
                if (!string.IsNullOrWhiteSpace(member))
                {
                    members.Add(member);
                }
            }
        }

        return members;
    }

    /// <summary>
    /// 将辅助方法名映射为底层字段名。
    /// </summary>
    public static IEnumerable<string> MapMethodToFields(string member)
    {
        return member switch
        {
            "IsStream" => ["Stream"],
            "GetMaxTokens" => ["MaxTokens", "MaxCompletionTokens"],
            "ParseInput" => ["Input"],
            "GetTools" => ["Tools"],
            "GetEfforts" => ["Thinking", "Reasoning", "ReasoningEffort"],
            "IsStringSystem" or "GetStringSystem" or "SetStringSystem" or "ParseSystem" => ["System"],
            _ => []
        };
    }

    /// <summary>
    /// 生成成员访问正则。
    /// </summary>
    private static Regex BuildMemberRegex(IReadOnlyList<string> variableNames)
    {
        var variablePattern = string.Join("|", variableNames.Select(Regex.Escape));
        return new Regex($@"\b(?:{variablePattern})\.(?<member>[A-Z][A-Za-z0-9_]*)\b", RegexOptions.Compiled);
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
}
