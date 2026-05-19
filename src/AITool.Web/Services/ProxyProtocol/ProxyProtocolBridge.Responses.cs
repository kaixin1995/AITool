using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Web.Services;

/// <summary>
/// Responses 协议转换所需的流式转换状态。
/// </summary>
public sealed class ChatToResponsesStreamState
{
    /// <summary>
    /// 是否已发送 response.created 事件。
    /// </summary>
    public bool ResponseStarted { get; set; }
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
    /// 已累积的输出文本。
    /// </summary>
    public string OutputText { get; set; } = string.Empty;
    /// <summary>
    /// 下一个可分配的工具调用索引。
    /// </summary>
    public int ToolCallIndex { get; set; } = 1;
    /// <summary>
    /// 已发送过的工具调用标识列表。
    /// </summary>
    public List<string> SentToolCallIds { get; } = [];
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

        // max_output_tokens → max_tokens
        if (root.TryGetPropertyValue("max_output_tokens", out var maxTokens) && maxTokens is not null)
        {
            payload["max_tokens"] = maxTokens.DeepClone();
        }

        // reasoning.effort → reasoning_effort
        if (root.TryGetPropertyValue("reasoning", out var reasoningNode) && reasoningNode is JsonObject reasoning)
        {
            if (reasoning.TryGetPropertyValue("effort", out var effort) && effort is not null)
            {
                payload["reasoning_effort"] = effort.DeepClone();
            }
        }

        // tools → 转换扁平结构为 Chat Completions 嵌套结构
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
                if (toolType == "function")
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
                else
                {
                    chatTools.Add(toolObj.DeepClone());
                }
            }

            payload["tools"] = chatTools;
        }

        // tool_choice → 转换格式
        if (root.TryGetPropertyValue("tool_choice", out var toolChoiceNode) && toolChoiceNode is not null)
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
    /// 将 Chat Completions 非流式响应转换为 Responses API 非流式响应。
    /// </summary>
    public static string ConvertChatResponseToResponses(string chatResponseBody)
    {
        var root = JsonNode.Parse(chatResponseBody) as JsonObject;
        if (root is null)
        {
            return chatResponseBody;
        }

        var chatId = root["id"]?.ToString() ?? $"chatcmpl-{Guid.NewGuid():N}";
        var model = root["model"]?.ToString() ?? string.Empty;
        var created = root["created"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outputs = new JsonArray();

        // 解析 choices
        if (root["choices"] is JsonArray choices && choices.Count > 0)
        {
            var choice = choices[0] as JsonObject;
            var message = choice?["message"] as JsonObject;
            if (message is not null)
            {
                // 文本输出
                var contentText = message["content"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(contentText))
                {
                    outputs.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["id"] = $"msg_{Guid.NewGuid():N}",
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = contentText
                            }
                        }
                    });
                }

                // 工具调用输出
                if (message["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var tc in toolCalls)
                    {
                        if (tc is not JsonObject tcObj)
                        {
                            continue;
                        }

                        outputs.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["id"] = $"fc_{Guid.NewGuid():N}",
                            ["status"] = "completed",
                            ["call_id"] = tcObj["id"]?.DeepClone(),
                            ["name"] = tcObj["function"]?["name"]?.DeepClone(),
                            ["arguments"] = tcObj["function"]?["arguments"]?.DeepClone() ?? "{}"
                        });
                    }
                }
            }
        }

        // 构造用量信息
        var chatUsage = root["usage"] as JsonObject;
        var responsesUsage = new JsonObject();
        if (chatUsage is not null)
        {
            responsesUsage["prompt_tokens"] = chatUsage["prompt_tokens"]?.DeepClone() ?? 0;
            responsesUsage["completion_tokens"] = chatUsage["completion_tokens"]?.DeepClone() ?? 0;
            responsesUsage["total_tokens"] = chatUsage["total_tokens"]?.DeepClone() ?? 0;
            var details = new JsonObject();
            if (chatUsage["prompt_tokens_details"] is JsonObject ptd)
            {
                details["cached_tokens"] = ptd["cached_tokens"]?.DeepClone() ?? 0;
            }
            responsesUsage["prompt_tokens_details"] = details;
        }

        var responseId = chatId.StartsWith("resp_") ? chatId : $"resp_{chatId}";
        var result = new JsonObject
        {
            ["id"] = responseId,
            ["object"] = "response",
            ["created_at"] = created,
            ["status"] = "completed",
            ["model"] = model,
            ["output"] = outputs,
            ["usage"] = responsesUsage
        };

        return result.ToJsonString();
    }

    /// <summary>
    /// 将 Anthropic 非流式响应转换为 Responses API 非流式响应。
    /// </summary>
    public static string ConvertAnthropicResponseToResponses(string anthropicBody)
    {
        // 先转成 OpenAI 格式，再转成 Responses 格式
        var openAiBody = BuildOpenAiResponseFromAnthropic(anthropicBody, "", 0, 0, 0);
        return ConvertChatResponseToResponses(openAiBody);
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

            // 提取用量
            if (root.TryGetProperty("usage", out var usageEl))
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

            // 首次发送 response.created + response.in_progress
            if (!state.ResponseStarted)
            {
                state.ResponseStarted = true;
                state.ResponseId = root.TryGetProperty("id", out var idEl)
                    ? (idEl.GetString()?.StartsWith("resp_") == true ? idEl.GetString() : $"resp_{idEl.GetString()}")
                    : $"resp_{Guid.NewGuid():N}";
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
                    state.OutputText += deltaText;
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

                    if (!string.IsNullOrEmpty(callId) && !state.SentToolCallIds.Contains(callId))
                    {
                        state.SentToolCallIds.Add(callId);
                        state.ToolCallIndex = idx + 1;

                        builder.Append(BuildResponsesEvent("response.output_item.added", new JsonObject
                        {
                            ["type"] = "function_call",
                            ["id"] = $"fc_{Guid.NewGuid():N}",
                            ["status"] = "in_progress",
                            ["call_id"] = callId,
                            ["name"] = funcName,
                            ["arguments"] = ""
                        }, outputIndex: idx));
                    }

                    if (!string.IsNullOrEmpty(funcArgs))
                    {
                        builder.Append(BuildResponsesEvent("response.function_call_arguments.delta",
                            funcArgs, outputIndex: idx, itemId: callId));
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
                    builder.Append(BuildResponsesEvent("response.output_text.done",
                        state.OutputText, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.content_part.done", new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = state.OutputText
                    }, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "message",
                        ["id"] = state.MessageId,
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "output_text", ["text"] = state.OutputText }
                        }
                    }, outputIndex: 0));
                }

                // 关闭工具调用输出项
                for (var i = 0; i < state.SentToolCallIds.Count; i++)
                {
                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["call_id"] = state.SentToolCallIds[i]
                    }, outputIndex: state.MessageAdded ? i + 1 : i));
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

            // 首次收到任何事件 → 发送 response.created + response.in_progress
            if (!state.ResponseStarted)
            {
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

            // message_start → 提取用量和模型
            if (string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase))
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
            if (string.Equals(eventName, "content_block_start", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("content_block", out var block))
                {
                    var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;

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
                        var callId = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        var idx = state.SentToolCallIds.Count + (state.MessageAdded ? 1 : 0);

                        if (!string.IsNullOrEmpty(callId))
                        {
                            state.SentToolCallIds.Add(callId);
                        }

                        builder.Append(BuildResponsesEvent("response.output_item.added", new JsonObject
                        {
                            ["type"] = "function_call",
                            ["id"] = $"fc_{Guid.NewGuid():N}",
                            ["status"] = "in_progress",
                            ["call_id"] = callId,
                            ["name"] = name,
                            ["arguments"] = ""
                        }, outputIndex: idx));
                    }
                }
            }

            // content_block_delta → 文本/推理/工具参数增量
            if (string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("delta", out var delta))
                {
                    var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                    var deltaText = delta.TryGetProperty("text", out var dtEl) ? dtEl.GetString() ?? "" : "";
                    var partialJson = delta.TryGetProperty("partial_json", out var pjEl) ? pjEl.GetString() ?? "" : "";

                    if (deltaType == "text_delta" && !string.IsNullOrEmpty(deltaText))
                    {
                        EnsureMessageStarted(state, builder);
                        builder.Append(BuildResponsesEvent("response.output_text.delta",
                            deltaText, outputIndex: 0, contentIndex: 0));
                        state.OutputText += deltaText;
                    }
                    else if (deltaType == "thinking_delta" && !string.IsNullOrEmpty(deltaText))
                    {
                        builder.Append(BuildResponsesEvent("response.reasoning_summary_text.delta", deltaText));
                    }
                    else if (deltaType == "input_json_delta" && !string.IsNullOrEmpty(partialJson))
                    {
                        var tcIdx = state.SentToolCallIds.Count > 0
                            ? state.SentToolCallIds.Count - 1 + (state.MessageAdded ? 1 : 0)
                            : state.MessageAdded ? 1 : 0;
                        var itemId = state.SentToolCallIds.Count > 0
                            ? state.SentToolCallIds[^1]
                            : "";
                        builder.Append(BuildResponsesEvent("response.function_call_arguments.delta",
                            partialJson, outputIndex: tcIdx, itemId: itemId));
                    }
                }
            }

            // message_delta → 提取用量和停止原因
            if (string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("usage", out var usageEl))
                {
                    var outTokens = usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                    state.Usage = (state.Usage.InputTokens, state.Usage.CachedTokens, outTokens);
                }
            }

            // message_stop → 完成整个响应
            if (string.Equals(eventName, "message_stop", StringComparison.OrdinalIgnoreCase))
            {
                if (state.MessageAdded)
                {
                    builder.Append(BuildResponsesEvent("response.output_text.done",
                        state.OutputText, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.content_part.done", new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = state.OutputText
                    }, outputIndex: 0, contentIndex: 0));

                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "message",
                        ["id"] = state.MessageId,
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "output_text", ["text"] = state.OutputText }
                        }
                    }, outputIndex: 0));
                }

                for (var i = 0; i < state.SentToolCallIds.Count; i++)
                {
                    builder.Append(BuildResponsesEvent("response.output_item.done", new JsonObject
                    {
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["call_id"] = state.SentToolCallIds[i]
                    }, outputIndex: state.MessageAdded ? i + 1 : i));
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
        }

        return builder.ToString();
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
                && reasoning.TryGetProperty("effort", out var effort))
            {
                return effort.GetString() ?? string.Empty;
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
    private static JsonNode? ConvertResponsesToolChoiceToChat(JsonNode toolChoice)
    {
        if (toolChoice is JsonValue jv && jv.TryGetValue(out string? sv))
        {
            return sv;
        }

        if (toolChoice is JsonObject obj)
        {
            var typeVal = obj["type"]?.ToString() ?? "";
            if (typeVal == "function" && obj.ContainsKey("name"))
            {
                return new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = obj["name"]?.DeepClone() }
                };
            }
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
