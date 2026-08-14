using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Protocol;

/// <summary>
/// Responses 协议转换所需的流式转换状态。
/// </summary>
public sealed class ResponsesToChatStreamState
{
    /// <summary>
    /// 是否已发送 assistant 角色分片。
    /// </summary>
    public bool RoleChunkSent { get; set; }
    /// <summary>
    /// 是否已发送 response.completed 对应的结束分片。
    /// </summary>
    public bool Completed { get; set; }
    /// <summary>
    /// 是否出现无法解析的上游流片段。
    /// </summary>
    public bool ConversionFailed { get; set; }
    /// <summary>
    /// 当前响应使用的模型名称。
    /// </summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>
    /// 已累积的正文和推理文本。
    /// </summary>
    public StringBuilder ContentText { get; } = new();
    public StringBuilder ReasoningText { get; } = new();
    /// <summary>
    /// 按 Responses output_index 保存工具调用状态。
    /// </summary>
    public Dictionary<int, ResponsesToolCallState> ToolCalls { get; } = [];
    /// <summary>
    /// 将 Responses output_index 映射为 Chat tool_calls 的连续索引。
    /// </summary>
    public Dictionary<int, int> ToolCallChatIndices { get; } = [];
    /// <summary>
    /// 按 Chat tool index 保存 Responses function_call 输出项标识。
    /// </summary>
    public Dictionary<int, string> ChatToolCallOutputIds { get; } = [];
    /// <summary>
    /// 按 Chat tool index 保存 Responses function_call 输出索引。
    /// </summary>
    public Dictionary<int, int> ChatToolCallOutputIndices { get; } = [];
    /// <summary>
    /// 按 Chat tool index 保存工具调用标识。
    /// </summary>
    public Dictionary<int, string> ChatToolCallIds { get; } = [];
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
}

/// <summary>
/// 保存 Responses 流式 function_call 的增量状态。
/// </summary>
public sealed class ResponsesToolCallState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StringBuilder Arguments { get; } = new();
}

public sealed class ChatToResponsesStreamState
{
    /// <summary>
    /// 是否已发送 response.created 事件。
    /// </summary>
    public bool ResponseStarted { get; set; }
    /// <summary>
    /// 是否已收到真实输出或结束事件，避免无效首帧启动桥接响应。
    /// </summary>
    public bool SawMeaningfulEvent { get; set; }
    /// <summary>
    /// 是否出现无法解析的上游流片段。
    /// </summary>
    public bool ConversionFailed { get; set; }
    /// <summary>
    /// 是否已创建 message 输出项。
    /// </summary>
    public bool MessageAdded { get; set; }
    /// <summary>
    /// 流式转换是否已完成。
    /// </summary>
    public bool Done { get; set; }
    /// <summary>
    /// 当前 Responses 对象的唯一标识。
    /// </summary>
    public string ResponseId { get; set; } = string.Empty;
    /// <summary>
    /// 当前 message 输出项的唯一标识。
    /// </summary>
    public string MessageId { get; set; } = string.Empty;
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>
    /// 创建时间戳。
    /// </summary>
    public long CreatedAt { get; set; }
    /// <summary>
    /// 已累积的输出文本（用 StringBuilder 累积，避免流式 delta 逐次整串复制导致 O(n²)）。
    /// </summary>
    private readonly StringBuilder _outputTextBuilder = new();

    /// <summary>
    /// 已累积的输出文本。
    /// </summary>
    public string OutputText => _outputTextBuilder.ToString();

    /// <summary>
    /// 追加一段输出文本增量。
    /// </summary>
    public void AppendOutputText(string deltaText) => _outputTextBuilder.Append(deltaText);
    /// <summary>
    /// 下一个可分配的工具调用索引。
    /// </summary>
    public int ToolCallIndex { get; set; } = 1;
    /// <summary>
    /// 已发送过的工具调用标识列表。
    /// </summary>
    public List<string> SentToolCallIds { get; } = [];
    /// <summary>
    /// 按 Anthropic 内容块索引保存 Responses function_call 的输出索引。
    /// </summary>
    public Dictionary<int, int> ToolCallOutputIndices { get; } = [];
    /// <summary>
    /// 按 Anthropic 内容块索引保存 Responses function_call 的输出项标识。
    /// </summary>
    public Dictionary<int, string> ToolCallOutputIds { get; } = [];
    /// <summary>
    /// 按 Anthropic 内容块索引保存 Responses function_call 的 call_id。
    /// </summary>
    public Dictionary<int, string> ToolCallCallIds { get; } = [];
    /// <summary>
    /// 按 Chat tool index 保存 Responses function_call 输出项标识。
    /// </summary>
    public Dictionary<int, string> ChatToolCallOutputIds { get; } = [];
    /// <summary>
    /// 按 Chat tool index 保存 Responses function_call 输出索引。
    /// </summary>
    public Dictionary<int, int> ChatToolCallOutputIndices { get; } = [];
    /// <summary>
    /// 按 Chat tool index 保存工具调用标识。
    /// </summary>
    public Dictionary<int, string> ChatToolCallIds { get; } = [];
    /// <summary>
    /// 累积的用量信息。
    /// </summary>
    public (int InputTokens, int CachedTokens, int OutputTokens) Usage { get; set; }
}

/// <summary>
/// 负责 OpenAI Responses API 与 Chat Completions API 之间的协议转换。
/// </summary>
public static partial class ProxyProtocolBridge
{
    /// <summary>
    /// 将 Responses API 请求体转换为 Chat Completions 请求体。
    /// 透传场景下（上游是 OpenAI）调用方可直接转发原始请求体，无需调用此方法。
    /// </summary>
    public static string ConvertResponsesRequestToChat(string requestBody, string targetModelName, bool enableStreaming)
    {
        var root = JsonNode.Parse(requestBody) as JsonObject;
        if (root is null)
        {
            return requestBody;
        }

        var messages = new JsonArray();

        // instructions → system 消息
        if (root.TryGetPropertyValue("instructions", out var instructionsNode))
        {
            var instructionText = instructionsNode?.ToString();
            if (!string.IsNullOrWhiteSpace(instructionText))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = instructionText
                });
            }
        }

        // input → 消息列表
        if (root.TryGetPropertyValue("input", out var inputNode) && inputNode is not null)
        {
            ParseResponsesInputToMessages(inputNode, messages);
        }

        var payload = new JsonObject
        {
            ["model"] = targetModelName,
            ["messages"] = messages,
            ["stream"] = enableStreaming
        };

        // 透传通用参数
        CopyIfPresent(root, payload, "temperature");
        CopyIfPresent(root, payload, "top_p");
        CopyIfPresent(root, payload, "user");
        CopyIfPresent(root, payload, "metadata");
        CopyIfPresent(root, payload, "store");

        // Codex 上游要求 store 必须为 false；原始 Chat 请求不携带此字段时强制设置
        if (payload["store"] is null)
        {
            payload["store"] = false;
        }

        // max_output_tokens → max_tokens
        if (root.TryGetPropertyValue("max_output_tokens", out var maxTokens) && maxTokens is not null)
        {
            payload["max_tokens"] = maxTokens.DeepClone();
        }

        // reasoning.effort / output_config.effort → reasoning_effort
        if (root.TryGetPropertyValue("reasoning", out var reasoningNode) && reasoningNode is JsonObject reasoning)
        {
            if (reasoning.TryGetPropertyValue("effort", out var effort) && effort is not null)
            {
                payload["reasoning_effort"] = effort.DeepClone();
            }
        }

        // Claude Code 的 Responses 请求可能通过 output_config.effort 传递思考等级。
        if (payload["reasoning_effort"] is null
            && root.TryGetPropertyValue("output_config", out var outputConfigNode)
            && outputConfigNode is JsonObject outputConfig
            && outputConfig.TryGetPropertyValue("effort", out var outputConfigEffort)
            && outputConfigEffort is not null)
        {
            payload["reasoning_effort"] = outputConfigEffort.DeepClone();
        }

        // tools → 转换扁平结构为 Chat Completions 嵌套结构。
        // Responses 的内置工具由 Responses 上游执行，普通 Chat 上游无法实现，不能原样转发。
        if (root.TryGetPropertyValue("tools", out var toolsNode) && toolsNode is JsonArray toolsArray)
        {
            var chatTools = new JsonArray();
            foreach (var tool in toolsArray)
            {
                if (tool is not JsonObject toolObj)
                {
                    continue;
                }

                var toolType = toolObj["type"]?.ToString() ?? "function";
                if (string.Equals(toolType, "function", StringComparison.OrdinalIgnoreCase))
                {
                    var chatTool = new JsonObject { ["type"] = "function" };
                    var function = new JsonObject();
                    if (toolObj.TryGetPropertyValue("name", out var name))
                    {
                        function["name"] = name?.DeepClone();
                    }

                    if (toolObj.TryGetPropertyValue("description", out var desc))
                    {
                        function["description"] = desc?.DeepClone();
                    }

                    if (toolObj.TryGetPropertyValue("parameters", out var parameters))
                    {
                        function["parameters"] = parameters?.DeepClone();
                    }

                    chatTool["function"] = function;
                    chatTools.Add(chatTool);
                }
                else if (string.Equals(toolType, "custom", StringComparison.OrdinalIgnoreCase))
                {
                    // Chat Completions 新版允许 custom 工具，保留其原始结构。
                    chatTools.Add(toolObj.DeepClone());
                }
                // web_search、file_search、computer_use 等 Responses 内置工具直接丢弃，
                // 避免发送 Chat 上游不认识的 tools[].type 导致整次请求失败。
            }

            payload["tools"] = chatTools;

            if (root.TryGetPropertyValue("tool_choice", out var toolChoiceNode) && toolChoiceNode is not null)
            {
                payload["tool_choice"] = ConvertResponsesToolChoiceToChat(toolChoiceNode, chatTools);
            }
        }
        else if (root.TryGetPropertyValue("tool_choice", out var toolChoiceNode) && toolChoiceNode is not null)
        {
            payload["tool_choice"] = ConvertResponsesToolChoiceToChat(toolChoiceNode);
        }

        // stream_options
        if (enableStreaming)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return payload.ToJsonString();
    }

    /// <summary>
    /// 将 Chat Completions 请求体转换为 Responses API 请求体。
    /// </summary>
    public static string ConvertChatRequestToResponses(string requestBody, string targetModelName, bool enableStreaming)
    {
        var root = JsonNode.Parse(requestBody) as JsonObject;
        if (root is null)
        {
            return requestBody;
        }

        var payload = new JsonObject
        {
            ["model"] = targetModelName,
            ["stream"] = enableStreaming
        };

        var input = new JsonArray();
        string? instructions = null;

        if (root.TryGetPropertyValue("messages", out var messagesNode) && messagesNode is JsonArray messages)
        {
            foreach (var messageNode in messages)
            {
                if (messageNode is not JsonObject messageObj)
                {
                    continue;
                }

                var role = messageObj["role"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    var systemText = ExtractOpenAiContentAsString(messageObj["content"]);
                    if (!string.IsNullOrWhiteSpace(systemText))
                    {
                        instructions = string.IsNullOrWhiteSpace(instructions)
                            ? systemText
                            : string.Concat(instructions, "\n", systemText);
                    }

                    continue;
                }

                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = messageObj["tool_call_id"]?.DeepClone() ?? string.Empty,
                        ["output"] = messageObj["content"]?.DeepClone() ?? string.Empty
                    });
                    continue;
                }

                var content = ConvertChatContentToResponses(messageObj["content"], role);
                var hasVisibleMessageContent = content is JsonArray contentArray && contentArray.Count > 0;

                // assistant 的 tool_calls 在 Responses/Codex 协议里必须是 top-level function_call，
                // 不能嵌在 message.content[] 中，否则上游会报：
                // Invalid value: 'function_call'. Supported values are ...
                // 另外，如果 assistant 这一轮只有 tool_calls 没有文本，也不能额外生成空 assistant message。
                if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) || hasVisibleMessageContent)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = role,
                        ["content"] = content
                    });
                }

                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && messageObj["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var toolCall in toolCalls)
                    {
                        if (toolCall is not JsonObject toolCallObj)
                        {
                            continue;
                        }

                        var callId = toolCallObj["id"]?.ToString();
                        var responseCallId = string.IsNullOrWhiteSpace(callId)
                            ? $"call_{Guid.NewGuid():N}"
                            : callId;

                        // Responses 要求 function_call 的资源 id 使用 fc_ 前缀；
                        // Chat Completions 的 tool call id 通常是 call_，只能作为 call_id 保留。
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["id"] = $"fc_{Guid.NewGuid():N}",
                            ["call_id"] = responseCallId,
                            ["name"] = toolCallObj["function"]?["name"]?.DeepClone() ?? string.Empty,
                            ["arguments"] = toolCallObj["function"]?["arguments"]?.DeepClone() ?? "{}"
                        });
                    }
                }
            }
        }

        payload["input"] = input;

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            payload["instructions"] = instructions;
        }

        // 透传通用参数（temperature / top_p / user / metadata 对标准 OpenAI Responses 有效；
        // Codex 上游不接受，会在 PrepareRequestBody 出口的 NormalizeResponsesBody 中按目标 URL 剔除）。
        CopyIfPresent(root, payload, "temperature");
        CopyIfPresent(root, payload, "top_p");
        CopyIfPresent(root, payload, "user");
        CopyIfPresent(root, payload, "metadata");
        CopyIfPresent(root, payload, "store");

        // Codex 上游要求 store 必须为 false；原始 Chat 请求不携带此字段时强制设置
        if (payload["store"] is null)
        {
            payload["store"] = false;
        }

        // max_completion_tokens / max_tokens → max_output_tokens。
        // 标准 OpenAI Responses API 接受此参数；Codex 上游不接受，但会在 PrepareRequestBody
        // 出口的 NormalizeResponsesBody 中按目标 URL 剔除，无需在此处特殊处理。
        if (root.TryGetPropertyValue("max_completion_tokens", out var maxCompletionTokens) && maxCompletionTokens is not null)
        {
            payload["max_output_tokens"] = maxCompletionTokens.DeepClone();
        }
        else if (root.TryGetPropertyValue("max_tokens", out var maxTokens) && maxTokens is not null)
        {
            payload["max_output_tokens"] = maxTokens.DeepClone();
        }

        if (root.TryGetPropertyValue("reasoning_effort", out var reasoningEffortNode) && reasoningEffortNode is not null)
        {
            payload["reasoning"] = new JsonObject
            {
                ["effort"] = reasoningEffortNode.DeepClone()
            };
        }

        if (root.TryGetPropertyValue("tools", out var toolsNode) && toolsNode is JsonArray toolsArray)
        {
            var responsesTools = new JsonArray();
            foreach (var toolNode in toolsArray)
            {
                if (toolNode is not JsonObject toolObj)
                {
                    continue;
                }

                var toolType = toolObj["type"]?.ToString() ?? "function";
                if (string.Equals(toolType, "function", StringComparison.OrdinalIgnoreCase))
                {
                    responsesTools.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["name"] = toolObj["function"]?["name"]?.DeepClone() ?? string.Empty,
                        ["description"] = toolObj["function"]?["description"]?.DeepClone(),
                        ["parameters"] = toolObj["function"]?["parameters"]?.DeepClone() ?? new JsonObject()
                    });
                }
                else
                {
                    responsesTools.Add(toolObj.DeepClone());
                }
            }

            payload["tools"] = responsesTools;
        }

        if (root.TryGetPropertyValue("tool_choice", out var toolChoiceNode) && toolChoiceNode is not null)
        {
            payload["tool_choice"] = ConvertChatToolChoiceToResponses(toolChoiceNode);
        }

        if (root.TryGetPropertyValue("parallel_tool_calls", out var parallelToolCallsNode)
            && parallelToolCallsNode is JsonValue parallelToolCallsValue
            && parallelToolCallsValue.TryGetValue(out bool parallelToolCalls))
        {
            payload["parallel_tool_calls"] = parallelToolCalls;
        }

        return payload.ToJsonString();
    }

    /// <summary>
    /// 将 Chat Completions 非流式响应转换为 Responses API 非流式响应。
    /// </summary>
    public static string ConvertChatResponseToResponses(string chatResponseBody)
    {
        try
        {
            var root = JsonNode.Parse(chatResponseBody) as JsonObject;
            if (root is null || root["choices"] is not JsonArray choices || choices.Count == 0)
            {
                return string.Empty;
            }

            var choice = choices[0] as JsonObject;
            var message = choice?["message"] as JsonObject;
            if (message is null)
            {
                return string.Empty;
            }

            var outputs = new JsonArray();

            // reasoning 必须作为 Responses 顶层 reasoning 输出项，不能混入 message.content。
            var reasoningText = ExtractChatReasoningText(message);
            if (!string.IsNullOrWhiteSpace(reasoningText))
            {
                outputs.Add(new JsonObject
                {
                    ["type"] = "reasoning",
                    ["id"] = $"rs_{Guid.NewGuid():N}",
                    ["status"] = "completed",
                    ["summary"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "summary_text",
                            ["text"] = reasoningText
                        }
                    }
                });
            }

            var messageContent = ConvertChatMessageContentToResponses(message["content"]);
            var refusal = ExtractChatRefusal(message);
            if (!string.IsNullOrWhiteSpace(refusal))
            {
                messageContent ??= new JsonArray();
                if (!messageContent.Any(part => part is JsonObject partObject
                    && string.Equals(partObject["type"]?.ToString(), "refusal", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(partObject["refusal"]?.ToString(), refusal, StringComparison.Ordinal)))
                {
                    messageContent.Add(new JsonObject
                    {
                        ["type"] = "refusal",
                        ["refusal"] = refusal
                    });
                }
            }

            if (messageContent is JsonArray { Count: > 0 } outputContent)
            {
                outputs.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["id"] = $"msg_{Guid.NewGuid():N}",
                    ["status"] = "completed",
                    ["role"] = "assistant",
                    ["content"] = outputContent
                });
            }

            // 新版 tool_calls 与旧版 function_call 都转换为 Responses function_call 输出项。
            if (message["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var toolCall in toolCalls)
                {
                    if (toolCall is JsonObject toolCallObject)
                    {
                        outputs.Add(BuildResponsesFunctionCall(toolCallObject));
                    }
                }
            }
            else if (message["function_call"] is JsonObject functionCall)
            {
                outputs.Add(BuildResponsesFunctionCall(functionCall));
            }

            // 没有文本、推理、拒答或工具调用时，不生成空 assistant 成功响应。
            if (outputs.Count == 0)
            {
                return string.Empty;
            }

            var chatId = root["id"]?.ToString() ?? $"chatcmpl-{Guid.NewGuid():N}";
            var model = root["model"]?.ToString() ?? string.Empty;
            var created = root["created"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var chatUsage = root["usage"] as JsonObject;
            var responsesUsage = new JsonObject
            {
                ["input_tokens"] = chatUsage?["prompt_tokens"]?.DeepClone() ?? 0,
                ["output_tokens"] = chatUsage?["completion_tokens"]?.DeepClone() ?? 0,
                ["total_tokens"] = chatUsage?["total_tokens"]?.DeepClone() ?? 0,
                ["input_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = (chatUsage?["prompt_tokens_details"] as JsonObject)?["cached_tokens"]?.DeepClone() ?? 0
                }
            };

            var responseId = chatId.StartsWith("resp_", StringComparison.OrdinalIgnoreCase)
                ? chatId
                : $"resp_{chatId}";
            return new JsonObject
            {
                ["id"] = responseId,
                ["object"] = "response",
                ["created_at"] = created,
                ["status"] = "completed",
                ["model"] = model,
                ["output"] = outputs,
                ["usage"] = responsesUsage
            }.ToJsonString();
        }
        catch
        {
            // 转换失败必须返回空结果，由调用层触发 fallback，而不是伪装成空响应成功。
            return string.Empty;
        }
    }

    /// <summary>
    /// 将 Chat message.content 的字符串或内容数组转换为 Responses 输出文本块。
    /// </summary>
    private static JsonArray? ConvertChatMessageContentToResponses(JsonNode? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content is JsonValue nullValue && nullValue.ToJsonString() == "null")
        {
            return null;
        }

        var result = new JsonArray();
        if (content is JsonValue stringValue)
        {
            if (stringValue.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
            {
                result.Add(new JsonObject { ["type"] = "output_text", ["text"] = text });
            }

            return result;
        }

        if (content is not JsonArray parts)
        {
            return result;
        }

        foreach (var part in parts)
        {
            if (part is not JsonObject partObject)
            {
                continue;
            }

            var type = partObject["type"]?.ToString() ?? string.Empty;
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
            {
                var text = partObject["text"]?.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    result.Add(new JsonObject { ["type"] = "output_text", ["text"] = text });
                }
            }
            else if (string.Equals(type, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                var refusal = partObject["refusal"]?.ToString() ?? partObject["text"]?.ToString();
                if (!string.IsNullOrEmpty(refusal))
                {
                    result.Add(new JsonObject { ["type"] = "refusal", ["refusal"] = refusal });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 提取 Chat message 上的 reasoning/refusal 兼容字段。
    /// </summary>
    private static string ExtractChatReasoningText(JsonObject message)
    {
        foreach (var propertyName in new[] { "reasoning_content", "reasoning", "thinking" })
        {
            if (message[propertyName] is JsonNode value)
            {
                var text = value is JsonValue
                    ? value.ToString()
                    : value["text"]?.ToString() ?? value["content"]?.ToString() ?? value["summary_text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 提取 message-level 或 content block 中的拒答文本。
    /// </summary>
    private static string ExtractChatRefusal(JsonObject message)
    {
        var refusal = message["refusal"]?.ToString();
        if (!string.IsNullOrWhiteSpace(refusal))
        {
            return refusal;
        }

        if (message["content"] is JsonArray parts)
        {
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (string.Equals(part["type"]?.ToString(), "refusal", StringComparison.OrdinalIgnoreCase))
                {
                    var text = part["refusal"]?.ToString() ?? part["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 构造 Responses function_call 输出项，兼容 tool_calls.function 与 legacy function_call 两种形态。
    /// </summary>
    private static JsonObject BuildResponsesFunctionCall(JsonObject source)
    {
        var function = source["function"] as JsonObject ?? source;
        var callId = source["id"]?.ToString();
        var arguments = function["arguments"];
        return new JsonObject
        {
            ["type"] = "function_call",
            ["id"] = $"fc_{Guid.NewGuid():N}",
            ["status"] = "completed",
            ["call_id"] = string.IsNullOrWhiteSpace(callId) ? $"call_{Guid.NewGuid():N}" : callId,
            ["name"] = function["name"]?.DeepClone() ?? string.Empty,
            ["arguments"] = arguments is null ? "{}" : arguments is JsonValue ? arguments.ToString() : arguments.ToJsonString()
        };
    }

    /// <summary>
    /// 将 Anthropic 非流式响应转换为 Responses API 非流式响应。
    /// </summary>
    public static string ConvertAnthropicResponseToResponses(string anthropicBody)
    {
        // 先转成 OpenAI 格式，再转成 Responses 格式
        var openAiBody = BuildOpenAiResponseFromAnthropic(anthropicBody, "", 0, 0, 0);
        return string.IsNullOrEmpty(openAiBody)
            ? string.Empty
            : ConvertChatResponseToResponses(openAiBody);
    }

    /// <summary>
    /// 将单个 Chat Completions 流式 SSE 数据块转换为 Responses API 流式事件。
    /// </summary>
    public static string ConvertChatStreamChunkToResponses(string sseJsonText, ChatToResponsesStreamState state)
    {
        var builder = new StringBuilder();

        try
        {
            using var doc = JsonDocument.Parse(sseJsonText);
            var root = doc.RootElement;

            // 真实 OpenAI 流式分片会携带 usage:null，只有对象值才能提取用量。
            if (root.TryGetProperty("usage", out var usageEl)
                && usageEl.ValueKind == JsonValueKind.Object)
            {
                var input = usageEl.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                var output = usageEl.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                var cached = 0;
                if (usageEl.TryGetProperty("prompt_tokens_details", out var ptd)
                    && ptd.TryGetProperty("cached_tokens", out var cachedEl))
                {
                    cached = cachedEl.GetInt32();
                }

                state.Usage = (input, cached, output);
            }

            // 只有真实输出或完成事件才启动 Responses 响应，避免 role/usage-only 分片制造空响应。
            var hasChoices = root.TryGetProperty("choices", out var availableChoices)
                && availableChoices.ValueKind == JsonValueKind.Array
                && availableChoices.GetArrayLength() > 0;
            var hasMeaningfulChoice = false;
            if (hasChoices)
            {
                foreach (var availableChoice in availableChoices.EnumerateArray())
                {
                    if (availableChoice.TryGetProperty("finish_reason", out var finishReasonElement)
                        && finishReasonElement.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(finishReasonElement.GetString()))
                    {
                        hasMeaningfulChoice = true;
                        break;
                    }

                    if (!availableChoice.TryGetProperty("delta", out var availableDelta)
                        || availableDelta.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (ExtractDeltaContent(availableDelta) is { Length: > 0 }
                        || ExtractReasoningFromElement(availableDelta).Length > 0
                        || (availableDelta.TryGetProperty("tool_calls", out var availableToolCalls)
                            && availableToolCalls.ValueKind == JsonValueKind.Array
                            && availableToolCalls.GetArrayLength() > 0))
                    {
                        hasMeaningfulChoice = true;
                        break;
                    }
                }
            }

            if (!hasMeaningfulChoice)
            {
                return builder.ToString();
            }

            // 首次发送 response.created + response.in_progress
            if (!state.ResponseStarted)
            {
                state.ResponseStarted = true;
                // 上游流式首帧可能没有可用 id，这里统一补成可落库的 responseId。
                var rawResponseId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                state.ResponseId = string.IsNullOrWhiteSpace(rawResponseId)
                    ? $"resp_{Guid.NewGuid():N}"
                    : (rawResponseId.StartsWith("resp_") ? rawResponseId : $"resp_{rawResponseId}");
                state.Model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? "" : "";
                state.CreatedAt = root.TryGetProperty("created", out var createdEl) ? createdEl.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                builder.Append(BuildResponsesEvent("response.created", new JsonObject
                {
                    ["id"] = state.ResponseId,
                    ["object"] = "response",
                    ["created_at"] = state.CreatedAt,
                    ["status"] = "in_progress",
                    ["model"] = state.Model,
                    ["output"] = new JsonArray(),
                    ["usage"] = null
                }));

                builder.Append(BuildResponsesEvent("response.in_progress", new JsonObject
                {
                    ["id"] = state.ResponseId,
                    ["object"] = "response",
                    ["created_at"] = state.CreatedAt,
                    ["status"] = "in_progress",
                    ["model"] = state.Model,
                    ["output"] = new JsonArray(),
                    ["usage"] = null
                }));
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return builder.ToString();
            }

            var choice = choices[0];
            var delta = choice.TryGetProperty("delta", out var d) ? d : default;

            // 角色标记 → 创建 message 输出项
            if (delta.ValueKind != JsonValueKind.Undefined && delta.TryGetProperty("role", out var roleEl)
                && roleEl.GetString() == "assistant" && !state.MessageAdded)
            {
                state.MessageAdded = true;
                state.MessageId = $"msg_{Guid.NewGuid():N}";

                builder.Append(BuildResponsesEvent("response.output_item.added", new JsonObject
                {
                    ["type"] = "message",
                    ["id"] = state.MessageId,
                    ["status"] = "in_progress",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray()
                }, outputIndex: 0));

                builder.Append(BuildResponsesEvent("response.content_part.added", new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = ""
                }, outputIndex: 0, contentIndex: 0));
            }

            // 文本增量
            if (delta.ValueKind != JsonValueKind.Undefined
                && delta.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String)
            {
                var deltaText = contentEl.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(deltaText))
                {
                    EnsureMessageStarted(state, builder);
                    builder.Append(BuildResponsesEvent("response.output_text.delta",
                        deltaText, outputIndex: 0, contentIndex: 0));
                    state.AppendOutputText(deltaText);
                }
            }

            // reasoning 增量
            if (delta.ValueKind != JsonValueKind.Undefined
                && delta.TryGetProperty("reasoning_content", out var reasoningEl)
                && reasoningEl.ValueKind == JsonValueKind.String)
            {
                var reasoningText = reasoningEl.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(reasoningText))
                {
                    builder.Append(BuildResponsesEvent("response.reasoning_summary_text.delta", reasoningText));
                }
            }

            // 工具调用增量
            if (delta.ValueKind != JsonValueKind.Undefined
                && delta.TryGetProperty("tool_calls", out var toolCallsEl)
                && toolCallsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCallsEl.EnumerateArray())
                {
                    var callId = tc.TryGetProperty("id", out var tcIdEl) ? tcIdEl.GetString() ?? "" : "";
                    var idx = tc.TryGetProperty("index", out var tcIdxEl) ? tcIdxEl.GetInt32() : state.ToolCallIndex;
                    var funcName = tc.TryGetProperty("function", out var funcEl)
                        ? (funcEl.TryGetProperty("name", out var fnEl) ? fnEl.GetString() ?? "" : "")
                        : "";
                    var funcArgs = tc.TryGetProperty("function", out var funcEl2)
                        ? (funcEl2.TryGetProperty("arguments", out var faEl) ? faEl.GetString() ?? "" : "")
                        : "";

                    if (!state.ChatToolCallOutputIndices.TryGetValue(idx, out var outputIndex))
                    {
                        outputIndex = state.SentToolCallIds.Count + (state.MessageAdded ? 1 : 0);
                        state.ChatToolCallOutputIndices[idx] = outputIndex;
                    }

                    if (string.IsNullOrEmpty(callId) && state.ChatToolCallIds.TryGetValue(idx, out var existingCallId))
                    {
                        callId = existingCallId;
                    }

                    if (string.IsNullOrEmpty(callId))
                    {
                        callId = $"call_{Guid.NewGuid():N}";
                    }

                    if (!state.ChatToolCallOutputIds.TryGetValue(idx, out var itemId))
                    {
                        itemId = $"fc_{Guid.NewGuid():N}";
                        state.ChatToolCallOutputIds[idx] = itemId;
                        state.ChatToolCallIds[idx] = callId;
                        state.SentToolCallIds.Add(callId);
                        state.ToolCallIndex = Math.Max(state.ToolCallIndex, idx + 1);

                        builder.Append(BuildResponsesEvent("response.output_item.added", new JsonObject
                        {
                            ["type"] = "function_call",
                            ["id"] = itemId,
                            ["status"] = "in_progress",
                            ["call_id"] = callId,
                            ["name"] = funcName,
                            ["arguments"] = ""
                        }, outputIndex: outputIndex));
                    }

                    if (!string.IsNullOrEmpty(funcArgs))
                    {
                        builder.Append(BuildResponsesEvent("response.function_call_arguments.delta",
                            funcArgs, outputIndex: outputIndex, itemId: itemId));
                    }
                }
            }

            // 结束原因
            if (choice.TryGetProperty("finish_reason", out var finishEl)
                && finishEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(finishEl.GetString()))
            {
                // 关闭 message 输出项
                if (state.MessageAdded)
                {
                    // 只取一次最终文本，避免 StringBuilder 支撑的属性多次 ToString 重复分配。
                    var outputText = state.OutputText;
                    builder.Append(BuildResponsesEvent("response.output_text.done",
                        outputText, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.content_part.done", new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = outputText
                    }, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "message",
                        ["id"] = state.MessageId,
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "output_text", ["text"] = outputText }
                        }
                    }, outputIndex: 0));
                }

                // 关闭工具调用输出项，保持 Chat tool index 与输出索引的一致映射。
                foreach (var toolCall in state.ChatToolCallOutputIndices.OrderBy(pair => pair.Value))
                {
                    var chatIndex = toolCall.Key;
                    var outputIndex = toolCall.Value;
                    var callId = state.ChatToolCallIds.TryGetValue(chatIndex, out var mappedCallId)
                        ? mappedCallId
                        : string.Empty;
                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["call_id"] = callId
                    }, outputIndex: outputIndex));
                }

                // 完成
                var (inp, cached, outp) = state.Usage;
                builder.Append(BuildResponsesEvent("response.completed", new JsonObject
                {
                    ["id"] = state.ResponseId,
                    ["object"] = "response",
                    ["created_at"] = state.CreatedAt,
                    ["status"] = "completed",
                    ["model"] = state.Model,
                    ["output"] = new JsonArray(),
                    ["usage"] = new JsonObject
                    {
                        ["prompt_tokens"] = inp,
                        ["completion_tokens"] = outp,
                        ["total_tokens"] = inp + outp,
                        ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached }
                    }
                }));

                state.Done = true;
            }
        }
        catch
        {
            state.ConversionFailed = true;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 将 Anthropic SSE 事件流实时转换为 Responses API 流式事件。
    /// </summary>
    public static string ConvertAnthropicStreamChunkToResponses(
        string eventName,
        string payloadJson,
        ChatToResponsesStreamState state)
    {
        var builder = new StringBuilder();

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            // 只有已知的 Anthropic 事件才参与转换，未知事件不能制造空 Responses 流。
            var isMessageStart = string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase);
            var isContentBlockStart = string.Equals(eventName, "content_block_start", StringComparison.OrdinalIgnoreCase);
            var isContentBlockDelta = string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase);
            var isMessageDelta = string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase);
            var isMessageStop = string.Equals(eventName, "message_stop", StringComparison.OrdinalIgnoreCase);
            if (!isMessageStart && !isContentBlockStart && !isContentBlockDelta && !isMessageDelta && !isMessageStop)
            {
                return string.Empty;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                state.ConversionFailed = true;
                return string.Empty;
            }

            // 在确认存在可转换输出时才创建 Responses 生命周期，保留尚未写出时的 fallback 能力。
            void EnsureResponseStarted()
            {
                if (state.ResponseStarted)
                {
                    return;
                }

                state.ResponseStarted = true;
                state.ResponseId = $"resp_{Guid.NewGuid():N}";
                state.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                builder.Append(BuildResponsesEvent("response.created", new JsonObject
                {
                    ["id"] = state.ResponseId,
                    ["object"] = "response",
                    ["created_at"] = state.CreatedAt,
                    ["status"] = "in_progress",
                    ["model"] = state.Model,
                    ["output"] = new JsonArray(),
                    ["usage"] = null
                }));

                builder.Append(BuildResponsesEvent("response.in_progress", new JsonObject
                {
                    ["id"] = state.ResponseId,
                    ["object"] = "response",
                    ["created_at"] = state.CreatedAt,
                    ["status"] = "in_progress",
                    ["model"] = state.Model,
                    ["output"] = new JsonArray(),
                    ["usage"] = null
                }));
            }

            // message_start 仅提供元数据，不能单独启动空的 Responses 流。
            if (isMessageStart)
            {
                if (root.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("model", out var modelEl))
                    {
                        state.Model = modelEl.GetString() ?? state.Model;
                    }

                    if (message.TryGetProperty("usage", out var usageEl))
                    {
                        state.Usage = (
                            usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                            usageEl.TryGetProperty("cache_read_input_tokens", out var ct) ? ct.GetInt32() : 0,
                            0
                        );
                    }
                }
            }

            // content_block_start → 如果是 text 类型则创建 message 输出项
            if (isContentBlockStart)
            {
                if (root.TryGetProperty("content_block", out var block) && block.ValueKind == JsonValueKind.Object)
                {
                    var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;

                    if (blockType == "text" || blockType == "tool_use" || blockType == "thinking")
                    {
                        state.SawMeaningfulEvent = true;
                        EnsureResponseStarted();
                    }

                    if (blockType == "text" && !state.MessageAdded)
                    {
                        state.MessageAdded = true;
                        state.MessageId = $"msg_{Guid.NewGuid():N}";

                        builder.Append(BuildResponsesEvent("response.output_item.added", new JsonObject
                        {
                            ["type"] = "message",
                            ["id"] = state.MessageId,
                            ["status"] = "in_progress",
                            ["role"] = "assistant",
                            ["content"] = new JsonArray()
                        }, outputIndex: 0));

                        builder.Append(BuildResponsesEvent("response.content_part.added", new JsonObject
                        {
                            ["type"] = "output_text",
                            ["text"] = ""
                        }, outputIndex: 0, contentIndex: 0));
                    }

                    // tool_use → 创建 function_call 输出项
                    if (blockType == "tool_use")
                    {
                        // 使用 Anthropic content block index 关联后续 input_json_delta，避免多个工具调用串线。
                        var blockIndex = root.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
                            ? indexEl.GetInt32()
                            : state.ToolCallOutputIndices.Count;
                        var callId = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        var outputIndex = state.SentToolCallIds.Count + (state.MessageAdded ? 1 : 0);
                        var itemId = $"fc_{Guid.NewGuid():N}";

                        if (string.IsNullOrEmpty(callId))
                        {
                            callId = $"call_{Guid.NewGuid():N}";
                        }

                        state.SentToolCallIds.Add(callId);
                        state.ToolCallOutputIndices[blockIndex] = outputIndex;
                        state.ToolCallOutputIds[blockIndex] = itemId;
                        state.ToolCallCallIds[blockIndex] = callId;

                        builder.Append(BuildResponsesEvent("response.output_item.added", new JsonObject
                        {
                            ["type"] = "function_call",
                            ["id"] = itemId,
                            ["status"] = "in_progress",
                            ["call_id"] = callId,
                            ["name"] = name,
                            ["arguments"] = ""
                        }, outputIndex: outputIndex));
                    }
                }
            }

            // content_block_delta → 文本/推理/工具参数增量
            if (isContentBlockDelta)
            {
                if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                    var deltaText = delta.TryGetProperty("text", out var dtEl) ? dtEl.GetString() ?? "" : "";
                    var partialJson = delta.TryGetProperty("partial_json", out var pjEl) ? pjEl.GetString() ?? "" : "";

                    if ((deltaType == "text_delta" && !string.IsNullOrEmpty(deltaText))
                        || (deltaType == "thinking_delta" && !string.IsNullOrEmpty(deltaText))
                        || (deltaType == "input_json_delta" && !string.IsNullOrEmpty(partialJson)))
                    {
                        state.SawMeaningfulEvent = true;
                        EnsureResponseStarted();
                    }

                    if (deltaType == "text_delta" && !string.IsNullOrEmpty(deltaText))
                    {
                        EnsureMessageStarted(state, builder);
                        builder.Append(BuildResponsesEvent("response.output_text.delta",
                            deltaText, outputIndex: 0, contentIndex: 0));
                        state.AppendOutputText(deltaText);
                    }
                    else if (deltaType == "thinking_delta" && !string.IsNullOrEmpty(deltaText))
                    {
                        builder.Append(BuildResponsesEvent("response.reasoning_summary_text.delta", deltaText));
                    }
                    else if (deltaType == "input_json_delta" && !string.IsNullOrEmpty(partialJson))
                    {
                        var blockIndex = root.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
                            ? indexEl.GetInt32()
                            : -1;
                        if (blockIndex >= 0 && state.ToolCallOutputIndices.TryGetValue(blockIndex, out var outputIndex))
                        {
                            state.ToolCallOutputIds.TryGetValue(blockIndex, out var itemId);
                            builder.Append(BuildResponsesEvent("response.function_call_arguments.delta",
                                partialJson, outputIndex: outputIndex, itemId: itemId));
                        }
                    }
                }
            }

            // message_delta → 提取用量和停止原因
            if (isMessageDelta)
            {
                if (root.TryGetProperty("usage", out var usageEl))
                {
                    var outTokens = usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                    state.Usage = (state.Usage.InputTokens, state.Usage.CachedTokens, outTokens);
                }
            }

            // message_stop → 完成整个响应
            if (isMessageStop)
            {
                if (!state.SawMeaningfulEvent)
                {
                    state.ConversionFailed = true;
                    return string.Empty;
                }

                EnsureResponseStarted();

                if (state.MessageAdded)
                {
                    // 只取一次最终文本，避免 StringBuilder 支撑的属性多次 ToString 重复分配。
                    var outputText = state.OutputText;
                    builder.Append(BuildResponsesEvent("response.output_text.done",
                        outputText, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.content_part.done", new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = outputText
                    }, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "message",
                        ["id"] = state.MessageId,
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "output_text", ["text"] = outputText }
                        }
                    }, outputIndex: 0));
                }

                foreach (var toolCall in state.ToolCallOutputIndices.OrderBy(pair => pair.Value))
                {
                    var blockIndex = toolCall.Key;
                    var outputIndex = toolCall.Value;
                    var callId = state.ToolCallCallIds.TryGetValue(blockIndex, out var mappedCallId)
                        ? mappedCallId
                        : state.SentToolCallIds.ElementAtOrDefault(outputIndex - (state.MessageAdded ? 1 : 0)) ?? string.Empty;
                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["call_id"] = callId
                    }, outputIndex: outputIndex));
                }

                var (inp, cached, outp) = state.Usage;
                builder.Append(BuildResponsesEvent("response.completed", new JsonObject
                {
                    ["id"] = state.ResponseId,
                    ["object"] = "response",
                    ["created_at"] = state.CreatedAt,
                    ["status"] = "completed",
                    ["model"] = state.Model,
                    ["output"] = new JsonArray(),
                    ["usage"] = new JsonObject
                    {
                        ["prompt_tokens"] = inp,
                        ["completion_tokens"] = outp,
                        ["total_tokens"] = inp + outp,
                        ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached }
                    }
                }));

                state.Done = true;
            }
        }
        catch
        {
            state.ConversionFailed = true;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 将 Responses API 非流式响应转换为 Chat Completions 非流式响应。
    /// </summary>
    public static string ConvertResponsesResponseToChat(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var responseId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? $"chatcmpl-{Guid.NewGuid():N}"
                : $"chatcmpl-{Guid.NewGuid():N}";
            var responseModel = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                ? modelEl.GetString() ?? modelName
                : modelName;
            var createdAt = root.TryGetProperty("created_at", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.Number
                ? createdAtEl.GetInt64()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string? contentText = null;
            string? reasoningText = null;
            var toolCalls = new JsonArray();
            var finishReason = "stop";

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemTypeEl) || itemTypeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var itemType = itemTypeEl.GetString();
                if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var textParts = new List<string>();
                        foreach (var part in contentEl.EnumerateArray())
                        {
                            if (!part.TryGetProperty("type", out var partTypeEl) || partTypeEl.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var partType = partTypeEl.GetString();
                            if ((string.Equals(partType, "output_text", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(partType, "text", StringComparison.OrdinalIgnoreCase))
                                && part.TryGetProperty("text", out var textEl)
                                && textEl.ValueKind == JsonValueKind.String)
                            {
                                textParts.Add(textEl.GetString() ?? string.Empty);
                            }
                            else if (string.Equals(partType, "refusal", StringComparison.OrdinalIgnoreCase))
                            {
                                var refusal = part.TryGetProperty("refusal", out var refusalEl)
                                    && refusalEl.ValueKind == JsonValueKind.String
                                    ? refusalEl.GetString() ?? string.Empty
                                    : part.TryGetProperty("text", out var refusalTextEl)
                                      && refusalTextEl.ValueKind == JsonValueKind.String
                                        ? refusalTextEl.GetString() ?? string.Empty
                                        : string.Empty;
                                if (!string.IsNullOrEmpty(refusal))
                                {
                                    textParts.Add(refusal);
                                }
                            }
                            else if ((string.Equals(partType, "reasoning", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(partType, "reasoning_summary", StringComparison.OrdinalIgnoreCase))
                                     && part.TryGetProperty("text", out var reasoningEl)
                                     && reasoningEl.ValueKind == JsonValueKind.String)
                            {
                                reasoningText = string.Concat(reasoningText, reasoningEl.GetString() ?? string.Empty);
                            }
                        }

                        if (textParts.Count > 0)
                        {
                            contentText = string.Concat(contentText, string.Join(string.Empty, textParts));
                        }
                    }
                }
                else if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    var callId = item.TryGetProperty("call_id", out var callIdEl) && callIdEl.ValueKind == JsonValueKind.String
                        ? callIdEl.GetString() ?? string.Empty
                        : string.Empty;
                    var toolName = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;
                    var arguments = item.TryGetProperty("arguments", out var argsEl)
                        ? argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString() ?? "{}" : argsEl.GetRawText()
                        : "{}";

                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = string.IsNullOrWhiteSpace(callId) ? $"call_{Guid.NewGuid():N}" : callId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = toolName,
                            ["arguments"] = arguments
                        }
                    });
                    finishReason = "tool_calls";
                }
            }

            var promptTokens = inputTokens;
            var completionTokens = outputTokens;
            var inputTokensFromUsage = false;
            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                if (usageEl.TryGetProperty("input_tokens", out var inputEl) && inputEl.ValueKind == JsonValueKind.Number)
                {
                    promptTokens = inputEl.GetInt32();
                    inputTokensFromUsage = true;
                }

                if (usageEl.TryGetProperty("output_tokens", out var usageOutputTokensEl) && usageOutputTokensEl.ValueKind == JsonValueKind.Number)
                {
                    completionTokens = usageOutputTokensEl.GetInt32();
                }

                if (usageEl.TryGetProperty("input_tokens_details", out var detailsEl)
                    && detailsEl.ValueKind == JsonValueKind.Object
                    && detailsEl.TryGetProperty("cached_tokens", out var cachedEl)
                    && cachedEl.ValueKind == JsonValueKind.Number)
                {
                    cachedTokens = cachedEl.GetInt32();
                }
            }

            // OpenAI 协议的 prompt_tokens 含缓存命中部分。响应体未提供 input_tokens 时，
            // 传入参数是不含缓存的新输入，必须还原为 新输入+缓存 再输出，避免客户端计费低估。
            if (!inputTokensFromUsage)
            {
                promptTokens = promptTokens + cachedTokens;
            }

            if (toolCalls.Count == 0 && string.IsNullOrWhiteSpace(contentText) && string.IsNullOrWhiteSpace(reasoningText))
            {
                return string.Empty;
            }

            var messageObject = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = contentText ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(reasoningText))
            {
                messageObject["reasoning_content"] = reasoningText;
            }

            if (toolCalls.Count > 0)
            {
                messageObject["tool_calls"] = toolCalls;
            }

            return new JsonObject
            {
                ["id"] = responseId,
                ["object"] = "chat.completion",
                ["created"] = createdAt,
                ["model"] = responseModel,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = messageObject,
                        ["finish_reason"] = finishReason
                    }
                },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = promptTokens,
                    ["prompt_tokens_details"] = new JsonObject
                    {
                        ["cached_tokens"] = cachedTokens,
                        ["cached_creation_tokens"] = 0
                    },
                    ["completion_tokens"] = completionTokens,
                    ["total_tokens"] = promptTokens + completionTokens
                }
            }.ToJsonString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 将 Responses SSE 事件流整体转换为 Chat Completions SSE。
    /// </summary>
    public static string ConvertResponsesStreamingToChat(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        return ConvertResponsesStreamingToChat(
            responseBody,
            new ResponsesToChatStreamState
            {
                Model = modelName,
                InputTokens = inputTokens,
                CachedTokens = cachedTokens,
                OutputTokens = outputTokens
            });
    }

    /// <summary>
    /// 将单个 Responses SSE 事件转换为 Chat Completions SSE，并复用同一转换状态。
    /// </summary>
    public static string ConvertResponsesStreamingToChat(string responseBody, ResponsesToChatStreamState state)
    {
        try
        {
            var contentText = state.ContentText;
            var reasoningText = state.ReasoningText;
            var toolCalls = state.ToolCalls;
            var builder = new StringBuilder();
            var finishReason = "stop";

            if (state.Completed)
            {
                return string.Empty;
            }

            using var reader = new StringReader(responseBody);
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

                if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    currentEvent = string.Empty;
                    return;
                }

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                var eventType = !string.IsNullOrWhiteSpace(currentEvent)
                    ? currentEvent
                    : (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                        ? typeEl.GetString() ?? string.Empty
                        : string.Empty);

                if (string.Equals(eventType, "response.created", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(eventType, "response.in_progress", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("response", out var responseEl)
                        && responseEl.ValueKind == JsonValueKind.Object
                        && responseEl.TryGetProperty("model", out var modelEl)
                        && modelEl.ValueKind == JsonValueKind.String)
                    {
                        state.Model = modelEl.GetString() ?? state.Model;
                    }

                    currentEvent = string.Empty;
                    return;
                }

                if (string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
                {
                    var deltaText = root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String
                        ? deltaEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        if (!state.RoleChunkSent)
                        {
                            builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = string.Empty
                            }, null, null));
                            state.RoleChunkSent = true;
                        }

                        contentText.Append(deltaText);
                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                        {
                            ["content"] = deltaText
                        }, null, null));
                    }

                    currentEvent = string.Empty;
                    return;
                }

                if (string.Equals(eventType, "response.refusal.delta", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(eventType, "response.refusal.done", StringComparison.OrdinalIgnoreCase))
                {
                    var deltaText = root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String
                        ? deltaEl.GetString() ?? string.Empty
                        : root.TryGetProperty("refusal", out var refusalEl) && refusalEl.ValueKind == JsonValueKind.String
                            ? refusalEl.GetString() ?? string.Empty
                            : string.Empty;
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        if (!state.RoleChunkSent)
                        {
                            builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = string.Empty
                            }, null, null));
                            state.RoleChunkSent = true;
                        }

                        contentText.Append(deltaText);
                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                        {
                            ["content"] = deltaText
                        }, null, null));
                    }

                    currentEvent = string.Empty;
                    return;
                }

                if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("item", out var messageItemEl)
                    && messageItemEl.ValueKind == JsonValueKind.Object
                    && messageItemEl.TryGetProperty("type", out var messageItemTypeEl)
                    && messageItemTypeEl.ValueKind == JsonValueKind.String
                    && string.Equals(messageItemTypeEl.GetString(), "message", StringComparison.OrdinalIgnoreCase)
                    && messageItemEl.TryGetProperty("content", out var messageContentEl)
                    && messageContentEl.ValueKind == JsonValueKind.Array)
                {
                    var extractedTexts = new List<string>();
                    foreach (var contentPart in messageContentEl.EnumerateArray())
                    {
                        if (!contentPart.TryGetProperty("type", out var contentPartTypeEl)
                            || contentPartTypeEl.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var contentPartType = contentPartTypeEl.GetString();
                        if ((string.Equals(contentPartType, "output_text", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(contentPartType, "text", StringComparison.OrdinalIgnoreCase))
                            && contentPart.TryGetProperty("text", out var contentPartTextEl)
                            && contentPartTextEl.ValueKind == JsonValueKind.String)
                        {
                            extractedTexts.Add(contentPartTextEl.GetString() ?? string.Empty);
                        }
                    }

                    if (extractedTexts.Count > 0)
                    {
                        var deltaText = string.Concat(extractedTexts);
                        if (!state.RoleChunkSent)
                        {
                            builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = string.Empty
                            }, null, null));
                            state.RoleChunkSent = true;
                        }

                        contentText.Append(deltaText);
                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                        {
                            ["content"] = deltaText
                        }, null, null));
                    }

                    currentEvent = string.Empty;
                    return;
                }

                if (string.Equals(eventType, "response.reasoning_summary_text.delta", StringComparison.OrdinalIgnoreCase))
                {
                    var deltaText = root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String
                        ? deltaEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        if (!state.RoleChunkSent)
                        {
                            builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = string.Empty
                            }, null, null));
                            state.RoleChunkSent = true;
                        }

                        reasoningText.Append(deltaText);
                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                        {
                            ["reasoning_content"] = deltaText
                        }, null, null));
                    }

                    currentEvent = string.Empty;
                    return;
                }

                if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("item", out var itemEl)
                    && itemEl.ValueKind == JsonValueKind.Object
                    && itemEl.TryGetProperty("type", out var itemTypeEl)
                    && itemTypeEl.ValueKind == JsonValueKind.String
                    && string.Equals(itemTypeEl.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    var index = root.TryGetProperty("output_index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
                        ? indexEl.GetInt32()
                        : toolCalls.Count;
                    var callId = itemEl.TryGetProperty("call_id", out var callIdEl) && callIdEl.ValueKind == JsonValueKind.String
                        ? callIdEl.GetString() ?? string.Empty
                        : string.Empty;
                    var name = itemEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;

                    if (!state.ToolCallChatIndices.TryGetValue(index, out var chatIndex))
                    {
                        chatIndex = state.ToolCallChatIndices.Count;
                        state.ToolCallChatIndices[index] = chatIndex;
                    }

                    if (!toolCalls.TryGetValue(index, out var toolCall))
                    {
                        toolCall = new ResponsesToolCallState();
                        toolCalls[index] = toolCall;
                    }

                    if (!string.IsNullOrWhiteSpace(callId))
                    {
                        toolCall.Id = callId;
                    }
                    else if (string.IsNullOrWhiteSpace(toolCall.Id))
                    {
                        toolCall.Id = $"call_{Guid.NewGuid():N}";
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        toolCall.Name = name;
                    }

                    if (!state.RoleChunkSent)
                    {
                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = string.Empty
                        }, null, null));
                        state.RoleChunkSent = true;
                    }

                    builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                    {
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = chatIndex,
                                ["id"] = toolCall.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = toolCall.Name,
                                    ["arguments"] = string.Empty
                                }
                            }
                        }
                    }, null, null));

                    currentEvent = string.Empty;
                    return;
                }

                if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase))
                {
                    var outputIndex = root.TryGetProperty("output_index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
                        ? indexEl.GetInt32()
                        : -1;
                    var deltaText = root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String
                        ? deltaEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (outputIndex >= 0 && !string.IsNullOrEmpty(deltaText))
                    {
                        if (!toolCalls.TryGetValue(outputIndex, out var toolCall))
                        {
                            toolCall = new ResponsesToolCallState
                            {
                                Id = $"call_{Guid.NewGuid():N}"
                            };
                            toolCalls[outputIndex] = toolCall;
                        }

                        toolCall.Arguments.Append(deltaText);

                        if (!state.ToolCallChatIndices.TryGetValue(outputIndex, out var chatIndex))
                        {
                            chatIndex = state.ToolCallChatIndices.Count;
                            state.ToolCallChatIndices[outputIndex] = chatIndex;
                        }

                        if (!state.RoleChunkSent)
                        {
                            builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = string.Empty
                            }, null, null));
                            state.RoleChunkSent = true;
                        }

                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                        {
                            ["tool_calls"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = chatIndex,
                                    ["function"] = new JsonObject
                                    {
                                        ["arguments"] = deltaText
                                    }
                                }
                            }
                        }, null, null));
                    }

                    currentEvent = string.Empty;
                    return;
                }

                if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("response", out var completedResponse)
                    && completedResponse.ValueKind == JsonValueKind.Object)
                {
                    if (completedResponse.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                    {
                        state.Model = modelEl.GetString() ?? state.Model;
                    }

                    if (completedResponse.TryGetProperty("output", out var completedOutputEl)
                        && completedOutputEl.ValueKind == JsonValueKind.Array)
                    {
                        var extractedTexts = new List<string>();
                        foreach (var outputItem in completedOutputEl.EnumerateArray())
                        {
                            if (!outputItem.TryGetProperty("type", out var outputItemTypeEl)
                                || outputItemTypeEl.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var outputItemType = outputItemTypeEl.GetString();
                            if (string.Equals(outputItemType, "message", StringComparison.OrdinalIgnoreCase)
                                && outputItem.TryGetProperty("content", out var outputContentEl)
                                && outputContentEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var outputContentPart in outputContentEl.EnumerateArray())
                                {
                                    if (!outputContentPart.TryGetProperty("type", out var outputContentTypeEl)
                                        || outputContentTypeEl.ValueKind != JsonValueKind.String)
                                    {
                                        continue;
                                    }

                                    var outputContentType = outputContentTypeEl.GetString();
                                    if ((string.Equals(outputContentType, "output_text", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(outputContentType, "text", StringComparison.OrdinalIgnoreCase))
                                        && outputContentPart.TryGetProperty("text", out var outputContentTextEl)
                                        && outputContentTextEl.ValueKind == JsonValueKind.String)
                                    {
                                        extractedTexts.Add(outputContentTextEl.GetString() ?? string.Empty);
                                    }
                                    else if ((string.Equals(outputContentType, "reasoning", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(outputContentType, "reasoning_summary", StringComparison.OrdinalIgnoreCase))
                                             && outputContentPart.TryGetProperty("text", out var outputReasoningTextEl)
                                             && outputReasoningTextEl.ValueKind == JsonValueKind.String)
                                    {
                                        var completedReasoning = outputReasoningTextEl.GetString() ?? string.Empty;
                                        if (!string.IsNullOrEmpty(completedReasoning)
                                            && reasoningText.Length == 0)
                                        {
                                            if (!state.RoleChunkSent)
                                            {
                                                builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                                {
                                                    ["role"] = "assistant",
                                                    ["content"] = string.Empty
                                                }, null, null));
                                                state.RoleChunkSent = true;
                                            }

                                            reasoningText.Append(completedReasoning);
                                            builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                            {
                                                ["reasoning_content"] = completedReasoning
                                            }, null, null));
                                        }
                                    }
                                }
                            }
                            else if (string.Equals(outputItemType, "reasoning", StringComparison.OrdinalIgnoreCase)
                                     && outputItem.TryGetProperty("summary", out var summaryEl)
                                     && summaryEl.ValueKind == JsonValueKind.Array
                                     && reasoningText.Length == 0)
                            {
                                var completedReasoning = string.Concat(summaryEl.EnumerateArray()
                                    .Where(summaryPart => summaryPart.TryGetProperty("text", out var summaryTextEl)
                                        && summaryTextEl.ValueKind == JsonValueKind.String)
                                    .Select(summaryPart => summaryPart.GetProperty("text").GetString() ?? string.Empty));
                                if (!string.IsNullOrEmpty(completedReasoning))
                                {
                                    if (!state.RoleChunkSent)
                                    {
                                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                        {
                                            ["role"] = "assistant",
                                            ["content"] = string.Empty
                                        }, null, null));
                                        state.RoleChunkSent = true;
                                    }

                                    reasoningText.Append(completedReasoning);
                                    builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                    {
                                        ["reasoning_content"] = completedReasoning
                                    }, null, null));
                                }
                            }
                            else if (string.Equals(outputItemType, "function_call", StringComparison.OrdinalIgnoreCase))
                            {
                                var index = outputItem.TryGetProperty("output_index", out var outputIndexEl)
                                    && outputIndexEl.ValueKind == JsonValueKind.Number
                                    ? outputIndexEl.GetInt32()
                                    : completedOutputEl.EnumerateArray().ToList().IndexOf(outputItem);
                                var callId = outputItem.TryGetProperty("call_id", out var callIdEl)
                                    && callIdEl.ValueKind == JsonValueKind.String
                                    ? callIdEl.GetString() ?? string.Empty
                                    : string.Empty;
                                var name = outputItem.TryGetProperty("name", out var nameEl)
                                    && nameEl.ValueKind == JsonValueKind.String
                                    ? nameEl.GetString() ?? string.Empty
                                    : string.Empty;
                                var arguments = outputItem.TryGetProperty("arguments", out var argumentsEl)
                                    ? argumentsEl.ValueKind == JsonValueKind.String
                                        ? argumentsEl.GetString() ?? string.Empty
                                        : argumentsEl.GetRawText()
                                    : string.Empty;

                                if (!toolCalls.TryGetValue(index, out var toolCall))
                                {
                                    toolCall = new ResponsesToolCallState
                                    {
                                        Id = string.IsNullOrWhiteSpace(callId) ? $"call_{Guid.NewGuid():N}" : callId,
                                        Name = name
                                    };
                                    toolCalls[index] = toolCall;

                                    if (!state.RoleChunkSent)
                                    {
                                        builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                        {
                                            ["role"] = "assistant",
                                            ["content"] = string.Empty
                                        }, null, null));
                                        state.RoleChunkSent = true;
                                    }

                                    builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                    {
                                        ["tool_calls"] = new JsonArray
                                        {
                                            new JsonObject
                                            {
                                                ["index"] = index,
                                                ["id"] = toolCall.Id,
                                                ["type"] = "function",
                                                ["function"] = new JsonObject
                                                {
                                                    ["name"] = toolCall.Name,
                                                    ["arguments"] = string.Empty
                                                }
                                            }
                                        }
                                    }, null, null));
                                }

                                if (!string.IsNullOrEmpty(arguments) && toolCall.Arguments.Length == 0)
                                {
                                    toolCall.Arguments.Append(arguments);
                                    builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                    {
                                        ["tool_calls"] = new JsonArray
                                        {
                                            new JsonObject
                                            {
                                                ["index"] = index,
                                                ["function"] = new JsonObject
                                                {
                                                    ["arguments"] = arguments
                                                }
                                            }
                                        }
                                    }, null, null));
                                }
                            }
                        }

                        if (extractedTexts.Count > 0 && contentText.Length == 0)
                        {
                            var completedText = string.Concat(extractedTexts);
                            if (!state.RoleChunkSent)
                            {
                                builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                                {
                                    ["role"] = "assistant",
                                    ["content"] = string.Empty
                                }, null, null));
                                state.RoleChunkSent = true;
                            }

                            contentText.Append(completedText);
                            builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject
                            {
                                ["content"] = completedText
                            }, null, null));
                        }
                    }

                    var inputTokensFromUsage = false;
                    if (completedResponse.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (usageEl.TryGetProperty("input_tokens", out var inputEl) && inputEl.ValueKind == JsonValueKind.Number)
                        {
                            state.InputTokens = inputEl.GetInt32();
                            inputTokensFromUsage = true;
                        }

                        if (usageEl.TryGetProperty("output_tokens", out var outputEl) && outputEl.ValueKind == JsonValueKind.Number)
                        {
                            state.OutputTokens = outputEl.GetInt32();
                        }

                        if (usageEl.TryGetProperty("input_tokens_details", out var detailsEl)
                            && detailsEl.ValueKind == JsonValueKind.Object
                            && detailsEl.TryGetProperty("cached_tokens", out var cachedEl)
                            && cachedEl.ValueKind == JsonValueKind.Number)
                        {
                            state.CachedTokens = cachedEl.GetInt32();
                        }
                    }

                    finishReason = toolCalls.Count > 0 ? "tool_calls" : "stop";
                    state.Completed = true;
                    // OpenAI 协议的 prompt_tokens 含缓存命中部分。completed 未提供 input_tokens 时，
                    // state.InputTokens 是调用方 seeding 的"不含缓存的新输入"，必须还原为 新输入+缓存
                    // 再输出，否则客户端侧按 prompt_tokens 计费会低估。
                    var promptTokens = inputTokensFromUsage ? state.InputTokens : state.InputTokens + state.CachedTokens;
                    builder.Append(BuildChatCompletionChunk(state.Model, new JsonObject(), finishReason, new JsonObject
                    {
                        ["prompt_tokens"] = promptTokens,
                        ["prompt_tokens_details"] = new JsonObject
                        {
                            ["cached_tokens"] = state.CachedTokens,
                            ["cached_creation_tokens"] = 0
                        },
                        ["completion_tokens"] = state.OutputTokens,
                        ["total_tokens"] = promptTokens + state.OutputTokens
                    }));
                    builder.Append("data: [DONE]\n\n");
                }

                currentEvent = string.Empty;
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
            return builder.ToString();
        }
        catch
        {
            // 转换失败不能把原始 Responses SSE 当作 Chat SSE 返回。
            state.ConversionFailed = true;
            return string.Empty;
        }
    }

    /// <summary>
    /// 转换 Chat 消息内容为 Responses content 数组。
    /// </summary>
    private static JsonNode? ConvertChatContentToResponses(JsonNode? content, string role)
    {
        if (content is null)
        {
            return new JsonArray();
        }

        if (content is JsonValue value && value.TryGetValue(out string? stringContent))
        {
            return new JsonArray
            {
                new JsonObject
                {
                    ["type"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "output_text" : "input_text",
                    ["text"] = stringContent ?? string.Empty
                }
            };
        }

        if (content is not JsonArray contentArray)
        {
            return content.DeepClone();
        }

        var result = new JsonArray();
        foreach (var part in contentArray)
        {
            if (part is not JsonObject partObj)
            {
                continue;
            }

            var type = partObj["type"]?.ToString() ?? string.Empty;
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new JsonObject
                {
                    ["type"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "output_text" : "input_text",
                    ["text"] = partObj["text"]?.DeepClone() ?? string.Empty
                });
                continue;
            }

            if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = partObj["image_url"]?["url"]?.DeepClone() ?? string.Empty
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 转换 Chat tool_choice 为 Responses tool_choice。
    /// </summary>
    private static JsonNode? ConvertChatToolChoiceToResponses(JsonNode toolChoice)
    {
        if (toolChoice is JsonValue value && value.TryGetValue(out string? stringValue))
        {
            return stringValue;
        }

        if (toolChoice is JsonObject obj)
        {
            var typeValue = obj["type"]?.ToString() ?? string.Empty;
            if (string.Equals(typeValue, "function", StringComparison.OrdinalIgnoreCase))
            {
                var functionName = obj["function"]?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(functionName))
                {
                    return new JsonObject
                    {
                        ["type"] = "function",
                        ["name"] = functionName
                    };
                }
            }
        }

        return toolChoice.DeepClone();
    }

    /// <summary>
    /// 构造单个 Chat Completions SSE 数据块。
    /// </summary>
    private static string BuildChatCompletionChunk(string modelName, JsonObject deltaObject, string? finishReason, JsonObject? usage)
    {
        var payload = new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = modelName,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = deltaObject,
                    ["finish_reason"] = finishReason is null ? null : JsonValue.Create(finishReason)
                }
            }
        };

        if (usage is not null)
        {
            payload["usage"] = usage;
        }

        return $"data: {payload.ToJsonString()}\n\n";
    }

    /// <summary>
    /// 从 Responses 请求体中提取模型名称。
    /// </summary>
    public static string ExtractResponsesModel(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            return doc.RootElement.TryGetProperty("model", out var modelEl)
                ? modelEl.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 从 Responses 请求体中判断是否启用流式。
    /// </summary>
    public static bool ExtractResponsesStream(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            return doc.RootElement.TryGetProperty("stream", out var streamEl)
                && streamEl.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从 Responses 请求体中提取 reasoning effort。
    /// </summary>
    public static string ExtractResponsesReasoningEffort(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("reasoning", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.Object
                && reasoning.TryGetProperty("effort", out var reasoningEffort)
                && reasoningEffort.ValueKind == JsonValueKind.String)
            {
                return reasoningEffort.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            }

            if (doc.RootElement.TryGetProperty("output_config", out var outputConfig)
                && outputConfig.ValueKind == JsonValueKind.Object
                && outputConfig.TryGetProperty("effort", out var outputConfigEffort)
                && outputConfigEffort.ValueKind == JsonValueKind.String)
            {
                return outputConfigEffort.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    /// <summary>
    /// 解析 Responses 的 input 字段为 Chat Completions 的 messages 数组。
    /// </summary>
    private static void ParseResponsesInputToMessages(JsonNode inputNode, JsonArray messages)
    {
        // 纯字符串 → 单条 user 消息
        if (inputNode is JsonValue jsonValue && jsonValue.TryGetValue(out string? strValue))
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = strValue });
            return;
        }

        // 数组
        if (inputNode is not JsonArray inputArray)
        {
            return;
        }

        JsonObject? lastAssistant = null;

        foreach (var item in inputArray)
        {
            if (item is not JsonObject itemObj)
            {
                continue;
            }

            var type = itemObj["type"]?.ToString() ?? "";

            // function_call_output → tool 消息
            if (type == "function_call_output")
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = itemObj["call_id"]?.DeepClone() ?? "",
                    ["content"] = itemObj["output"]?.DeepClone() ?? ""
                });
                lastAssistant = null;
                continue;
            }

            // function_call → 合并到 assistant 消息的 tool_calls
            if (type == "function_call")
            {
                if (lastAssistant is null)
                {
                    lastAssistant = new JsonObject { ["role"] = "assistant", ["content"] = "" };
                    messages.Add(lastAssistant);
                }

                if (lastAssistant["tool_calls"] is not JsonArray)
                {
                    lastAssistant["tool_calls"] = new JsonArray();
                }

                ((JsonArray)lastAssistant["tool_calls"]!).Add(new JsonObject
                {
                    ["id"] = itemObj["call_id"]?.DeepClone() ?? itemObj["id"]?.DeepClone() ?? "",
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = itemObj["name"]?.DeepClone() ?? "",
                        ["arguments"] = itemObj["arguments"]?.DeepClone() ?? "{}"
                    }
                });
                continue;
            }

            // 带角色的普通消息
            var role = itemObj["role"]?.ToString();
            if (!string.IsNullOrEmpty(role))
            {
                var content = ConvertResponsesContentToChat(itemObj["content"], role);
                var msg = new JsonObject { ["role"] = role, ["content"] = content };
                messages.Add(msg);
                lastAssistant = role == "assistant" ? msg : null;
            }
        }
    }

    /// <summary>
    /// 转换 Responses 内容格式为 Chat Completions 内容格式。
    /// </summary>
    private static JsonNode? ConvertResponsesContentToChat(JsonNode? content, string role)
    {
        if (content is null)
        {
            return "";
        }

        if (content is JsonValue sv && sv.TryGetValue(out string? str))
        {
            return str;
        }

        if (content is not JsonArray contentArray)
        {
            return content.DeepClone();
        }

        var result = new JsonArray();
        foreach (var item in contentArray)
        {
            if (item is not JsonObject itemObj)
            {
                continue;
            }

            var type = itemObj["type"]?.ToString() ?? "";
            switch (type)
            {
                case "input_text":
                case "output_text":
                    result.Add(new JsonObject { ["type"] = "text", ["text"] = itemObj["text"]?.DeepClone() ?? "" });
                    break;
                case "input_image":
                    result.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = itemObj["image_url"]?.DeepClone() ?? "" }
                    });
                    break;
                default:
                    if (itemObj.ContainsKey("text"))
                    {
                        result.Add(new JsonObject { ["type"] = "text", ["text"] = itemObj["text"]?.DeepClone() ?? "" });
                    }
                    break;
            }
        }

        return result.Count == 1 && result[0]?["type"]?.ToString() == "text"
            ? result[0]!["text"]?.DeepClone() ?? ""
            : result;
    }

    /// <summary>
    /// 转换 Responses 的 tool_choice 格式为 Chat Completions 格式。
    /// </summary>
    private static JsonNode? ConvertResponsesToolChoiceToChat(
        JsonNode toolChoice,
        JsonArray? availableTools = null)
    {
        if (toolChoice is JsonValue jv && jv.TryGetValue(out string? sv))
        {
            // 内置工具被过滤后，required 已无法满足，改为不强制调用工具，避免上游再次拒绝请求。
            return string.Equals(sv, "required", StringComparison.OrdinalIgnoreCase)
                && availableTools is not null
                && availableTools.Count == 0
                ? "none"
                : sv;
        }

        if (toolChoice is JsonObject obj)
        {
            var typeVal = obj["type"]?.ToString() ?? string.Empty;
            if (string.Equals(typeVal, "function", StringComparison.OrdinalIgnoreCase)
                && obj.ContainsKey("name"))
            {
                var functionName = obj["name"]?.ToString() ?? string.Empty;
                var functionExists = availableTools is null
                    || availableTools
                        .OfType<JsonObject>()
                        .Any(tool => string.Equals(
                            tool["type"]?.ToString(),
                            "function",
                            StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                tool["function"]?["name"]?.ToString(),
                                functionName,
                                StringComparison.Ordinal));
                return functionExists
                    ? new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = obj["name"]?.DeepClone() }
                    }
                    : "none";
            }

            if (string.Equals(typeVal, "custom", StringComparison.OrdinalIgnoreCase))
            {
                return toolChoice.DeepClone();
            }

            // Responses 内置工具的 tool_choice 在 Chat 上游没有对应能力。
            return "none";
        }

        return toolChoice.DeepClone();
    }

    /// <summary>
    /// 确保 message 输出项已创建。
    /// </summary>
    private static void EnsureMessageStarted(ChatToResponsesStreamState state, StringBuilder builder)
    {
        if (state.MessageAdded)
        {
            return;
        }

        state.MessageAdded = true;
        state.MessageId = $"msg_{Guid.NewGuid():N}";

        builder.Append(BuildResponsesEvent("response.output_item.added", new JsonObject
        {
            ["type"] = "message",
            ["id"] = state.MessageId,
            ["status"] = "in_progress",
            ["role"] = "assistant",
            ["content"] = new JsonArray()
        }, outputIndex: 0));

        builder.Append(BuildResponsesEvent("response.content_part.added", new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = ""
        }, outputIndex: 0, contentIndex: 0));
    }

    /// <summary>
    /// 构造单个 Responses SSE 事件，参数为 JSON 对象。
    /// </summary>
    private static string BuildResponsesEvent(string eventType, JsonObject data, int outputIndex = -1, int contentIndex = -1, string? itemId = null)
    {
        var evt = new JsonObject { ["type"] = eventType };
        if (outputIndex >= 0)
        {
            evt["output_index"] = outputIndex;
        }
        if (contentIndex >= 0)
        {
            evt["content_index"] = contentIndex;
        }
        if (itemId is not null)
        {
            evt["item_id"] = itemId;
        }

        // 把 data 中的字段合并到事件对象
        if (string.Equals(eventType, "response.created", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.in_progress", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase))
        {
            evt["response"] = data;
        }
        else if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase))
        {
            evt["item"] = data;
        }
        else if (string.Equals(eventType, "response.content_part.added", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.content_part.done", StringComparison.OrdinalIgnoreCase))
        {
            evt["part"] = data;
        }
        else
        {
            // delta 类型事件，data 直接是字符串
            return $"event: {eventType}\ndata: {data.ToJsonString()}\n\n";
        }

        return $"event: {eventType}\ndata: {evt.ToJsonString()}\n\n";
    }

    /// <summary>
    /// 构造单个 Responses SSE 事件，参数为纯文本 delta。
    /// </summary>
    private static string BuildResponsesEvent(string eventType, string deltaText, int outputIndex = -1, int contentIndex = -1, string? itemId = null)
    {
        var evt = new JsonObject { ["type"] = eventType, ["delta"] = deltaText };
        if (outputIndex >= 0)
        {
            evt["output_index"] = outputIndex;
        }
        if (contentIndex >= 0)
        {
            evt["content_index"] = contentIndex;
        }
        if (itemId is not null)
        {
            evt["item_id"] = itemId;
        }

        return $"event: {eventType}\ndata: {evt.ToJsonString()}\n\n";
    }

    private static void CopyIfPresent(JsonObject source, JsonObject target, string propertyName)
    {
        if (source.TryGetPropertyValue(propertyName, out var value) && value is not null)
        {
            target[propertyName] = value.DeepClone();
        }
    }
}
