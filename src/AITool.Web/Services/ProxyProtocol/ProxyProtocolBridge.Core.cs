using System.Collections.Generic;
using System.Linq;
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
        string? targetBaseUrl = null,
        string? stripRequestFields = null)
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

        // 按模型配置的字段黑名单剔除请求体字段（透传与转换路径都生效，放最后一步统一处理）。
        // 用于兼容不支持某些字段的上游（如 GPT-5 不认 reasoning_content、z.ai 不认 metadata）。
        if (!string.IsNullOrWhiteSpace(stripRequestFields))
        {
            result = ApplyStripRequestFields(result, stripRequestFields);
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
    /// 按字段黑名单从请求体剔除指定字段。透传与协议转换后统一调用，兼容不支持某些字段的上游。
    /// <para>
    /// 字段路径语法（逗号分隔多项）：
    /// - 顶层字段：直接写名字，如 <c>metadata</c>、<c>stream_options</c>
    /// - 裸字段名（不含 . 或 []）：自动当作 <c>messages[].字段名</c>，如 <c>reasoning_content</c>
    /// - 精确路径：<c>a.b</c> 嵌套属性，<c>a[].b</c> 对数组每条元素的 b 生效
    /// </para>
    /// 解析或剔除失败时静默保留原值，不影响转发可用性。
    /// </summary>
    private static string ApplyStripRequestFields(string requestBody, string? stripFields)
    {
        if (string.IsNullOrWhiteSpace(stripFields))
        {
            return requestBody;
        }

        try
        {
            var rootNode = JsonNode.Parse(requestBody) as JsonObject;
            if (rootNode is null)
            {
                return requestBody;
            }

            // 解析字段路径列表，并对裸字段名做语法糖（自动包成 messages[].字段名）。
            var paths = stripFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeStripPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (paths.Count == 0)
            {
                return requestBody;
            }

            foreach (var path in paths)
            {
                StripPath(rootNode, path);
            }

            return rootNode.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }

    /// <summary>
    /// 规范化单个字段路径：裸字段名（不含 . 和 []）自动当作 messages 数组每条的字段。
    /// 如 reasoning_content → messages[].reasoning_content；metadata 保持不变（顶层）。
    /// </summary>
    private static string NormalizeStripPath(string raw)
    {
        var path = raw.Trim();
        if (path.Contains('.') || path.Contains('['))
        {
            return path;
        }
        return $"messages[].{path}";
    }

    /// <summary>
    /// 按路径从节点剔除字段。路径段以 . 分隔，段名后跟 [] 表示该层是数组（对每条元素继续下钻）。
    /// </summary>
    private static void StripPath(JsonNode node, string path)
    {
        // 把 "messages[].reasoning_content" 拆成 ["messages", "[]", "reasoning_content"]。
        // 用 [] 作为独立段标记数组语义。
        var segments = new List<string>();
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.EndsWith("[]", StringComparison.OrdinalIgnoreCase))
            {
                var name = part[..^2].Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    segments.Add(name);
                }
                segments.Add("[]");
            }
            else
            {
                segments.Add(part);
            }
        }

        if (segments.Count == 0)
        {
            return;
        }

        StripSegments(node, segments, 0);
    }

    /// <summary>
    /// 递归按段剔除。最后一段执行 Remove，中间段下钻；遇到 [] 段则对数组每条元素递归。
    /// </summary>
    private static void StripSegments(JsonNode node, List<string> segments, int index)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        // 末段：直接 Remove。
        if (index == segments.Count - 1)
        {
            var last = segments[index];
            if (last != "[]")
            {
                obj.Remove(last);
            }
            return;
        }

        var seg = segments[index];
        if (seg == "[]")
        {
            // 当前节点应是数组，对每条元素继续下一段。
            return;
        }

        var child = obj[seg];
        if (child is null)
        {
            return;
        }

        // 下一段若是 []，说明当前 child 应是数组，对每条元素处理后续段。
        if (index + 1 < segments.Count && segments[index + 1] == "[]")
        {
            if (child is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not null)
                    {
                        StripSegments(item, segments, index + 2);
                    }
                }
            }
        }
        else
        {
            StripSegments(child, segments, index + 1);
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
