using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Domain.Proxy;

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
        /// 是否出现无法解析的上游流片段。
        /// </summary>
        public bool ConversionFailed { get; set; }
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
        IReadOnlyList<CompatibilityRule>? compatibilityRules = null,
        bool isPassthrough = true)
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

        // 应用模型关联的兼容规则集（透传与转换路径都生效，放最后一步统一处理）。
        // 按当前路径（isPassthrough）筛选规则 scope 后依次 strip/rename/default。
        if (compatibilityRules is { Count: > 0 })
        {
            result = ApplyCompatibilityProfile(result, compatibilityRules, isPassthrough);
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
                // claude-code 新版的思考强度由 output_config.effort 决定（thinking.type 只是开关）。
                // 必须同时覆盖 output_config.effort，否则客户端原值（如 max）会盖过配置的覆盖值。
                rootNode["output_config"] = new JsonObject { ["effort"] = normalized };

                // thinking 同时设成 enabled + budget_tokens，兼容只认老式 budget_tokens 的 Anthropic 端点。
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

                // ChatGPT /responses 端点强制 stream=true，否则返回 {"detail":"Stream must be set to true"}。
                // 客户端非流式请求由 ProxyForwardService.ForwardAsync 透明聚合成完整 JSON 返回。
                rootNode["stream"] = true;
            }

            return rootNode.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }

    /// <summary>
    /// 应用兼容规则集：按规则对请求体做字段级变换（strip/rename/default）。
    /// 规则已由缓存层解析为列表，此处按 isPassthrough 筛选 scope 后依次应用，零 JSON 规则解析开销。
    /// 任一规则失败静默跳过，不影响其他规则和转发可用性。
    /// </summary>
    /// <param name="isPassthrough">当前是否透传路径（clientProtocol==targetProtocol）。true 保留 scope=passthrough/all 的规则；false 保留 scope=bridge/all 的规则。</param>
    private static string ApplyCompatibilityProfile(string requestBody, IReadOnlyList<CompatibilityRule> rules, bool isPassthrough)
    {
        if (rules.Count == 0) return requestBody;

        try
        {
            var rootNode = JsonNode.Parse(requestBody) as JsonObject;
            if (rootNode is null) return requestBody;

            foreach (var rule in rules)
            {
                // 按 scope 筛选：all 始终生效；passthrough/bridge 仅对应路径生效。
                var scope = (rule.Scope ?? "all").Trim().ToLowerInvariant();
                if (scope != "all")
                {
                    var wantPassthrough = string.Equals(scope, "passthrough", StringComparison.OrdinalIgnoreCase);
                    if (wantPassthrough != isPassthrough) continue;
                }

                try
                {
                    var op = (rule.Op ?? "").Trim().ToLowerInvariant();
                    switch (op)
                    {
                        case "strip":
                            // 裸字段名（不含 . 和 []）：同时尝试删顶层字段和 messages 数组每条的该字段，
                            // 兼容 metadata（顶层）和 reasoning_content（messages 内）两种常见场景。
                            // 含 . 或 [] 的视为精确路径，原样解析。
                            var rawTarget = (rule.Target ?? "").Trim();
                            if (!string.IsNullOrWhiteSpace(rawTarget))
                            {
                                if (rawTarget.Contains('.') || rawTarget.Contains('['))
                                {
                                    StripPath(rootNode, rawTarget);
                                }
                                else
                                {
                                    StripPath(rootNode, rawTarget);
                                    StripPath(rootNode, "messages[]." + rawTarget);
                                }
                            }
                            break;
                        case "rename":
                            ApplyRename(rootNode, rule.From, rule.To);
                            break;
                        case "default":
                            ApplyDefault(rootNode, rule.Key, rule.Value);
                            break;
                    }
                }
                catch
                {
                    // 单条规则失败不影响其他规则。
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
    /// 顶层字段重命名：from 存在且 to 非空时，把 from 的值移到 to（保留原值类型），再删除 from。
    /// from 不存在或 to 为空则跳过。
    /// </summary>
    private static void ApplyRename(JsonObject root, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return;
        if (root[from] is null) return;
        root[to] = root[from]!.DeepClone();
        root.Remove(from);
    }

    /// <summary>
    /// 为缺失的顶层字段补默认值。仅当字段不存在时才注入（不覆盖客户端已有值）。
    /// value 按 true/false/整数/小数 自动推断类型，否则按字符串。
    /// </summary>
    private static void ApplyDefault(JsonObject root, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (root[key] is not null) return; // 已有值不覆盖

        var v = value ?? string.Empty;
        if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) { root[key] = true; return; }
        if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) { root[key] = false; return; }
        if (int.TryParse(v, out var intVal)) { root[key] = intVal; return; }
        if (double.TryParse(v, out var dblVal)) { root[key] = dblVal; return; }
        root[key] = v;
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

            if (string.IsNullOrEmpty(openAiResponseBody))
            {
                return string.Empty;
            }

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
