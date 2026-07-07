using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Web.Services;

/// <summary>
/// 负责在 OpenAI 与 Anthropic 协议之间转换请求和响应内容。
/// </summary>
public static partial class ProxyProtocolBridge
{
    /// <summary>
    /// 保存 OpenAI 流式响应转换为 Anthropic 事件流时的消息状态。
    /// </summary>
    public sealed class AnthropicOpenAiStreamState
    {
        /// <summary>
        /// 当前 Anthropic 消息的唯一标识。
        /// </summary>
        public string MessageId { get; set; } = $"msg_{Guid.NewGuid():N}";
        /// <summary>
        /// 下一个可分配的内容块索引。
        /// </summary>
        public int NextContentIndex { get; set; }
        /// <summary>
        /// thinking 内容块的索引，未创建时为 -1。
        /// </summary>
        public int ThinkingIndex { get; set; } = -1;
        /// <summary>
        /// text 内容块的索引，未创建时为 -1。
        /// </summary>
        public int TextIndex { get; set; } = -1;
        /// <summary>
        /// thinking 内容块是否已发送结束事件。
        /// </summary>
        public bool ThinkingClosed { get; set; }
        /// <summary>
        /// text 内容块是否已发送结束事件。
        /// </summary>
        public bool TextClosed { get; set; }
        /// <summary>
        /// 当前流中是否已经输出过任意内容块。
        /// </summary>
        public bool HadAnyContent { get; set; }
        /// <summary>
        /// 是否已收到上游流的结束事件。
        /// </summary>
        public bool ReceivedDoneEvent { get; set; }
        /// <summary>
        /// 输入 token 数。
        /// </summary>
        public int InputTokens { get; set; }
        /// <summary>
        /// 命中缓存的输入 token 数。
        /// </summary>
        public int CachedTokens { get; set; }
        /// <summary>
        /// 写入缓存时消耗的输入 token 数。
        /// </summary>
        public int CacheCreationTokens { get; set; }
        /// <summary>
        /// 输出 token 数。
        /// </summary>
        public int OutputTokens { get; set; }
        /// <summary>
        /// 当前消息最终对应的停止原因。
        /// </summary>
        public string StopReason { get; set; } = "end_turn";
        /// <summary>
        /// 按工具调用索引保存的工具块状态。
        /// </summary>
        public Dictionary<int, AnthropicToolCallBlockState> ToolCalls { get; } = [];
    }

    /// <summary>
    /// 保存单个 Anthropic tool_use 内容块的输出状态。
    /// </summary>
    public sealed class AnthropicToolCallBlockState
    {
        /// <summary>
        /// 该工具调用在 Anthropic 内容数组中的索引。
        /// </summary>
        public int ContentIndex { get; init; }
        /// <summary>
        /// 当前工具调用对应的 tool_use_id。
        /// </summary>
        public string ToolUseId { get; set; } = $"toolu_{Guid.NewGuid():N}";
        /// <summary>
        /// 工具名称。
        /// </summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>
        /// 是否已发送工具块起始事件。
        /// </summary>
        public bool Started { get; set; }
        /// <summary>
        /// 是否已发送工具块结束事件。
        /// </summary>
        public bool Closed { get; set; }
    }

    /// <summary>
    /// 按客户端协议和目标协议生成最终要转发的请求体。
    /// </summary>
    public static string PrepareRequestBody(
        string clientProtocol,
        string targetProtocol,
        string requestBody,
        string targetModelName,
        bool enableStreaming,
        string? overrideReasoningEffort = null,
        string? targetBaseUrl = null)
    {
        string result;
        JsonObject? rootNode = null;

        if (string.Equals(clientProtocol, targetProtocol, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(clientProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                result = ReplaceOpenAiModelAndEnsureStreamUsage(requestBody, targetModelName, enableStreaming);
            }
            else if (string.Equals(clientProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
            {
                result = ReplaceOpenAiModelAndEnsureStreamUsage(requestBody, targetModelName, enableStreaming);
            }
            else
            {
                result = ReplaceModelName(requestBody, targetModelName);
            }
        }
        else if (string.Equals(clientProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            result = ConvertChatRequestToResponses(requestBody, targetModelName, enableStreaming);
        }
        else if (string.Equals(clientProtocol, "Responses", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            result = ConvertResponsesRequestToChat(requestBody, targetModelName, enableStreaming);
        }
        else
        {
            rootNode = JsonNode.Parse(requestBody) as JsonObject;
            if (rootNode is null)
            {
                return requestBody;
            }

            if (string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
                && string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
            {
                var openAiRequestBody = BuildOpenAiRequestFromAnthropic(rootNode, targetModelName, enableStreaming);
                result = ConvertChatRequestToResponses(openAiRequestBody, targetModelName, enableStreaming);
            }
            else
            {
                result = string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
                    ? BuildOpenAiRequestFromAnthropic(rootNode, targetModelName, enableStreaming)
                    : BuildAnthropicRequestFromOpenAi(rootNode, targetModelName, enableStreaming);
            }
        }

        // 如果需要覆盖思考等级，在最终请求体上直接修改，避免二次 Parse。
        if (!string.IsNullOrWhiteSpace(overrideReasoningEffort))
        {
            result = ApplyReasoningEffort(result, overrideReasoningEffort, targetProtocol);
        }

        // 目标为 Responses 协议时强制 store=false：
        // - ConvertChatRequestToResponses 已自带此逻辑，但 OpenAI→OpenAI 直通分支只替换 model/stream，
        //   不触发转换。客户端（Codex CLI / Claude Code）若未显式带 store，上游会返回 400
        //   "store must be set to false"。此处兜底保证任何发往 Responses 上游的请求体都满足要求。
        if (string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            var isCodex = IsCodexTarget(targetBaseUrl);
            result = NormalizeResponsesBody(result, isCodex);
        }

        return result;
    }

    /// <summary>
    /// 在请求体 JSON 上覆盖思考等级。内联到 PrepareRequestBody 中避免二次 Parse。
    /// </summary>
    private static string ApplyReasoningEffort(string requestBody, string overrideEffort, string targetProtocol)
    {
        try
        {
            var rootNode = JsonNode.Parse(requestBody) as JsonObject;
            if (rootNode is null)
            {
                return requestBody;
            }

            var normalized = overrideEffort.Trim().ToLowerInvariant();

            if (string.Equals(targetProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                // Anthropic 协议：用 thinking.budget_tokens 表达思考强度
                rootNode["thinking"] = new JsonObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = normalized switch
                    {
                        "low" => 1280,
                        "medium" => 2048,
                        "high" => 4096,
                        "xhigh" => 8192,
                        "max" => 16384,
                        _ => 4096 // 自定义值按 high 处理
                    }
                };
            }
            else if (string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
            {
                // Responses 协议：用 reasoning.effort 表达
                if (rootNode["reasoning"] is not JsonObject)
                {
                    rootNode["reasoning"] = new JsonObject();
                }
                ((JsonObject)rootNode["reasoning"]!)["effort"] = normalized;
            }
            else
            {
                // OpenAI 协议：用顶层 reasoning_effort 表达（覆盖值原样透传，max/xhigh 等均合法）。
                rootNode["reasoning_effort"] = normalized;
            }

            return rootNode.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }

    /// <summary>
    /// 强制覆盖请求体中的思考等级。已内联到 PrepareRequestBody，此方法保留向后兼容。
    /// </summary>
    public static string OverrideReasoningEffort(string requestBody, string overrideEffort, string targetProtocol)
        => ApplyReasoningEffort(requestBody, overrideEffort, targetProtocol);

    /// <summary>
    /// 判断目标 URL 是否为 Codex 上游（chatgpt.com/backend-api/codex）。
    /// 只有 Codex 上游会拒绝 max_output_tokens / temperature / metadata 等参数；
    /// 标准 OpenAI Responses API（api.openai.com）接受这些参数。
    /// </summary>
    public static bool IsCodexTarget(string? targetBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(targetBaseUrl))
        {
            return false;
        }

        return targetBaseUrl.Contains("chatgpt.com/backend-api", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 统一规范化发往 Responses 上游的请求体。
    /// 1) store=false：所有 Responses 上游都要求（Codex 强制，标准 OpenAI 也推荐）。
    /// 2) 仅 Codex 上游：剔除上游不接受的参数（max_output_tokens / temperature / metadata 等），
    ///    否则会返回 {"detail":"Unsupported parameter: xxx"}（400）。
    ///    清单参考 CPA codex_openai-responses_request.go 的 DeleteBytes 列表。
    /// 解析失败时原样返回，避免影响可用性。
    /// </summary>
    public static string NormalizeResponsesBody(string requestBody, bool isCodex)
    {
        try
        {
            var rootNode = JsonNode.Parse(requestBody) as JsonObject;
            if (rootNode is null)
            {
                return requestBody;
            }

            if (rootNode["store"] is null)
            {
                rootNode["store"] = false;
            }

            if (isCodex)
            {
                foreach (var unsupported in CodexUnsupportedParameters)
                {
                    rootNode.Remove(unsupported);
                }
            }

            return rootNode.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }

    /// <summary>
    /// Codex 上游（chatgpt.com/backend-api/codex/responses）不接受的请求体字段。
    /// 任一字段出现都会触发 {"detail":"Unsupported parameter: xxx"}（400）。
    /// 清单与 CPA codex_openai-responses_request.go 的 DeleteBytes 列表保持一致，
    /// 并补充 metadata（CPA 同样遗漏，但实测会被 Codex 拒绝）。
    /// </summary>
    private static readonly string[] CodexUnsupportedParameters =
    {
        "metadata",
        "temperature",
        "top_p",
        "max_output_tokens",
        "max_completion_tokens",
        "truncation",
        "user",
        "previous_response_id",
        "prompt_cache_retention",
        "safety_identifier",
        "stream_options",
        "context_management"
    };

    /// <summary>
    /// 按客户端协议将上游响应内容转换为可直接返回的格式。
    /// </summary>
    public static string AdaptResponseBodyForClient(
        string clientProtocol,
        string upstreamProtocol,
        string responseBody,
        bool isStreaming,
        string modelName,
        int inputTokens,
        int cachedTokens,
        int outputTokens)
    {
        if (string.Equals(clientProtocol, upstreamProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return responseBody;
        }

        if (string.Equals(clientProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase)
            && string.Equals(upstreamProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            return isStreaming
                ? ConvertResponsesStreamingToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens)
                : ConvertResponsesResponseToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens);
        }

        if (string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            && string.Equals(upstreamProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            var openAiResponseBody = isStreaming
                ? ConvertResponsesStreamingToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens)
                : ConvertResponsesResponseToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens);

            return isStreaming
                ? BuildAnthropicStreamingResponseFromOpenAi(openAiResponseBody, modelName, inputTokens, cachedTokens, outputTokens)
                : BuildAnthropicResponseFromOpenAi(openAiResponseBody, modelName, inputTokens, cachedTokens, outputTokens);
        }

        if (isStreaming)
        {
            return string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
                ? BuildAnthropicStreamingResponseFromOpenAi(responseBody, modelName, inputTokens, cachedTokens, outputTokens)
                : BuildOpenAiStreamingResponseFromAnthropic(responseBody, modelName, inputTokens, cachedTokens, outputTokens);
        }

        return string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            ? BuildAnthropicResponseFromOpenAi(responseBody, modelName, inputTokens, cachedTokens, outputTokens)
            : BuildOpenAiResponseFromAnthropic(responseBody, modelName, inputTokens, cachedTokens, outputTokens);
    }
}
