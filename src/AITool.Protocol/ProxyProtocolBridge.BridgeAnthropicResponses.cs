using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Protocol;

/// <summary>
/// 负责 Anthropic Messages 与 OpenAI Responses 两个协议之间的直接转换。
/// 与早期"经 OpenAI Chat Completions 两段中转"的实现不同，这里逐字段互转，
/// 避免 Chat 中间表示无法表达 thinking 签名、reasoning 输出项、文件内容等语义造成的信息丢失。
/// </summary>
public static partial class ProxyProtocolBridge
{
    /// <summary>
    /// thinking 签名桥接载体前缀。Anthropic thinking block 的签名字段在 Responses 协议中没有对应物，
    /// 响应方向把 {thinking, signature} 编码进 reasoning 输出项的 encrypted_content 字段，
    /// 请求方向识别该前缀后还原成带签名的 thinking block 发回 Anthropic 上游。
    /// 该格式仅在本桥接层的两端使用，上游与客户端都无需理解。
    /// </summary>
    private const string AnthropicThinkingBridgePrefix = "aitool-anthropic-thinking-v1:";

    /// <summary>
    /// 将 Anthropic 请求体直接转换为 Responses API 请求体（不经 Chat Completions 中转）。
    /// </summary>
    public static string BuildResponsesRequestFromAnthropic(JsonObject rootNode, string targetModelName, bool enableStreaming)
    {
        var input = new JsonArray();
        string? instructions = null;

        var systemNode = rootNode["system"];
        if (systemNode is not null)
        {
            var systemText = ExtractSystemContent(systemNode);
            if (!string.IsNullOrWhiteSpace(systemText))
            {
                instructions = systemText;
            }
        }

        if (rootNode["messages"] is JsonArray srcMessages)
        {
            foreach (var msg in srcMessages)
            {
                if (msg is not JsonObject msgObj)
                {
                    continue;
                }

                var role = msgObj["role"]?.GetValue<string>() ?? "user";

                // messages 数组里混入的 system 条目（claude-code 新版常见）合并进 instructions，
                // Responses 的 input 不接受 system 角色的消息项。
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    var extraSystemText = ExtractOpenAiContentAsString(msgObj["content"]) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(extraSystemText))
                    {
                        instructions = string.IsNullOrWhiteSpace(instructions)
                            ? extraSystemText
                            : string.Concat(instructions, "\n\n", extraSystemText);
                    }

                    continue;
                }

                if (msgObj["content"] is not JsonArray blocks)
                {
                    // 字符串 content：user/assistant 纯文本各生成一个 message 输出项。
                    var plainText = msgObj["content"] is JsonValue textValue && textValue.TryGetValue<string>(out var s)
                        ? s
                        : msgObj["content"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(plainText))
                    {
                        input.Add(BuildResponsesMessageItem(role, new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "output_text" : "input_text",
                                ["text"] = plainText
                            }
                        }));
                    }

                    continue;
                }

                // 内容块按到达顺序转换；文本累积成 message 输出项，工具调用紧跟其后作为顶层 item。
                var pendingParts = new JsonArray();
                foreach (var block in blocks)
                {
                    if (block is not JsonObject blockObj)
                    {
                        continue;
                    }

                    var type = blockObj["type"]?.GetValue<string>() ?? "";

                    if (type == "text")
                    {
                        var text = blockObj["text"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(text))
                        {
                            continue;
                        }

                        pendingParts.Add(new JsonObject
                        {
                            ["type"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "output_text" : "input_text",
                            ["text"] = text
                        });
                    }
                    else if (type == "image" && blockObj["source"] is JsonObject imgSrc)
                    {
                        var mediaType = imgSrc["media_type"]?.GetValue<string>() ?? "image/png";
                        var data = imgSrc["data"]?.GetValue<string>() ?? string.Empty;
                        pendingParts.Add(new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:{mediaType};base64,{data}"
                        });
                    }
                    else if (type == "document" && blockObj["source"] is JsonObject docSrc
                             && string.Equals(docSrc["type"]?.GetValue<string>(), "base64", StringComparison.OrdinalIgnoreCase))
                    {
                        // Responses 的 input_file 承载 Anthropic document 的 base64 内容。
                        var mediaType = docSrc["media_type"]?.GetValue<string>() ?? "application/pdf";
                        var data = docSrc["data"]?.GetValue<string>() ?? string.Empty;
                        var fileItem = new JsonObject
                        {
                            ["type"] = "input_file",
                            ["file_data"] = $"data:{mediaType};base64,{data}"
                        };
                        var title = blockObj["title"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            fileItem["filename"] = title;
                        }

                        pendingParts.Add(fileItem);
                    }
                    else if (type == "tool_result")
                    {
                        // tool_result 是顶层 function_call_output item，必须先落盘累积的文本。
                        FlushPendingMessageItem(input, ref pendingParts, role);
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = blockObj["tool_use_id"]?.DeepClone() ?? string.Empty,
                            ["output"] = SerializeAnthropicToolResultContent(blockObj["content"])
                        });
                    }
                    else if (type == "tool_use")
                    {
                        // assistant 的 tool_use 是顶层 function_call item；thinking 历史块无对应物，丢弃
                        // （与经 Chat 中转的旧路径行为一致，签名信息无法在请求方向重建）。
                        FlushPendingMessageItem(input, ref pendingParts, role);
                        var callId = blockObj["id"]?.GetValue<string>();
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["id"] = $"fc_{Guid.NewGuid():N}",
                            ["call_id"] = string.IsNullOrWhiteSpace(callId) ? $"call_{Guid.NewGuid():N}" : callId,
                            ["name"] = blockObj["name"]?.DeepClone() ?? string.Empty,
                            ["arguments"] = blockObj["input"] is null ? "{}" : blockObj["input"]!.ToJsonString()
                        });
                    }
                }

                FlushPendingMessageItem(input, ref pendingParts, role);
            }
        }

        var payload = new JsonObject
        {
            ["model"] = targetModelName,
            ["stream"] = enableStreaming,
            ["input"] = input
        };

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            payload["instructions"] = instructions;
        }

        var maxTokens = rootNode["max_tokens"]?.GetValue<uint>() ?? 0;
        if (maxTokens > 0)
        {
            payload["max_output_tokens"] = maxTokens;
        }

        CopyNodeIfPresent(rootNode, payload, "temperature");
        CopyNodeIfPresent(rootNode, payload, "top_p");

        var effort = ResolveEffortFromAnthropicThinking(rootNode);
        if (!string.IsNullOrEmpty(effort))
        {
            payload["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        ConvertAnthropicToolsToResponses(rootNode, payload);
        ConvertAnthropicToolChoiceToResponses(rootNode, payload);

        return payload.ToJsonString();
    }

    /// <summary>
    /// 将 Responses API 请求体直接转换为 Anthropic 请求体（不经 Chat Completions 中转）。
    /// </summary>
    public static string BuildAnthropicRequestFromResponses(JsonObject rootNode, string targetModelName, bool enableStreaming)
    {
        var claudeMessages = new JsonArray();
        var systemParts = new List<string>();

        if (rootNode["instructions"] is JsonValue instructionsValue && instructionsValue.TryGetValue<string>(out var instructions))
        {
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                systemParts.Add(instructions);
            }
        }

        if (rootNode["input"] is not null)
        {
            ParseResponsesInputToAnthropicMessages(rootNode["input"], claudeMessages, systemParts);
        }

        // Anthropic 要求第一条消息是 user 角色；缺失时补占位（与 Chat→Anthropic 转换的兜底一致）。
        if (claudeMessages.Count == 0)
        {
            claudeMessages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "..." } }
            });
        }
        else
        {
            var firstRole = claudeMessages[0]?["role"]?.GetValue<string>();
            if (!string.Equals(firstRole, "user", StringComparison.OrdinalIgnoreCase))
            {
                var placeholder = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "..." } }
                };
                var newMessages = new JsonArray { placeholder };
                foreach (var m in claudeMessages)
                {
                    // 节点仍挂在原数组下，必须克隆后才能加入新数组（JsonNode 不允许双父）。
                    newMessages.Add(m!.DeepClone());
                }

                claudeMessages = newMessages;
            }
        }

        var payload = new JsonObject
        {
            ["model"] = targetModelName,
            ["messages"] = claudeMessages,
            ["stream"] = enableStreaming
        };

        var maxOutputTokens = rootNode["max_output_tokens"]?.GetValue<uint>() ?? 0;
        if (maxOutputTokens > 0)
        {
            payload["max_tokens"] = maxOutputTokens;
        }

        if (systemParts.Count > 0)
        {
            payload["system"] = systemParts.Count == 1
                ? systemParts[0]
                : new JsonArray(systemParts.Select(p => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = p }).ToArray());
        }

        CopyNodeIfPresent(rootNode, payload, "temperature");
        CopyNodeIfPresent(rootNode, payload, "top_p");
        // metadata 与旧路径（经 Chat 中转的 BuildAnthropicRequestFromOpenAi）保持一致：原样透传。
        CopyNodeIfPresent(rootNode, payload, "metadata");

        ConvertResponsesToolsToAnthropic(rootNode, payload);
        ConvertResponsesToolChoiceToAnthropic(rootNode, payload);

        // reasoning.effort / output_config.effort → thinking + output_config（口径与 ApplyReasoningEffort 一致）。
        var effortValue = ExtractEffortFromResponsesRequest(rootNode);
        if (!string.IsNullOrEmpty(effortValue))
        {
            payload["output_config"] = new JsonObject { ["effort"] = effortValue };
            payload["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = effortValue switch
                {
                    "low" => 1280,
                    "medium" => 2048,
                    "high" => 4096,
                    "xhigh" => 8192,
                    "max" => 16384,
                    _ => 4096
                }
            };
        }

        return payload.ToJsonString();
    }

    /// <summary>
    /// 将 Anthropic 非流式响应直接转换为 Responses API 非流式响应。
    /// thinking block 会带上签名桥接载体（encrypted_content），供下一轮请求方向还原。
    /// </summary>
    public static string BuildResponsesResponseFromAnthropic(string anthropicBody)
    {
        try
        {
            var root = JsonNode.Parse(anthropicBody) as JsonObject;
            if (root is null || root["content"] is not JsonArray content || content.Count == 0)
            {
                return string.Empty;
            }

            var outputs = new JsonArray();
            foreach (var blockNode in content)
            {
                if (blockNode is not JsonObject block)
                {
                    continue;
                }

                var type = block["type"]?.GetValue<string>() ?? "";
                if (type == "text")
                {
                    var text = block["text"]?.GetValue<string>() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        outputs.Add(new JsonObject
                        {
                            ["type"] = "message",
                            ["id"] = $"msg_{Guid.NewGuid():N}",
                            ["status"] = "completed",
                            ["role"] = "assistant",
                            ["content"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "output_text", ["text"] = text }
                            }
                        });
                    }
                }
                else if (type == "thinking")
                {
                    var thinkingText = block["thinking"]?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(thinkingText))
                    {
                        continue;
                    }

                    var reasoningItem = new JsonObject
                    {
                        ["type"] = "reasoning",
                        ["id"] = $"rs_{Guid.NewGuid():N}",
                        ["status"] = "completed",
                        ["summary"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "summary_text", ["text"] = thinkingText }
                        }
                    };
                    var signature = block["signature"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(signature))
                    {
                        reasoningItem["encrypted_content"] = EncodeAnthropicThinkingBridge(thinkingText, signature);
                    }

                    outputs.Add(reasoningItem);
                }
                else if (type == "tool_use")
                {
                    outputs.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["id"] = $"fc_{Guid.NewGuid():N}",
                        ["status"] = "completed",
                        ["call_id"] = block["id"]?.DeepClone() ?? $"call_{Guid.NewGuid():N}",
                        ["name"] = block["name"]?.DeepClone() ?? string.Empty,
                        ["arguments"] = block["input"] is null ? "{}" : block["input"]!.ToJsonString()
                    });
                }
            }

            if (outputs.Count == 0)
            {
                return string.Empty;
            }

            var messageId = root["id"]?.GetValue<string>();
            var responseId = string.IsNullOrWhiteSpace(messageId)
                ? $"resp_{Guid.NewGuid():N}"
                : (messageId.StartsWith("resp_", StringComparison.OrdinalIgnoreCase) ? messageId : $"resp_{messageId}");

            var stopReason = root["stop_reason"]?.GetValue<string>() ?? "end_turn";
            var isMaxTokens = string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

            var usageNode = root["usage"] as JsonObject;
            var freshInput = usageNode?["input_tokens"]?.GetValue<int>() ?? 0;
            var cacheRead = usageNode?["cache_read_input_tokens"]?.GetValue<int>() ?? 0;
            var cacheCreation = usageNode?["cache_creation_input_tokens"]?.GetValue<int>() ?? 0;
            var outputTokens = usageNode?["output_tokens"]?.GetValue<int>() ?? 0;

            var response = new JsonObject
            {
                ["id"] = responseId,
                ["object"] = "response",
                ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["status"] = isMaxTokens ? "incomplete" : "completed",
                ["model"] = root["model"]?.DeepClone() ?? string.Empty,
                ["output"] = outputs,
                ["usage"] = new JsonObject
                {
                    // Responses 口径 input_tokens 含缓存命中部分（Anthropic 出口三桶相加还原）。
                    ["input_tokens"] = freshInput + cacheRead + cacheCreation,
                    ["output_tokens"] = outputTokens,
                    ["total_tokens"] = freshInput + cacheRead + cacheCreation + outputTokens,
                    ["output_tokens_details"] = new JsonObject { ["total_tokens"] = outputTokens },
                    ["input_tokens_details"] = new JsonObject
                    {
                        ["cached_tokens"] = cacheRead,
                        ["cached_creation_tokens"] = cacheCreation
                    }
                }
            };

            if (isMaxTokens)
            {
                response["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
            }

            return response.ToJsonString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 将 Responses API 非流式响应直接转换为 Anthropic 非流式响应。
    /// 顶层 status=failed/cancelled 或携带 error 对象时视为失败，返回空串交由调用层 fallback。
    /// </summary>
    public static string BuildAnthropicResponseFromResponses(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        try
        {
            var root = JsonNode.Parse(responseBody) as JsonObject;
            if (root is null || root["output"] is not JsonArray output || output.Count == 0)
            {
                return string.Empty;
            }

            if (IsResponsesFailureStatus(root))
            {
                return string.Empty;
            }

            var content = new JsonArray();
            var hasToolUse = false;
            foreach (var itemNode in output)
            {
                if (itemNode is not JsonObject item)
                {
                    continue;
                }

                var type = item["type"]?.GetValue<string>() ?? "";
                if (type == "message" && item["content"] is JsonArray parts)
                {
                    foreach (var partNode in parts)
                    {
                        if (partNode is not JsonObject part)
                        {
                            continue;
                        }

                        var partType = part["type"]?.GetValue<string>() ?? "";
                        if (partType is "output_text" or "text" or "refusal")
                        {
                            var text = partType == "refusal"
                                ? part["refusal"]?.GetValue<string>() ?? part["text"]?.GetValue<string>() ?? string.Empty
                                : part["text"]?.GetValue<string>() ?? string.Empty;
                            if (!string.IsNullOrEmpty(text))
                            {
                                content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                            }
                        }
                        else if (partType is "reasoning" or "reasoning_summary")
                        {
                            var summaryText = part["text"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(summaryText))
                            {
                                content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = summaryText });
                            }
                        }
                    }
                }
                else if (type == "reasoning")
                {
                    // Responses 的 reasoning 输出项 → thinking block（无签名，仅客户端展示；
                    // 客户端回传时请求方向按无桥接容器处理直接丢弃，不会构造非法签名块）。
                    var summary = item["summary"] as JsonArray;
                    var summaryText = summary is null
                        ? string.Empty
                        : string.Concat(summary.OfType<JsonObject>()
                            .Select(p => p["text"]?.GetValue<string>() ?? string.Empty));
                    if (!string.IsNullOrWhiteSpace(summaryText))
                    {
                        content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = summaryText });
                    }
                }
                else if (type == "function_call")
                {
                    hasToolUse = true;
                    JsonNode? toolInput;
                    var arguments = item["arguments"]?.GetValue<string>() ?? "{}";
                    try
                    {
                        toolInput = JsonNode.Parse(arguments);
                    }
                    catch
                    {
                        toolInput = arguments;
                    }

                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = item["call_id"]?.DeepClone() ?? $"toolu_{Guid.NewGuid():N}",
                        ["name"] = item["name"]?.DeepClone() ?? string.Empty,
                        ["input"] = toolInput
                    });
                }
            }

            if (content.Count == 0)
            {
                return string.Empty;
            }

            var status = root["status"]?.GetValue<string>() ?? "completed";
            var incompleteReason = (root["incomplete_details"] as JsonObject)?["reason"]?.GetValue<string>();
            var stopReason = hasToolUse
                ? "tool_use"
                : string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase)
                    ? "max_tokens"
                    : "end_turn";

            var usageNode = root["usage"] as JsonObject;
            var usageInput = usageNode?["input_tokens"]?.GetValue<int>() ?? 0;
            var usageOutput = usageNode?["output_tokens"]?.GetValue<int>() ?? 0;
            var details = usageNode?["input_tokens_details"] as JsonObject;
            var cacheRead = details?["cached_tokens"]?.GetValue<int>() ?? cachedTokens;
            var cacheWrite = details?["cached_creation_tokens"]?.GetValue<int>()
                ?? details?["cache_write_tokens"]?.GetValue<int>()
                ?? 0;

            // Responses input_tokens 含缓存；Anthropic 出口口径 input_tokens 不含缓存（官方三桶加法）。
            var freshInput = usageInput > 0 ? Math.Max(0, usageInput - cacheRead - cacheWrite) : inputTokens;
            var effectiveOutput = usageOutput > 0 ? usageOutput : outputTokens;

            var responseId = root["id"]?.GetValue<string>();
            return new JsonObject
            {
                ["id"] = string.IsNullOrWhiteSpace(responseId) ? $"msg_{Guid.NewGuid():N}" : responseId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = root["model"]?.DeepClone() ?? modelName,
                ["content"] = content,
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = freshInput,
                    ["cache_creation_input_tokens"] = cacheWrite,
                    ["cache_read_input_tokens"] = cacheRead,
                    ["output_tokens"] = effectiveOutput
                }
            }.ToJsonString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 将完整的 Responses SSE 事件流重建为 Anthropic SSE 事件流（用于非流式请求但需要以 SSE 回写的场景）。
    /// 优先取 response.completed 携带的完整 response 对象；缺失时返回空串。
    /// </summary>
    public static string BuildAnthropicStreamFromResponses(string sseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        try
        {
            using var reader = new StringReader(sseBody);
            string? line;
            string currentEvent = string.Empty;
            var dataLines = new List<string>();
            JsonObject? completedResponse = null;

            void FlushEvent()
            {
                if (dataLines.Count == 0)
                {
                    return;
                }

                var payload = string.Join("\n", dataLines);
                dataLines.Clear();
                if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    currentEvent = string.Empty;
                    return;
                }

                try
                {
                    var node = JsonNode.Parse(payload) as JsonObject;
                    if (node is null)
                    {
                        return;
                    }

                    var eventType = !string.IsNullOrWhiteSpace(currentEvent)
                        ? currentEvent
                        : node["type"]?.GetValue<string>() ?? string.Empty;
                    if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase)
                        && node["response"] is JsonObject response)
                    {
                        completedResponse = response;
                    }
                }
                catch
                {
                    // 单个事件解析失败不影响其余事件。
                }
                finally
                {
                    currentEvent = string.Empty;
                }
            }

            while ((line = reader.ReadLine()) is not null)
            {
                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    currentEvent = line.Length > 6 ? line[6..].Trim() : string.Empty;
                    continue;
                }

                if (string.IsNullOrEmpty(line))
                {
                    FlushEvent();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var data = line.Length > 5 ? line[5..] : string.Empty;
                    if (data.StartsWith(' '))
                    {
                        data = data[1..];
                    }

                    dataLines.Add(data);
                }
            }

            FlushEvent();

            if (completedResponse is null)
            {
                return string.Empty;
            }

            var anthropicBody = BuildAnthropicResponseFromResponses(completedResponse.ToJsonString(), modelName, inputTokens, cachedTokens, outputTokens);
            if (string.IsNullOrEmpty(anthropicBody))
            {
                return string.Empty;
            }

            return RebuildAnthropicSseFromResponse(anthropicBody);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Responses 流式 → Anthropic 流式的逐事件转换状态。
    /// Core 复用 OpenAI→Anthropic 状态机的块管理（thinking/text/tool_use 索引、收尾、用量）。
    /// </summary>
    public sealed class ResponsesToAnthropicStreamState
    {
        public AnthropicOpenAiStreamState Core { get; } = new();
        public string Model { get; set; } = string.Empty;
        /// <summary>是否已收到 response.completed（或 incomplete）终态事件。</summary>
        public bool Completed { get; set; }
        /// <summary>是否收到 response.failed / error 终态事件。</summary>
        public bool Failed { get; set; }
        /// <summary>按 Responses output_index 保存输出项与 Anthropic 内容块索引的映射。</summary>
        public Dictionary<int, ResponsesOutputItemState> Items { get; } = [];
    }

    /// <summary>
    /// 单个 Responses 输出项的转换状态。
    /// </summary>
    public sealed class ResponsesOutputItemState
    {
        /// <summary>输出项类型：message / reasoning / function_call。</summary>
        public string Kind { get; set; } = string.Empty;
        /// <summary>对应的 Anthropic 内容块索引；-1 表示尚未分配。</summary>
        public int BlockIndex { get; set; } = -1;
        /// <summary>内容块是否已关闭。</summary>
        public bool BlockClosed { get; set; }
    }

    /// <summary>
    /// 将单个 Responses SSE 事件直接转换为 Anthropic SSE 事件片段（不经 Chat 中转）。
    /// message_start 的发送时机由调用层控制：仅当本方法返回非空事件时才补发首帧，保留 fallback 能力。
    /// 流结束时的收尾（关闭未关块、message_delta/message_stop）由调用层调用 CompleteAnthropicStream 完成。
    /// </summary>
    public static string ConvertResponsesSseEventToAnthropic(string eventName, string payloadJson, ResponsesToAnthropicStreamState state)
    {
        var builder = new StringBuilder();
        var core = state.Core;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (string.Equals(eventName, "response.created", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventName, "response.in_progress", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.Object)
                {
                    if (responseEl.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                    {
                        state.Model = modelEl.GetString() ?? state.Model;
                    }
                }

                return string.Empty;
            }

            if (string.Equals(eventName, "response.output_item.added", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                var outputIndex = root.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number ? oi.GetInt32() : -1;
                var itemType = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

                if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase))
                {
                    core.HadAnyContent = true;
                    CloseThinkingBlockIfNeeded(builder, core);
                    var blockIndex = EnsureTextBlockOpen(builder, core);
                    if (outputIndex >= 0)
                    {
                        state.Items[outputIndex] = new ResponsesOutputItemState { Kind = "message", BlockIndex = blockIndex };
                    }
                }
                else if (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    core.HadAnyContent = true;
                    var blockIndex = EnsureThinkingBlockOpen(builder, core);
                    if (outputIndex >= 0)
                    {
                        state.Items[outputIndex] = new ResponsesOutputItemState { Kind = "reasoning", BlockIndex = blockIndex };
                    }
                }
                else if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    core.HadAnyContent = true;
                    CloseThinkingBlockIfNeeded(builder, core);
                    CloseTextBlockIfNeeded(builder, core);

                    var callId = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() ?? "" : "";
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    if (outputIndex < 0)
                    {
                        outputIndex = core.ToolCalls.Count;
                    }

                    var blockIndex = core.NextContentIndex++;
                    core.ToolCalls[outputIndex] = new AnthropicToolCallBlockState
                    {
                        ContentIndex = blockIndex,
                        ToolUseId = string.IsNullOrWhiteSpace(callId) ? $"toolu_{Guid.NewGuid():N}" : callId,
                        Name = name,
                        Started = true,
                        Closed = false
                    };
                    state.Items[outputIndex] = new ResponsesOutputItemState { Kind = "function_call", BlockIndex = blockIndex };

                    AppendSseEvent(builder, "content_block_start", new JsonObject
                    {
                        ["type"] = "content_block_start",
                        ["index"] = blockIndex,
                        ["content_block"] = new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = core.ToolCalls[outputIndex].ToolUseId,
                            ["name"] = name,
                            ["input"] = new JsonObject()
                        }
                    });
                }

                return builder.ToString();
            }

            if (string.Equals(eventName, "response.output_text.delta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventName, "response.refusal.delta", StringComparison.OrdinalIgnoreCase))
            {
                var deltaText = root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(deltaText))
                {
                    return string.Empty;
                }

                core.HadAnyContent = true;
                CloseThinkingBlockIfNeeded(builder, core);
                var textIndex = EnsureTextBlockOpen(builder, core);
                AppendSseEvent(builder, "content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = textIndex,
                    ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = deltaText }
                });
                return builder.ToString();
            }

            if (string.Equals(eventName, "response.reasoning_summary_text.delta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventName, "response.reasoning_text.delta", StringComparison.OrdinalIgnoreCase))
            {
                var deltaText = root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(deltaText))
                {
                    return string.Empty;
                }

                core.HadAnyContent = true;
                var thinkingIndex = EnsureThinkingBlockOpen(builder, core);
                AppendSseEvent(builder, "content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = thinkingIndex,
                    ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = deltaText }
                });
                return builder.ToString();
            }

            if (string.Equals(eventName, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase))
            {
                var outputIndex = root.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number ? oi.GetInt32() : -1;
                var deltaText = root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                if (outputIndex < 0 || string.IsNullOrEmpty(deltaText))
                {
                    return string.Empty;
                }

                if (core.ToolCalls.TryGetValue(outputIndex, out var toolCall) && toolCall.Started && !toolCall.Closed)
                {
                    AppendSseEvent(builder, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = toolCall.ContentIndex,
                        ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = deltaText }
                    });
                }

                return builder.ToString();
            }

            if (string.Equals(eventName, "response.output_item.done", StringComparison.OrdinalIgnoreCase))
            {
                var outputIndex = root.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number ? oi.GetInt32() : -1;
                if (outputIndex < 0 || !state.Items.TryGetValue(outputIndex, out var itemState) || itemState.BlockClosed)
                {
                    return string.Empty;
                }

                if (itemState.Kind == "message")
                {
                    if (core.TextIndex == itemState.BlockIndex && !core.TextClosed)
                    {
                        CloseTextBlockIfNeeded(builder, core);
                        itemState.BlockClosed = true;
                    }
                }
                else if (itemState.Kind == "reasoning")
                {
                    if (core.ThinkingIndex == itemState.BlockIndex && !core.ThinkingClosed)
                    {
                        CloseThinkingBlockIfNeeded(builder, core);
                        itemState.BlockClosed = true;
                    }
                }
                else if (itemState.Kind == "function_call")
                {
                    if (core.ToolCalls.TryGetValue(outputIndex, out var toolCall) && toolCall.Started && !toolCall.Closed)
                    {
                        AppendSseEvent(builder, "content_block_stop", new JsonObject
                        {
                            ["type"] = "content_block_stop",
                            ["index"] = toolCall.ContentIndex
                        });
                        toolCall.Closed = true;
                        itemState.BlockClosed = true;
                    }
                }

                return builder.ToString();
            }

            if (string.Equals(eventName, "response.completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventName, "response.incomplete", StringComparison.OrdinalIgnoreCase))
            {
                state.Completed = true;
                core.ReceivedDoneEvent = true;

                if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.Object)
                {
                    if (responseEl.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                    {
                        state.Model = modelEl.GetString() ?? state.Model;
                    }

                    if (responseEl.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        // Responses input_tokens 含缓存；归一化为 Anthropic 出口的三桶口径。
                        var totalInput = usageEl.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number ? it.GetInt32() : 0;
                        var cachedRead = 0;
                        var cacheWrite = 0;
                        if (usageEl.TryGetProperty("input_tokens_details", out var det) && det.ValueKind == JsonValueKind.Object)
                        {
                            if (det.TryGetProperty("cached_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                            {
                                cachedRead = ct.GetInt32();
                            }

                            if (det.TryGetProperty("cached_creation_tokens", out var cc) && cc.ValueKind == JsonValueKind.Number)
                            {
                                cacheWrite = cc.GetInt32();
                            }
                            else if (det.TryGetProperty("cache_write_tokens", out var cw) && cw.ValueKind == JsonValueKind.Number)
                            {
                                cacheWrite = cw.GetInt32();
                            }
                        }

                        if (totalInput > 0)
                        {
                            core.InputTokens = Math.Max(0, totalInput - cachedRead - cacheWrite);
                        }

                        if (cachedRead > 0)
                        {
                            core.CachedTokens = cachedRead;
                        }

                        if (cacheWrite > 0)
                        {
                            core.CacheCreationTokens = cacheWrite;
                        }

                        if (usageEl.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number && ot.GetInt32() > 0)
                        {
                            core.OutputTokens = ot.GetInt32();
                        }
                    }

                    var incompleteReason = responseEl.TryGetProperty("incomplete_details", out var inc) && inc.ValueKind == JsonValueKind.Object
                        && inc.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString()
                        : null;
                    if (string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
                    {
                        core.StopReason = "max_tokens";
                    }
                }

                if (core.ToolCalls.Values.Any(t => t.Started))
                {
                    core.StopReason = "tool_use";
                }

                return string.Empty;
            }

            if (string.Equals(eventName, "response.failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventName, "error", StringComparison.OrdinalIgnoreCase))
            {
                state.Failed = true;
                return string.Empty;
            }
        }
        catch
        {
            core.ConversionFailed = true;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 将完整的 Anthropic SSE 事件流聚合重建为 Responses SSE 事件流（用于一次性转换完整流的场景）。
    /// </summary>
    public static string BuildResponsesStreamFromAnthropic(string sseBody)
    {
        try
        {
            var state = new ChatToResponsesStreamState();
            var builder = new StringBuilder();
            using var reader = new StringReader(sseBody);
            string? line;
            string currentEvent = string.Empty;
            var dataLines = new List<string>();

            void FlushEvent()
            {
                if (dataLines.Count == 0)
                {
                    currentEvent = string.Empty;
                    return;
                }

                var payload = string.Join("\n", dataLines);
                dataLines.Clear();
                if (!string.IsNullOrWhiteSpace(payload) && !string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append(ConvertAnthropicStreamChunkToResponses(currentEvent, payload, state));
                }

                currentEvent = string.Empty;
            }

            while ((line = reader.ReadLine()) is not null)
            {
                if (TryExtractSseFieldPayload(line, "event", out var eventNameValue))
                {
                    currentEvent = eventNameValue.Trim();
                    continue;
                }

                if (string.IsNullOrEmpty(line))
                {
                    FlushEvent();
                    continue;
                }

                if (TryExtractSseFieldPayload(line, "data", out var dataValue))
                {
                    dataLines.Add(dataValue);
                }
            }

            FlushEvent();
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 判断 Responses 响应对象是否处于失败终态（status=failed/cancelled 或携带 error 对象）。
    /// </summary>
    private static bool IsResponsesFailureStatus(JsonObject root)
    {
        var status = root["status"]?.GetValue<string>();
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return root["error"] is JsonObject;
    }

    /// <summary>
    /// 确保 Anthropic text 内容块已打开，返回其索引。
    /// </summary>
    private static int EnsureTextBlockOpen(StringBuilder builder, AnthropicOpenAiStreamState state)
    {
        if (state.TextIndex >= 0 && !state.TextClosed)
        {
            return state.TextIndex;
        }

        state.TextIndex = state.NextContentIndex++;
        state.TextClosed = false;
        AppendSseEvent(builder, "content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = state.TextIndex,
            ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
        });
        return state.TextIndex;
    }

    /// <summary>
    /// 确保 Anthropic thinking 内容块已打开，返回其索引。已关闭的块用新索引重开（分段思考防御）。
    /// </summary>
    private static int EnsureThinkingBlockOpen(StringBuilder builder, AnthropicOpenAiStreamState state)
    {
        if (state.ThinkingIndex >= 0 && !state.ThinkingClosed)
        {
            return state.ThinkingIndex;
        }

        state.ThinkingIndex = state.NextContentIndex++;
        state.ThinkingClosed = false;
        AppendSseEvent(builder, "content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = state.ThinkingIndex,
            ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "" }
        });
        return state.ThinkingIndex;
    }

    /// <summary>
    /// 把累积的文本/媒体内容落盘为一个 Responses message 输出项。
    /// </summary>
    private static void FlushPendingMessageItem(JsonArray input, ref JsonArray pendingParts, string role)
    {
        if (pendingParts.Count == 0)
        {
            return;
        }

        input.Add(BuildResponsesMessageItem(role, pendingParts));
        pendingParts = new JsonArray();
    }

    /// <summary>
    /// 构造 Responses 的 message 输出项。
    /// </summary>
    private static JsonObject BuildResponsesMessageItem(string role, JsonArray parts)
    {
        return new JsonObject
        {
            ["type"] = "message",
            ["id"] = $"msg_{Guid.NewGuid():N}",
            ["role"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
            ["content"] = parts
        };
    }

    /// <summary>
    /// 解析 Responses 的 input 字段为 Anthropic messages 数组。
    /// </summary>
    private static void ParseResponsesInputToAnthropicMessages(JsonNode? inputNode, JsonArray messages, List<string> systemParts)
    {
        if (inputNode is JsonValue jsonValue && jsonValue.TryGetValue(out string? strValue))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = strValue ?? string.Empty } }
            });
            return;
        }

        if (inputNode is not JsonArray inputArray)
        {
            return;
        }

        JsonObject? currentAssistant = null;
        JsonObject? currentUser = null;

        void FlushAccumulators()
        {
            currentAssistant = null;
            currentUser = null;
        }

        foreach (var item in inputArray)
        {
            if (item is not JsonObject itemObj)
            {
                continue;
            }

            var type = itemObj["type"]?.GetValue<string>() ?? "";

            if (type == "function_call")
            {
                if (currentAssistant is null)
                {
                    currentAssistant = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray() };
                    messages.Add(currentAssistant);
                }

                JsonNode? toolInput;
                var arguments = itemObj["arguments"]?.GetValue<string>() ?? "{}";
                try
                {
                    toolInput = JsonNode.Parse(arguments);
                }
                catch
                {
                    toolInput = arguments;
                }

                ((JsonArray)currentAssistant["content"]!).Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = itemObj["call_id"]?.DeepClone() ?? itemObj["id"]?.DeepClone() ?? $"toolu_{Guid.NewGuid():N}",
                    ["name"] = itemObj["name"]?.DeepClone() ?? string.Empty,
                    ["input"] = toolInput
                });
                currentUser = null;
                continue;
            }

            if (type == "function_call_output")
            {
                if (currentUser is null)
                {
                    currentUser = new JsonObject { ["role"] = "user", ["content"] = new JsonArray() };
                    messages.Add(currentUser);
                }

                ((JsonArray)currentUser["content"]!).Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = itemObj["call_id"]?.DeepClone() ?? string.Empty,
                    ["content"] = itemObj["output"] is JsonValue outputValue && outputValue.TryGetValue<string>(out var outputText)
                        ? outputText
                        : itemObj["output"]?.ToJsonString() ?? string.Empty
                });
                currentAssistant = null;
                continue;
            }

            if (type == "reasoning")
            {
                // 仅还原本桥接层生成的签名载体；上游原生 reasoning 无法构造合法签名，丢弃。
                var encrypted = itemObj["encrypted_content"]?.GetValue<string>();
                if (TryDecodeAnthropicThinkingBridge(encrypted, out var thinkingText, out var signature))
                {
                    if (currentAssistant is null)
                    {
                        currentAssistant = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray() };
                        messages.Add(currentAssistant);
                    }

                    // Anthropic 要求 thinking 块位于 assistant 内容块最前。
                    ((JsonArray)currentAssistant["content"]!).Insert(0, new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = thinkingText,
                        ["signature"] = signature ?? string.Empty
                    });
                }

                continue;
            }

            var role = itemObj["role"]?.GetValue<string>();
            if (string.IsNullOrEmpty(role))
            {
                continue;
            }

            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
            {
                var systemText = ExtractResponsesContentText(itemObj["content"]);
                if (!string.IsNullOrWhiteSpace(systemText))
                {
                    systemParts.Add(systemText);
                }

                FlushAccumulators();
                continue;
            }

            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                if (currentAssistant is null)
                {
                    currentAssistant = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray() };
                    messages.Add(currentAssistant);
                }

                AppendResponsesContentToAnthropicBlocks(itemObj["content"], currentAssistant);
                currentUser = null;
                continue;
            }

            // user 消息：与相邻的 user 内容合并（function_call_output 已在 currentUser 上累积）。
            if (currentUser is null)
            {
                currentUser = new JsonObject { ["role"] = "user", ["content"] = new JsonArray() };
                messages.Add(currentUser);
            }

            AppendResponsesContentToAnthropicBlocks(itemObj["content"], currentUser);
            currentAssistant = null;
        }
    }

    /// <summary>
    /// 把 Responses message item 的 content 部件转换为 Anthropic 内容块并追加到目标消息。
    /// </summary>
    private static void AppendResponsesContentToAnthropicBlocks(JsonNode? content, JsonObject targetMessage)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (!string.IsNullOrEmpty(text))
            {
                ((JsonArray)targetMessage["content"]!).Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }

            return;
        }

        if (content is not JsonArray parts)
        {
            return;
        }

        foreach (var partNode in parts)
        {
            if (partNode is not JsonObject part)
            {
                continue;
            }

            var partType = part["type"]?.GetValue<string>() ?? "";
            if (partType is "input_text" or "output_text" or "text")
            {
                var partText = part["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(partText))
                {
                    ((JsonArray)targetMessage["content"]!).Add(new JsonObject { ["type"] = "text", ["text"] = partText });
                }
            }
            else if (partType == "input_image")
            {
                var url = part["image_url"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var commaIdx = url.IndexOf(',');
                    if (commaIdx > 0)
                    {
                        var meta = url[..commaIdx];
                        var data = url[(commaIdx + 1)..];
                        var mediaType = meta.Replace("data:", "", StringComparison.OrdinalIgnoreCase)
                            .Replace(";base64", "", StringComparison.OrdinalIgnoreCase);
                        ((JsonArray)targetMessage["content"]!).Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = mediaType,
                                ["data"] = data
                            }
                        });
                    }
                }
                else
                {
                    // 远程图片 URL 直接交给 Anthropic 的 url source。
                    ((JsonArray)targetMessage["content"]!).Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "url", ["url"] = url }
                    });
                }
            }
            else if (partType == "input_file")
            {
                var fileData = part["file_data"]?.GetValue<string>() ?? "";
                if (!fileData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var commaIdx = fileData.IndexOf(',');
                if (commaIdx <= 0)
                {
                    continue;
                }

                var meta = fileData[..commaIdx];
                var data = fileData[(commaIdx + 1)..];
                var mediaType = meta.Replace("data:", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(";base64", "", StringComparison.OrdinalIgnoreCase);
                var documentBlock = new JsonObject
                {
                    ["type"] = "document",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = mediaType,
                        ["data"] = data
                    }
                };
                var filename = part["filename"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(filename))
                {
                    documentBlock["title"] = filename;
                }

                ((JsonArray)targetMessage["content"]!).Add(documentBlock);
            }
        }
    }

    /// <summary>
    /// 把 Responses message item 的 content 提取为纯文本（用于 system/developer 角色）。
    /// </summary>
    private static string? ExtractResponsesContentText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is not JsonArray parts)
        {
            return content?.ToJsonString();
        }

        var textParts = new List<string>();
        foreach (var partNode in parts)
        {
            if (partNode is JsonObject part && part["type"]?.GetValue<string>() is "input_text" or "output_text" or "text")
            {
                var t = part["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(t))
                {
                    textParts.Add(t);
                }
            }
        }

        return textParts.Count > 0 ? string.Join("\n", textParts) : null;
    }

    /// <summary>
    /// 将 Anthropic tools 转换为 Responses tools（function 直转，web_search 服务端工具做类型级映射）。
    /// </summary>
    private static void ConvertAnthropicToolsToResponses(JsonObject rootNode, JsonObject payload)
    {
        if (rootNode["tools"] is not JsonArray tools)
        {
            return;
        }

        var responsesTools = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool is not JsonObject toolObj)
            {
                continue;
            }

            var type = toolObj["type"]?.GetValue<string>() ?? "";
            var name = toolObj["name"]?.GetValue<string>();

            if (type.StartsWith("web_search", StringComparison.OrdinalIgnoreCase) && string.Equals(name, "web_search", StringComparison.OrdinalIgnoreCase))
            {
                // Anthropic server tool → Responses 内置 web_search 工具（类型级映射，可选参数无可对应物时丢弃）。
                responsesTools.Add(new JsonObject { ["type"] = "web_search" });
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            responsesTools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = toolObj["description"]?.DeepClone(),
                ["parameters"] = toolObj["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
            });
        }

        if (responsesTools.Count > 0)
        {
            payload["tools"] = responsesTools;
        }
    }

    /// <summary>
    /// 将 Anthropic tool_choice 转换为 Responses tool_choice，并透传 disable_parallel_tool_use。
    /// </summary>
    private static void ConvertAnthropicToolChoiceToResponses(JsonObject rootNode, JsonObject payload)
    {
        if (rootNode["tool_choice"] is not JsonNode tc)
        {
            return;
        }

        if (tc is JsonValue tcValue)
        {
            var tcStr = tcValue.GetValue<string>();
            payload["tool_choice"] = tcStr switch
            {
                "auto" => "auto",
                "any" => "required",
                "none" => "none",
                _ => "auto"
            };
        }
        else if (tc is JsonObject tcObj)
        {
            var type = tcObj["type"]?.GetValue<string>();
            if (string.Equals(type, "tool", StringComparison.OrdinalIgnoreCase) && tcObj["name"] is not null)
            {
                payload["tool_choice"] = new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tcObj["name"]!.DeepClone()
                };
            }
            else
            {
                payload["tool_choice"] = type switch
                {
                    "auto" => "auto",
                    "any" => "required",
                    "none" => "none",
                    _ => "auto"
                };
            }

            if (tcObj["disable_parallel_tool_use"] is JsonValue dptu && dptu.TryGetValue<bool>(out var disabled))
            {
                payload["parallel_tool_calls"] = !disabled;
            }
        }
    }

    /// <summary>
    /// 将 Responses tools 转换为 Anthropic tools（function 直转，web_search 映射为服务端工具）。
    /// </summary>
    private static void ConvertResponsesToolsToAnthropic(JsonObject rootNode, JsonObject payload)
    {
        if (rootNode["tools"] is not JsonArray tools)
        {
            return;
        }

        var claudeTools = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool is not JsonObject toolObj)
            {
                continue;
            }

            var type = toolObj["type"]?.GetValue<string>() ?? "function";

            if (string.Equals(type, "web_search", StringComparison.OrdinalIgnoreCase))
            {
                claudeTools.Add(new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search"
                });
                continue;
            }

            if (!string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                // file_search / computer_use 等 Responses 内置工具在 Anthropic 上游没有对应物，丢弃。
                continue;
            }

            var name = toolObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            claudeTools.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = toolObj["description"]?.DeepClone(),
                ["input_schema"] = toolObj["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
            });
        }

        if (claudeTools.Count > 0)
        {
            payload["tools"] = claudeTools;
        }
    }

    /// <summary>
    /// 将 Responses tool_choice / parallel_tool_calls 转换为 Anthropic tool_choice。
    /// </summary>
    private static void ConvertResponsesToolChoiceToAnthropic(JsonObject rootNode, JsonObject payload)
    {
        var tc = rootNode["tool_choice"];
        var parallel = rootNode["parallel_tool_calls"];

        if (tc is null && parallel is not JsonValue)
        {
            return;
        }

        JsonNode? claudeTc = null;
        var hasParallelConstraint = false;
        var parallelValue = true;

        if (parallel is JsonValue parallelValueNode && parallelValueNode.TryGetValue<bool>(out var p))
        {
            hasParallelConstraint = true;
            parallelValue = p;
        }

        if (tc is JsonValue tcValue)
        {
            var tcStr = tcValue.GetValue<string>();
            claudeTc = tcStr switch
            {
                "auto" => new JsonObject { ["type"] = "auto" },
                "required" => new JsonObject { ["type"] = "any" },
                "none" => new JsonObject { ["type"] = "none" },
                _ => null
            };
        }
        else if (tc is JsonObject tcObj)
        {
            var type = tcObj["type"]?.GetValue<string>();
            if (string.Equals(type, "function", StringComparison.OrdinalIgnoreCase) && tcObj["name"] is not null)
            {
                claudeTc = new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = tcObj["name"]!.DeepClone()
                };
            }
        }

        if (claudeTc is null && hasParallelConstraint)
        {
            claudeTc = new JsonObject { ["type"] = "auto" };
        }

        if (hasParallelConstraint && claudeTc is JsonObject tcResult)
        {
            var tcType = tcResult["type"]?.GetValue<string>();
            if (!string.Equals(tcType, "none", StringComparison.OrdinalIgnoreCase))
            {
                tcResult["disable_parallel_tool_use"] = !parallelValue;
            }
        }

        if (claudeTc is not null)
        {
            payload["tool_choice"] = claudeTc;
        }
    }

    /// <summary>
    /// 从 Responses 请求体解析思考等级（reasoning.effort 优先，output_config.effort 兜底）。
    /// </summary>
    private static string ExtractEffortFromResponsesRequest(JsonObject rootNode)
    {
        if (rootNode["reasoning"] is JsonObject reasoning && reasoning["effort"] is JsonValue effortValue)
        {
            var effort = effortValue.GetValue<string>().Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(effort))
            {
                return effort;
            }
        }

        if (rootNode["output_config"] is JsonObject outputConfig && outputConfig["effort"] is JsonValue outputEffortValue)
        {
            var effort = outputEffortValue.GetValue<string>().Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(effort))
            {
                return effort;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 把 Anthropic 非流式响应 JSON 重建为完整的 Anthropic SSE 事件流。
    /// </summary>
    private static string RebuildAnthropicSseFromResponse(string anthropicBody)
    {
        var root = JsonNode.Parse(anthropicBody) as JsonObject;
        if (root is null)
        {
            return string.Empty;
        }

        var usage = root["usage"] as JsonObject;
        var builder = new StringBuilder();
        AppendSseEvent(builder, "message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = root["id"]?.DeepClone() ?? $"msg_{Guid.NewGuid():N}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = root["model"]?.DeepClone() ?? string.Empty,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = usage?["input_tokens"]?.DeepClone() ?? 0,
                    ["cache_creation_input_tokens"] = usage?["cache_creation_input_tokens"]?.DeepClone() ?? 0,
                    ["cache_read_input_tokens"] = usage?["cache_read_input_tokens"]?.DeepClone() ?? 0,
                    ["output_tokens"] = 0
                },
                ["content"] = new JsonArray()
            }
        });

        if (root["content"] is JsonArray blocks)
        {
            var index = 0;
            foreach (var blockNode in blocks)
            {
                if (blockNode is not JsonObject block)
                {
                    continue;
                }

                var type = block["type"]?.GetValue<string>() ?? "";
                var startBlock = new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = index,
                    ["content_block"] = type switch
                    {
                        "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = "" },
                        "tool_use" => new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = block["id"]?.DeepClone(),
                            ["name"] = block["name"]?.DeepClone(),
                            ["input"] = new JsonObject()
                        },
                        _ => new JsonObject { ["type"] = "text", ["text"] = "" }
                    }
                };
                AppendSseEvent(builder, "content_block_start", startBlock);

                if (type == "thinking")
                {
                    AppendSseEvent(builder, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = index,
                        ["delta"] = new JsonObject
                        {
                            ["type"] = "thinking_delta",
                            ["thinking"] = block["thinking"]?.GetValue<string>() ?? string.Empty
                        }
                    });
                }
                else if (type == "tool_use")
                {
                    var partialJson = block["input"]?.ToJsonString() ?? "{}";
                    if (!string.IsNullOrEmpty(partialJson))
                    {
                        AppendSseEvent(builder, "content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = index,
                            ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = partialJson }
                        });
                    }
                }
                else
                {
                    AppendSseEvent(builder, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = index,
                        ["delta"] = new JsonObject
                        {
                            ["type"] = "text_delta",
                            ["text"] = block["text"]?.GetValue<string>() ?? string.Empty
                        }
                    });
                }

                AppendSseEvent(builder, "content_block_stop", new JsonObject
                {
                    ["type"] = "content_block_stop",
                    ["index"] = index
                });
                index++;
            }
        }

        AppendSseEvent(builder, "message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = usage?["input_tokens"]?.DeepClone() ?? 0,
                ["cache_creation_input_tokens"] = usage?["cache_creation_input_tokens"]?.DeepClone() ?? 0,
                ["cache_read_input_tokens"] = usage?["cache_read_input_tokens"]?.DeepClone() ?? 0,
                ["output_tokens"] = usage?["output_tokens"]?.DeepClone() ?? 0
            },
            ["delta"] = new JsonObject { ["stop_reason"] = root["stop_reason"]?.DeepClone() ?? "end_turn" }
        });
        AppendSseEvent(builder, "message_stop", new JsonObject { ["type"] = "message_stop" });
        return builder.ToString();
    }

    /// <summary>
    /// 编码 thinking 签名桥接载体。
    /// </summary>
    private static string EncodeAnthropicThinkingBridge(string thinking, string? signature)
    {
        var payload = new JsonObject
        {
            ["thinking"] = thinking,
            ["signature"] = signature
        };
        return AnthropicThinkingBridgePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    /// <summary>
    /// 解码 thinking 签名桥接载体；非本格式或内容非法时返回 false。
    /// </summary>
    private static bool TryDecodeAnthropicThinkingBridge(string? value, out string thinking, out string? signature)
    {
        thinking = string.Empty;
        signature = null;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(AnthropicThinkingBridgePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(value[AnthropicThinkingBridgePrefix.Length..]);
            var node = JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
            if (node is null || node["thinking"] is not JsonValue thinkingValue || !thinkingValue.TryGetValue<string>(out var decoded))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(decoded))
            {
                return false;
            }

            thinking = decoded;
            signature = node["signature"] is JsonValue signatureValue && signatureValue.TryGetValue<string>(out var sig) ? sig : null;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
