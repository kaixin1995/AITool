using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Web.Services;

/// <summary>
/// 负责在 OpenAI 与 Anthropic 协议之间转换请求和响应内容。
/// </summary>
public static class ProxyProtocolBridge
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
        bool enableStreaming)
    {
        if (string.Equals(clientProtocol, targetProtocol, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(clientProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceOpenAiModelAndEnsureStreamUsage(requestBody, targetModelName, enableStreaming);
            }

            return ReplaceModelName(requestBody, targetModelName);
        }

        var rootNode = JsonNode.Parse(requestBody) as JsonObject;
        if (rootNode is null)
        {
            return requestBody;
        }

        return string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            ? BuildOpenAiRequestFromAnthropic(rootNode, targetModelName, enableStreaming)
            : BuildAnthropicRequestFromOpenAi(rootNode, targetModelName, enableStreaming);
    }

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

    /// <summary>
    /// 构造 Anthropic 流式响应的起始事件。
    /// </summary>
    public static string BuildAnthropicStreamStart(string modelName, AnthropicOpenAiStreamState state)
    {
        var builder = new StringBuilder();
        AppendSseEvent(builder, "message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = state.MessageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = modelName,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 0,
                    ["cache_creation_input_tokens"] = 0,
                    ["cache_read_input_tokens"] = 0,
                    ["output_tokens"] = 0
                },
                ["content"] = new JsonArray()
            }
        });
        return builder.ToString();
    }

    /// <summary>
    /// 将单个 OpenAI 流式数据块转换为 Anthropic 事件片段。
    /// </summary>
    public static string ConvertOpenAiStreamChunkToAnthropic(string jsonText, AnthropicOpenAiStreamState state)
    {
        var builder = new StringBuilder();

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;

            if (root.TryGetProperty("usage", out var usage))
            {
                var extracted = ExtractUsageFromElement(usage, "OpenAI");
                if (extracted.InputTokens > 0)
                {
                    state.InputTokens = extracted.InputTokens;
                }

                if (extracted.CachedTokens >= 0)
                {
                    state.CachedTokens = extracted.CachedTokens;
                }

                if (extracted.OutputTokens > 0)
                {
                    state.OutputTokens = extracted.OutputTokens;
                }

                // 提取 cache_creation_tokens
                if (usage.TryGetProperty("prompt_tokens_details", out var ptd)
                    && ptd.TryGetProperty("cached_creation_tokens", out var cct))
                {
                    state.CacheCreationTokens = cct.GetInt32();
                }
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return builder.ToString();
            }

            foreach (var choice in EnumerateOpenAiChoices(root))
            {
                if (choice.TryGetProperty("finish_reason", out var finishReason) && finishReason.ValueKind == JsonValueKind.String)
                {
                    state.StopReason = MapOpenAiFinishReason(finishReason.GetString());
                }

                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                var reasoningText = ExtractReasoningFromElement(delta);
                if (reasoningText.Length > 0)
                {
                    state.HadAnyContent = true;
                    if (state.ThinkingIndex < 0)
                    {
                        state.ThinkingIndex = state.NextContentIndex++;
                        AppendSseEvent(builder, "content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start",
                            ["index"] = state.ThinkingIndex,
                            ["content_block"] = new JsonObject
                            {
                                ["type"] = "thinking",
                                ["thinking"] = ""
                            }
                        });
                    }

                    AppendSseEvent(builder, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = state.ThinkingIndex,
                        ["delta"] = new JsonObject
                        {
                            ["type"] = "thinking_delta",
                            ["thinking"] = reasoningText
                        }
                    });
                }

                var toolCallDeltas = GetToolCallDeltas(choice);
                if (toolCallDeltas.Count > 0)
                {
                    state.HadAnyContent = true;
                    CloseThinkingBlockIfNeeded(builder, state);
                    CloseTextBlockIfNeeded(builder, state);
                    foreach (var toolCallDelta in toolCallDeltas)
                    {
                        AppendToolCallDelta(builder, state, toolCallDelta);
                    }
                }

                var contentText = ExtractDeltaContent(delta);
                if (contentText is null)
                {
                    continue;
                }

                state.HadAnyContent = true;
                if (state.ThinkingIndex >= 0 && !state.ThinkingClosed)
                {
                    AppendSseEvent(builder, "content_block_stop", new JsonObject
                    {
                        ["type"] = "content_block_stop",
                        ["index"] = state.ThinkingIndex
                    });
                    state.ThinkingClosed = true;
                }

                if (state.TextIndex < 0)
                {
                    state.TextIndex = state.NextContentIndex++;
                    AppendSseEvent(builder, "content_block_start", new JsonObject
                    {
                        ["type"] = "content_block_start",
                        ["index"] = state.TextIndex,
                        ["content_block"] = new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = ""
                        }
                    });
                }

                AppendSseEvent(builder, "content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = state.TextIndex,
                    ["delta"] = new JsonObject
                    {
                        ["type"] = "text_delta",
                        ["text"] = contentText
                    }
                });
            }
        }
        catch
        {
        }

        return builder.ToString();
    }

    /// <summary>
    /// 为 Anthropic 流式响应补齐剩余结束事件。
    /// </summary>
    public static string CompleteAnthropicStream(AnthropicOpenAiStreamState state)
    {
        var builder = new StringBuilder();

        if (state.ThinkingIndex >= 0 && !state.ThinkingClosed)
        {
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = state.ThinkingIndex
            });
            state.ThinkingClosed = true;
        }

        if (state.TextIndex >= 0 && !state.TextClosed)
        {
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = state.TextIndex
            });
            state.TextClosed = true;
        }

        CloseToolCallBlocks(builder, state);

        AppendSseEvent(builder, "message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = state.InputTokens,
                ["cache_creation_input_tokens"] = state.CacheCreationTokens,
                ["cache_read_input_tokens"] = state.CachedTokens,
                ["output_tokens"] = state.OutputTokens
            },
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = state.StopReason
            }
        });
        AppendSseEvent(builder, "message_stop", new JsonObject
        {
            ["type"] = "message_stop"
        });
        return builder.ToString();
    }

    /// <summary>
    /// 将完整的 OpenAI 普通响应重建为 Anthropic 事件流。
    /// </summary>
    public static string BuildAnthropicStreamFromOpenAiResponse(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        string? upstreamId = null;
        string contentText = string.Empty;
        string reasoningText = string.Empty;
        string finishReason = "stop";
        int cacheCreation = 0;
        int upstreamInput = 0;
        int upstreamOutput = 0;
        JsonArray? toolUseBlocks = null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idEl))
            {
                upstreamId = idEl.GetString();
            }

            if (root.TryGetProperty("usage", out var usageEl))
            {
                if (usageEl.TryGetProperty("prompt_tokens", out var pt))
                {
                    upstreamInput = pt.GetInt32();
                }

                if (usageEl.TryGetProperty("completion_tokens", out var ct))
                {
                    upstreamOutput = ct.GetInt32();
                }

                if (usageEl.TryGetProperty("prompt_tokens_details", out var ptd))
                {
                    if (ptd.TryGetProperty("cached_tokens", out var cachedEl))
                    {
                        cachedTokens = cachedEl.GetInt32();
                    }

                    if (ptd.TryGetProperty("cached_creation_tokens", out var cctEl))
                    {
                        cacheCreation = cctEl.GetInt32();
                    }
                }
            }

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var selectedChoice = GetPreferredOpenAiChoice(choices);
                if (selectedChoice is { } choice)
                {
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    {
                        finishReason = fr.GetString() ?? "stop";
                    }

                    if (choice.TryGetProperty("message", out var message))
                    {
                        reasoningText = ExtractReasoningFromElement(message);
                        contentText = ExtractContentFromMessage(message);

                        if (message.TryGetProperty("tool_calls", out var toolCalls)
                            && toolCalls.ValueKind == JsonValueKind.Array)
                        {
                            toolUseBlocks = new JsonArray();
                            foreach (var tc in toolCalls.EnumerateArray())
                            {
                                var tcId = tc.TryGetProperty("id", out var tcIdEl) ? tcIdEl.GetString() ?? string.Empty : string.Empty;
                                var tcName = string.Empty;
                                var tcArgs = "{}";
                                if (tc.TryGetProperty("function", out var funcEl))
                                {
                                    if (funcEl.TryGetProperty("name", out var nEl))
                                    {
                                        tcName = nEl.GetString() ?? string.Empty;
                                    }

                                    if (funcEl.TryGetProperty("arguments", out var aEl))
                                    {
                                        tcArgs = aEl.GetString() ?? "{}";
                                    }
                                }

                                JsonNode? tcInput;
                                try
                                {
                                    tcInput = JsonNode.Parse(tcArgs);
                                }
                                catch
                                {
                                    tcInput = tcArgs;
                                }

                                toolUseBlocks.Add(new JsonObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = tcId,
                                    ["name"] = tcName,
                                    ["input"] = tcInput
                                });
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            return string.Empty;
        }

        var stopReason = MapOpenAiFinishReason(finishReason);
        var effectiveInput = upstreamInput > 0 ? upstreamInput : inputTokens;
        var effectiveOutput = upstreamOutput > 0 ? upstreamOutput : outputTokens;
        var directInputTokens = Math.Max(effectiveInput - cachedTokens - cacheCreation, 0);

        var builder = new StringBuilder();
        AppendSseEvent(builder, "message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = upstreamId ?? $"msg_{Guid.NewGuid():N}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = modelName,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = directInputTokens,
                    ["cache_creation_input_tokens"] = cacheCreation,
                    ["cache_read_input_tokens"] = cachedTokens,
                    ["output_tokens"] = 0
                },
                ["content"] = new JsonArray()
            }
        });

        var contentIndex = 0;
        if (!string.IsNullOrEmpty(reasoningText))
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = string.Empty
                }
            });
            AppendSseEvent(builder, "content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = contentIndex,
                ["delta"] = new JsonObject
                {
                    ["type"] = "thinking_delta",
                    ["thinking"] = reasoningText
                }
            });
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = contentIndex
            });
            contentIndex++;
        }

        if (!string.IsNullOrEmpty(contentText))
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = string.Empty
                }
            });
            AppendSseEvent(builder, "content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = contentIndex,
                ["delta"] = new JsonObject
                {
                    ["type"] = "text_delta",
                    ["text"] = contentText
                }
            });
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = contentIndex
            });
            contentIndex++;
        }

        if (toolUseBlocks is not null)
        {
            foreach (var block in toolUseBlocks)
            {
                if (block is not JsonObject toolBlock)
                {
                    continue;
                }

                AppendSseEvent(builder, "content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = contentIndex,
                    ["content_block"] = new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolBlock["id"]?.DeepClone(),
                        ["name"] = toolBlock["name"]?.DeepClone(),
                        ["input"] = new JsonObject()
                    }
                });

                var partialJson = toolBlock["input"]?.ToJsonString() ?? "{}";
                if (!string.IsNullOrEmpty(partialJson))
                {
                    AppendSseEvent(builder, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = contentIndex,
                        ["delta"] = new JsonObject
                        {
                            ["type"] = "input_json_delta",
                            ["partial_json"] = partialJson
                        }
                    });
                }

                AppendSseEvent(builder, "content_block_stop", new JsonObject
                {
                    ["type"] = "content_block_stop",
                    ["index"] = contentIndex
                });
                contentIndex++;
            }
        }

        AppendSseEvent(builder, "message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = effectiveInput,
                ["cache_creation_input_tokens"] = cacheCreation,
                ["cache_read_input_tokens"] = cachedTokens,
                ["output_tokens"] = effectiveOutput
            },
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = stopReason
            }
        });
        AppendSseEvent(builder, "message_stop", new JsonObject
        {
            ["type"] = "message_stop"
        });
        return builder.ToString();
    }

    /// <summary>
    /// 将 Anthropic 请求体转换为 OpenAI 请求格式。
    /// </summary>
    private static string BuildOpenAiRequestFromAnthropic(JsonObject rootNode, string targetModelName, bool enableStreaming)
    {
        var messages = new JsonArray();

        // 将 Anthropic system 字段提取为 OpenAI system 消息
        var systemNode = rootNode["system"];
        if (systemNode is not null)
        {
            var systemText = ExtractSystemContent(systemNode);
            if (!string.IsNullOrWhiteSpace(systemText))
            {
                messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });
            }
        }

        // 转换 messages，处理 content blocks、tool_use、tool_result、多模态等
        if (rootNode["messages"] is JsonArray srcMessages)
        {
            foreach (var msg in srcMessages)
            {
                if (msg is not JsonObject msgObj)
                {
                    continue;
                }

                var role = msgObj["role"]?.GetValue<string>() ?? "user";
                var content = msgObj["content"];
                var toolCalls = msgObj["tool_calls"];

                // Anthropic assistant 消息可能含 tool_use blocks
                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var openAiMsg = new JsonObject { ["role"] = "assistant" };
                    var (textContent, toolUseBlocks, imageBlocks) = ParseAnthropicContentBlocks(content);

                    if (textContent is not null)
                    {
                        openAiMsg["content"] = textContent;
                    }

                    // 将多模态图片转为 OpenAI 格式
                    if (imageBlocks.Count > 0)
                    {
                        var mediaArray = new JsonArray();
                        if (textContent is not null)
                        {
                            mediaArray.Add(new JsonObject { ["type"] = "text", ["text"] = textContent });
                        }

                        foreach (var img in imageBlocks)
                        {
                            mediaArray.Add(img);
                        }

                        openAiMsg["content"] = mediaArray;
                    }

                    // tool_use 转为 OpenAI tool_calls
                    if (toolUseBlocks.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var toolUse in toolUseBlocks)
                        {
                            calls.Add(toolUse?.DeepClone());
                        }

                        openAiMsg["tool_calls"] = calls;
                        if (openAiMsg["content"] is null)
                        {
                            openAiMsg["content"] = null;
                        }
                    }

                    messages.Add(openAiMsg);
                    continue;
                }

                // Anthropic user 消息中可能含 tool_result blocks，需要拆分为独立 tool 消息
                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    var (userText, _, toolResults, userImageBlocks) = ParseAnthropicUserContent(content);

                    // 先添加 tool_result 作为独立的 tool 角色消息
                    foreach (var toolResult in toolResults)
                    {
                        messages.Add(toolResult);
                    }

                    // 再添加用户内容
                    if (userText is not null || userImageBlocks.Count > 0)
                    {
                        var userMsg = new JsonObject { ["role"] = "user" };
                        if (userImageBlocks.Count > 0)
                        {
                            var parts = new JsonArray();
                            if (userText is not null)
                            {
                                parts.Add(new JsonObject { ["type"] = "text", ["text"] = userText });
                            }

                            foreach (var img in userImageBlocks)
                            {
                                parts.Add(img);
                            }

                            userMsg["content"] = parts;
                        }
                        else
                        {
                            userMsg["content"] = userText ?? "...";
                        }

                        messages.Add(userMsg);
                    }

                    continue;
                }

                // 其他角色（system 已处理）直接复制
                var copyMsg = new JsonObject { ["role"] = role };
                if (content is not null)
                {
                    copyMsg["content"] = content.DeepClone();
                }

                messages.Add(copyMsg);
            }
        }

        var payload = new JsonObject
        {
            ["model"] = targetModelName,
            ["messages"] = messages,
            ["stream"] = enableStreaming
        };

        // max_tokens / max_completion_tokens
        var maxTokens = rootNode["max_tokens"]?.GetValue<uint>() ?? 0;
        if (maxTokens > 0)
        {
            payload["max_tokens"] = maxTokens;
        }

        if (enableStreaming)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        CopyNodeIfPresent(rootNode, payload, "temperature");
        CopyNodeIfPresent(rootNode, payload, "top_p");
        CopyNodeIfPresent(rootNode, payload, "metadata");

        // tools 格式转换：Anthropic tools → OpenAI function tools
        ConvertAnthropicToolsToOpenAi(rootNode, payload);

        // tool_choice 格式转换：Anthropic → OpenAI
        ConvertAnthropicToolChoiceToOpenAi(rootNode, payload);

        // stop_sequences → stop
        if (rootNode["stop_sequences"] is not null)
        {
            payload["stop"] = rootNode["stop_sequences"]!.DeepClone();
        }

        // thinking → reasoning_effort 分级映射
        if (rootNode["thinking"] is JsonObject thinkingObj)
        {
            var budgetTokens = thinkingObj["budget_tokens"]?.GetValue<int>() ?? 0;
            payload["reasoning_effort"] = budgetTokens switch
            {
                <= 1280 => "low",
                <= 2048 => "medium",
                _ => "high"
            };
        }

        return payload.ToJsonString();
    }

    /// <summary>
    /// 将 OpenAI 请求体转换为 Anthropic 请求格式。
    /// </summary>
    private static string BuildAnthropicRequestFromOpenAi(JsonObject rootNode, string targetModelName, bool enableStreaming)
    {
        var claudeMessages = new JsonArray();
        var systemParts = new List<string>();

        // 处理 OpenAI messages：提取 system、规范化消息、处理 tool 消息、多模态转换
        if (rootNode["messages"] is JsonArray srcMessages)
        {
            OpenAiMessageAccumulator? lastAccumulator = null;

            foreach (var msg in srcMessages)
            {
                if (msg is not JsonObject msgObj)
                {
                    continue;
                }

                var role = msgObj["role"]?.GetValue<string>() ?? "user";
                var content = msgObj["content"];

                // system 消息提取到 system 字段
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    var sysText = ExtractOpenAiContentAsString(content);
                    if (!string.IsNullOrWhiteSpace(sysText))
                    {
                        systemParts.Add(sysText);
                    }

                    lastAccumulator = null;
                    continue;
                }

                // tool 消息：转为 Anthropic tool_result block 并合并到前一条 user 消息
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    var toolCallId = msgObj["tool_call_id"]?.GetValue<string>() ?? "";
                    var toolContent = SerializeOpenAiToolContent(content);

                    var toolResultBlock = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolCallId,
                        ["content"] = toolContent
                    };

                    // 合并到前一条 user 消息的 content blocks 中
                    if (lastAccumulator is { Role: "user" })
                    {
                        lastAccumulator.Blocks.Add(toolResultBlock);
                    }
                    else
                    {
                        claudeMessages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray { toolResultBlock }
                        });
                        lastAccumulator = null;
                    }

                    continue;
                }

                // assistant 消息：处理 tool_calls 和 reasoning_content
                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var accumulator = new OpenAiMessageAccumulator { Role = "assistant" };

                    // reasoning_content 转为 thinking block
                    var reasoning = msgObj["reasoning_content"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(reasoning))
                    {
                        accumulator.Blocks.Add(new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = reasoning!
                        });
                    }

                    // 普通文本和图片内容
                    var (textContent, imageBlocks) = ParseOpenAiContentToClaudeBlocks(content);
                    if (textContent is not null)
                    {
                        accumulator.Blocks.Add(new JsonObject { ["type"] = "text", ["text"] = textContent });
                    }

                    foreach (var img in imageBlocks)
                    {
                        accumulator.Blocks.Add(img as JsonObject ?? new JsonObject { ["type"] = "text", ["text"] = img?.ToJsonString() ?? "" });
                    }

                    // tool_calls 转为 tool_use blocks
                    if (msgObj["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (var tc in toolCalls)
                        {
                            if (tc is not JsonObject tcObj)
                            {
                                continue;
                            }

                            var tcId = tcObj["id"]?.GetValue<string>() ?? $"toolu_{Guid.NewGuid():N}";
                            var tcName = tcObj["function"]?["name"]?.GetValue<string>() ?? "";
                            var tcArgsStr = tcObj["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                            JsonNode? tcInput;
                            try
                            {
                                tcInput = JsonNode.Parse(tcArgsStr);
                            }
                            catch
                            {
                                tcInput = tcArgsStr;
                            }

                            accumulator.Blocks.Add(new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = tcId,
                                ["name"] = tcName,
                                ["input"] = tcInput
                            });
                        }
                    }

                    AddAccumulatorToMessages(claudeMessages, accumulator, ref lastAccumulator);
                    continue;
                }

                // user 消息：处理文本和图片
                {
                    var accumulator = new OpenAiMessageAccumulator { Role = "user" };
                    var (userText, userImageBlocks) = ParseOpenAiContentToClaudeBlocks(content);
                    if (userText is not null)
                    {
                        accumulator.Blocks.Add(new JsonObject { ["type"] = "text", ["text"] = userText });
                    }

                    foreach (var img in userImageBlocks)
                    {
                        accumulator.Blocks.Add(img as JsonObject ?? new JsonObject { ["type"] = "text", ["text"] = img?.ToJsonString() ?? "" });
                    }

                    if (accumulator.Blocks.Count == 0)
                    {
                        accumulator.Blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "..." });
                    }

                    AddAccumulatorToMessages(claudeMessages, accumulator, ref lastAccumulator);
                }
            }
        }

        // 确保第一条非 system 消息是 user 角色
        if (claudeMessages.Count > 0)
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
                    newMessages.Add(m);
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

        // max_completion_tokens 优先于 max_tokens
        var maxCompletionTokens = rootNode["max_completion_tokens"]?.GetValue<uint>() ?? 0;
        var maxTokens = rootNode["max_tokens"]?.GetValue<uint>() ?? 0;
        var effectiveMax = maxCompletionTokens > 0 ? maxCompletionTokens : maxTokens;
        if (effectiveMax > 0)
        {
            payload["max_tokens"] = effectiveMax;
        }

        // system 字段
        if (systemParts.Count > 0)
        {
            payload["system"] = systemParts.Count == 1
                ? systemParts[0]
                : new JsonArray(systemParts.Select(p => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = p }).ToArray());
        }

        CopyNodeIfPresent(rootNode, payload, "temperature");
        CopyNodeIfPresent(rootNode, payload, "top_p");
        CopyNodeIfPresent(rootNode, payload, "metadata");

        // tools 格式转换：OpenAI function tools → Anthropic tools
        ConvertOpenAiToolsToAnthropic(rootNode, payload);

        // tool_choice + parallel_tool_calls 格式转换
        ConvertOpenAiToolChoiceToAnthropic(rootNode, payload);

        // stop → stop_sequences
        if (rootNode["stop"] is not null)
        {
            payload["stop_sequences"] = rootNode["stop"]!.DeepClone();
        }

        // reasoning_effort 分级映射到 thinking
        if (rootNode["reasoning_effort"] is JsonNode reasoningEffort)
        {
            var effort = reasoningEffort.GetValue<string>().Trim().ToLowerInvariant();
            payload["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = effort switch
                {
                    "low" => 1280,
                    "medium" => 2048,
                    _ => 4096
                }
            };
        }

        // web_search_options → Anthropic web_search 工具
        ConvertOpenAiWebSearchToAnthropic(rootNode, payload);

        return payload.ToJsonString();
    }

    /// <summary>
    /// 将 OpenAI 普通响应转换为 Anthropic 消息格式。
    /// </summary>
    private static string BuildAnthropicResponseFromOpenAi(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        string? upstreamId = null;
        string contentText = "";
        string reasoningText = "";
        string finishReason = "stop";
        int cacheCreation = 0;
        int upstreamInput = 0;
        int upstreamOutput = 0;
        JsonArray? toolUseBlocks = null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idEl))
                upstreamId = idEl.GetString();

            if (root.TryGetProperty("usage", out var usageEl))
            {
                if (usageEl.TryGetProperty("prompt_tokens", out var pt))
                    upstreamInput = pt.GetInt32();
                if (usageEl.TryGetProperty("completion_tokens", out var ct))
                    upstreamOutput = ct.GetInt32();
                if (usageEl.TryGetProperty("prompt_tokens_details", out var ptd)
                    && ptd.TryGetProperty("cached_tokens", out var cachedEl))
                    cachedTokens = cachedEl.GetInt32();
                if (ptd.TryGetProperty("cached_creation_tokens", out var cctEl))
                    cacheCreation = cctEl.GetInt32();
            }

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var selectedChoice = GetPreferredOpenAiChoice(choices);
                if (selectedChoice is { } choice)
                {
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finishReason = fr.GetString() ?? "stop";

                    if (choice.TryGetProperty("message", out var message))
                    {
                        reasoningText = ExtractReasoningFromElement(message);
                        contentText = ExtractContentFromMessage(message);

                        // 提取 tool_calls
                        if (message.TryGetProperty("tool_calls", out var toolCalls)
                            && toolCalls.ValueKind == JsonValueKind.Array)
                        {
                            toolUseBlocks = new JsonArray();
                            foreach (var tc in toolCalls.EnumerateArray())
                            {
                                var tcId = tc.TryGetProperty("id", out var tcIdEl) ? tcIdEl.GetString() ?? "" : "";
                                var tcName = "";
                                var tcArgs = "{}";
                                if (tc.TryGetProperty("function", out var funcEl))
                                {
                                    if (funcEl.TryGetProperty("name", out var nEl)) tcName = nEl.GetString() ?? "";
                                    if (funcEl.TryGetProperty("arguments", out var aEl)) tcArgs = aEl.GetString() ?? "{}";
                                }
                                JsonNode? tcInput;
                                try { tcInput = JsonNode.Parse(tcArgs); } catch { tcInput = tcArgs; }
                                toolUseBlocks.Add(new JsonObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = tcId,
                                    ["name"] = tcName,
                                    ["input"] = tcInput
                                });
                            }
                        }
                    }
                }
            }
        }
        catch { }

        var stopReason = MapOpenAiFinishReason(finishReason);
        var effectiveInput = upstreamInput > 0 ? upstreamInput : inputTokens;
        var effectiveOutput = upstreamOutput > 0 ? upstreamOutput : outputTokens;

        var contentArray = new JsonArray();
        if (!string.IsNullOrWhiteSpace(reasoningText))
            contentArray.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoningText });

        if (!string.IsNullOrWhiteSpace(contentText))
            contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = contentText });

        if (toolUseBlocks is not null)
        {
            foreach (var b in toolUseBlocks) contentArray.Add(b);
        }

        if (contentArray.Count == 0)
            contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = "" });

        return new JsonObject
        {
            ["id"] = upstreamId ?? $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = modelName,
            ["content"] = contentArray,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = effectiveInput,
                ["cache_creation_input_tokens"] = cacheCreation,
                ["cache_read_input_tokens"] = cachedTokens,
                ["output_tokens"] = effectiveOutput
            }
        }.ToJsonString();
    }

    /// <summary>
    /// 将 Anthropic 普通响应转换为 OpenAI 消息格式。
    /// </summary>
    private static string BuildOpenAiResponseFromAnthropic(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        string? upstreamId = null;
        string? responseText = null;
        var thinkingParts = new List<string>();
        string stopReason = "end_turn";
        int cacheCreation = 0;
        int cacheRead = 0;
        int upstreamInput = 0;
        int upstreamOutput = 0;
        var tools = new JsonArray();

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idEl))
                upstreamId = idEl.GetString();

            if (root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                stopReason = sr.GetString() ?? "end_turn";

            if (root.TryGetProperty("usage", out var usageEl))
            {
                if (usageEl.TryGetProperty("input_tokens", out var it)) upstreamInput = it.GetInt32();
                if (usageEl.TryGetProperty("output_tokens", out var ot)) upstreamOutput = ot.GetInt32();
                if (usageEl.TryGetProperty("cache_read_input_tokens", out var crit)) cacheRead = crit.GetInt32();
                if (usageEl.TryGetProperty("cache_creation_input_tokens", out var ccit)) cacheCreation = ccit.GetInt32();
            }

            if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentArr.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var t) ? t.GetString() : "";
                    switch (type)
                    {
                        case "tool_use":
                            var tcArgs = "{}";
                            if (block.TryGetProperty("input", out var inputEl))
                            {
                                try { tcArgs = inputEl.ValueKind == JsonValueKind.String ? inputEl.GetString() ?? "{}" : inputEl.GetRawText(); } catch { }
                            }
                            tools.Add(new JsonObject
                            {
                                ["id"] = block.TryGetProperty("id", out var bid) ? bid.GetString() ?? "" : "",
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = block.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "",
                                    ["arguments"] = tcArgs
                                }
                            });
                            break;
                        case "thinking":
                            if (block.TryGetProperty("thinking", out var thEl) && thEl.ValueKind == JsonValueKind.String)
                                AppendIfNotEmpty(thinkingParts, thEl.GetString());
                            break;
                        case "text":
                            if (block.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String)
                            {
                                responseText = string.Concat(responseText, txtEl.GetString() ?? string.Empty);
                            }
                            break;
                    }
                }
            }
        }
        catch { }

        var finishReason = MapAnthropicStopReason(stopReason);
        var effectiveInput = upstreamInput > 0 ? upstreamInput : inputTokens;
        var effectiveOutput = upstreamOutput > 0 ? upstreamOutput : outputTokens;
        var totalInput = effectiveInput + cacheRead + cacheCreation;

        var messageObject = new JsonObject
        {
            ["role"] = "assistant"
        };

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            messageObject["content"] = responseText;
        }

        if (tools.Count > 0)
            messageObject["tool_calls"] = tools;

        if (!string.IsNullOrWhiteSpace(string.Join("", thinkingParts)))
            messageObject["reasoning_content"] = string.Join("\n", thinkingParts);

        return new JsonObject
        {
            ["id"] = upstreamId ?? $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = modelName,
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
                ["prompt_tokens"] = totalInput,
                ["prompt_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = cacheRead,
                    ["cached_creation_tokens"] = cacheCreation
                },
                ["completion_tokens"] = effectiveOutput,
                ["total_tokens"] = totalInput + effectiveOutput
            }
        }.ToJsonString();
    }

    /// <summary>
    /// 将 OpenAI 流式响应整体转换为 Anthropic 事件流。
    /// </summary>
    private static string BuildAnthropicStreamingResponseFromOpenAi(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        var (contentText, reasoningText) = ExtractOpenAiStreamingText(responseBody);
        var (finishReason, toolCallBlocks, usageInfo) = ExtractOpenAiStreamingMetadata(responseBody);
        var stopReason = MapOpenAiFinishReason(finishReason);

        int upstreamInput = usageInfo.UpstreamInput > 0 ? usageInfo.UpstreamInput : inputTokens;
        int upstreamOutput = usageInfo.UpstreamOutput > 0 ? usageInfo.UpstreamOutput : outputTokens;
        int cacheCreation = usageInfo.CacheCreation;
        int cacheRead = usageInfo.UpstreamCached > 0 ? usageInfo.UpstreamCached : cachedTokens;

        var builder = new StringBuilder();

        AppendSseEvent(builder, "message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_{Guid.NewGuid():N}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = modelName,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = Math.Max(upstreamInput - cacheRead - cacheCreation, 0),
                    ["cache_creation_input_tokens"] = cacheCreation,
                    ["cache_read_input_tokens"] = cacheRead,
                    ["output_tokens"] = 0
                },
                ["content"] = new JsonArray()
            }
        });

        var contentIndex = 0;
        if (reasoningText.Length > 0)
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "" }
            });
            AppendSseEvent(builder, "content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = contentIndex,
                ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = reasoningText }
            });
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = contentIndex
            });
            contentIndex++;
        }

        if (contentText is not null)
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
            });
            AppendSseEvent(builder, "content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = contentIndex,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = contentText }
            });
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = contentIndex
            });
            contentIndex++;
        }

        // tool_calls 块
        foreach (var tc in toolCallBlocks)
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = tc.Id,
                    ["name"] = tc.Name,
                    ["input"] = new JsonObject()
                }
            });
            if (!string.IsNullOrEmpty(tc.Arguments))
            {
                AppendSseEvent(builder, "content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = contentIndex,
                    ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = tc.Arguments }
                });
            }
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = contentIndex
            });
            contentIndex++;
        }

        AppendSseEvent(builder, "message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = upstreamInput,
                ["cache_creation_input_tokens"] = cacheCreation,
                ["cache_read_input_tokens"] = cacheRead,
                ["output_tokens"] = upstreamOutput
            },
            ["delta"] = new JsonObject { ["stop_reason"] = stopReason }
        });
        AppendSseEvent(builder, "message_stop", new JsonObject { ["type"] = "message_stop" });

        return builder.ToString();
    }

    /// <summary>
    /// 将 Anthropic 流式响应整体转换为 OpenAI 事件流。
    /// </summary>
    private static string BuildOpenAiStreamingResponseFromAnthropic(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        var (contentText, reasoningText) = ExtractAnthropicStreamingText(responseBody);
        var (stopReason, toolCalls, usageInfo) = ExtractAnthropicStreamingMetadata(responseBody);
        var finishReason = MapAnthropicStopReason(stopReason);

        int upstreamInput = usageInfo.UpstreamInput > 0 ? usageInfo.UpstreamInput : inputTokens;
        int upstreamOutput = usageInfo.UpstreamOutput > 0 ? usageInfo.UpstreamOutput : outputTokens;
        int cacheCreation = usageInfo.CacheCreation;
        int cacheRead = usageInfo.UpstreamCached > 0 ? usageInfo.UpstreamCached : cachedTokens;
        var totalInput = upstreamInput + cacheRead + cacheCreation;

        var builder = new StringBuilder();

        // role chunk
        AppendOpenAiChunk(builder, modelName, new JsonObject { ["role"] = "assistant", ["content"] = "" });

        // 思考/推理内容
        if (reasoningText.Length > 0)
        {
            AppendOpenAiChunk(builder, modelName, new JsonObject { ["reasoning_content"] = reasoningText });
        }

        // 文本内容
        if (contentText is not null)
        {
            AppendOpenAiChunk(builder, modelName, new JsonObject { ["content"] = contentText });
        }

        // 工具调用
        foreach (var tc in toolCalls)
        {
            var tcIdx = tc.Index;
            AppendOpenAiChunkWithToolCalls(builder, modelName, new JsonArray
            {
                new JsonObject
                {
                    ["index"] = tcIdx,
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments
                    }
                }
            });
        }

        // 结束 chunk（含用量信息）
        builder.Append("data: ");
        builder.Append(new JsonObject
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
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = finishReason
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = totalInput,
                ["prompt_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = cacheRead,
                    ["cached_creation_tokens"] = cacheCreation
                },
                ["completion_tokens"] = upstreamOutput,
                ["total_tokens"] = totalInput + upstreamOutput
            }
        }.ToJsonString());
        builder.Append("\n\ndata: [DONE]\n\n");
        return builder.ToString();
    }

    /// <summary>
    /// 追加一个不含工具调用的 OpenAI 流式数据块。
    /// </summary>
    private static void AppendOpenAiChunk(StringBuilder builder, string modelName, JsonObject deltaObject)
    {
        builder.Append("data: ");
        builder.Append(new JsonObject
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
                    ["finish_reason"] = null
                }
            }
        }.ToJsonString());
        builder.Append("\n\n");
    }

    /// <summary>
    /// 追加一个包含工具调用增量的 OpenAI 流式数据块。
    /// </summary>
    private static void AppendOpenAiChunkWithToolCalls(StringBuilder builder, string modelName, JsonArray toolCalls)
    {
        builder.Append("data: ");
        builder.Append(new JsonObject
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
                    ["delta"] = new JsonObject { ["tool_calls"] = toolCalls },
                    ["finish_reason"] = null
                }
            }
        }.ToJsonString());
        builder.Append("\n\n");
    }

    /// <summary>
    /// 从 OpenAI 流式响应中提取停止原因、工具调用和用量信息。
    /// </summary>
    private static (string FinishReason, List<StreamingToolCall> ToolCalls, StreamingUsageInfo Usage) ExtractOpenAiStreamingMetadata(string responseBody)
    {
        var finishReason = "stop";
        var toolCalls = new Dictionary<int, StreamingToolCall>();
        var usageInfo = new StreamingUsageInfo();

        foreach (var rawLine in responseBody.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase)) continue;
            var jsonText = line["data: ".Length..];
            if (string.Equals(jsonText, "[DONE]", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                // 用量
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt))
                        usageInfo.UpstreamInput = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct))
                        usageInfo.UpstreamOutput = ct.GetInt32();
                    if (usage.TryGetProperty("prompt_tokens_details", out var ptd))
                    {
                        if (ptd.TryGetProperty("cached_tokens", out var cacht))
                            usageInfo.UpstreamCached = cacht.GetInt32();
                        if (ptd.TryGetProperty("cached_creation_tokens", out var cct))
                            usageInfo.CacheCreation = cct.GetInt32();
                    }
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;

                foreach (var choice in EnumerateOpenAiChoices(root))
                {
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    {
                        var frStr = fr.GetString();
                        if (!string.IsNullOrEmpty(frStr)) finishReason = frStr;
                    }

                    if (!choice.TryGetProperty("delta", out var delta) ||
                        !delta.TryGetProperty("tool_calls", out var tcs) ||
                        tcs.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var idx = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                            ? idxEl.GetInt32() : 0;
                        if (!toolCalls.TryGetValue(idx, out var existing))
                        {
                            existing = new StreamingToolCall { Index = idx };
                            toolCalls[idx] = existing;
                        }
                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            existing.Id = idEl.GetString() ?? "";
                        if (tc.TryGetProperty("function", out var funcEl))
                        {
                            if (funcEl.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                                existing.Name = nEl.GetString() ?? "";
                            if (funcEl.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                                existing.Arguments += aEl.GetString() ?? "";
                        }
                    }
                }
            }
            catch { }
        }

        return (finishReason, toolCalls.Values.OrderBy(x => x.Index).ToList(), usageInfo);
    }

    /// <summary>
    /// 从 Anthropic 流式响应中提取停止原因、工具调用和用量信息。
    /// </summary>
    private static (string StopReason, List<StreamingToolCall> ToolCalls, StreamingUsageInfo Usage) ExtractAnthropicStreamingMetadata(string responseBody)
    {
        var stopReason = "end_turn";
        var toolCalls = new List<StreamingToolCall>();
        var usageInfo = new StreamingUsageInfo();
        var currentEvent = "";
        var dataLines = new List<string>();

        foreach (var rawLine in responseBody.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("event: ", StringComparison.OrdinalIgnoreCase))
            {
                if (dataLines.Count > 0)
                    ProcessAnthropicMetadataBlock(currentEvent, dataLines, ref stopReason, toolCalls, ref usageInfo);
                currentEvent = line["event: ".Length..].Trim();
                dataLines.Clear();
                continue;
            }
            if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data: ".Length..]);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(line) || dataLines.Count == 0) continue;
            ProcessAnthropicMetadataBlock(currentEvent, dataLines, ref stopReason, toolCalls, ref usageInfo);
            currentEvent = "";
            dataLines.Clear();
        }
        if (dataLines.Count > 0)
            ProcessAnthropicMetadataBlock(currentEvent, dataLines, ref stopReason, toolCalls, ref usageInfo);

        return (stopReason, toolCalls, usageInfo);
    }

    /// <summary>
    /// 解析单个 Anthropic SSE 元数据事件并提取停止原因、工具调用和用量信息。
    /// </summary>
    private static void ProcessAnthropicMetadataBlock(string eventName, List<string> dataLines, ref string stopReason, List<StreamingToolCall> toolCalls, ref StreamingUsageInfo usageInfo)
    {
        var data = string.Join("\n", dataLines);
        if (data == "[DONE]") return;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // message_start 中的用量
            if (string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var it)) usageInfo.UpstreamInput = it.GetInt32();
                    if (usage.TryGetProperty("cache_read_input_tokens", out var crit)) usageInfo.UpstreamCached = crit.GetInt32();
                    if (usage.TryGetProperty("cache_creation_input_tokens", out var ccit)) usageInfo.CacheCreation = ccit.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var ot)) usageInfo.UpstreamOutput = ot.GetInt32();
                }
            }

            // message_delta 中的 stop_reason 和用量
            if (string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                    stopReason = sr.GetString() ?? "end_turn";
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("output_tokens", out var ot)) usageInfo.UpstreamOutput = ot.GetInt32();
                    if (usage.TryGetProperty("input_tokens", out var it)) usageInfo.UpstreamInput = it.GetInt32();
                    if (usage.TryGetProperty("cache_read_input_tokens", out var crit)) usageInfo.UpstreamCached = crit.GetInt32();
                    if (usage.TryGetProperty("cache_creation_input_tokens", out var ccit)) usageInfo.CacheCreation = ccit.GetInt32();
                }
            }

            // content_block_start 中的 tool_use
            if (string.Equals(eventName, "content_block_start", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("content_block", out var cb))
            {
                var cbType = cb.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (cbType == "tool_use")
                {
                    var idx = root.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : toolCalls.Count;
                    toolCalls.Add(new StreamingToolCall
                    {
                        Index = idx,
                        Id = cb.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                        Name = cb.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : ""
                    });
                }
            }

            // content_block_delta 中的 input_json_delta
            if (string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("delta", out var delta2))
            {
                var deltaType = delta2.TryGetProperty("type", out var dt) ? dt.GetString() : "";
                if (deltaType == "input_json_delta")
                {
                    var idx = root.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : -1;
                    var partialJson = delta2.TryGetProperty("partial_json", out var pj) ? pj.GetString() ?? "" : "";
                    var tc = toolCalls.LastOrDefault(t => t.Index == idx);
                    if (tc is not null) tc.Arguments += partialJson;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 保存流式工具调用在重组过程中的索引、标识和参数片段。
    /// </summary>
    private sealed class StreamingToolCall
    {
        /// <summary>
        /// 工具调用在流式 choices 中的索引。
        /// </summary>
        public int Index { get; set; }
        /// <summary>
        /// 工具调用标识。
        /// </summary>
        public string Id { get; set; } = "";
        /// <summary>
        /// 工具名称。
        /// </summary>
        public string Name { get; set; } = "";
        /// <summary>
        /// 累积后的工具参数文本。
        /// </summary>
        public string Arguments { get; set; } = "";
    }

    /// <summary>
    /// 保存流式响应中提取出的上游 token 用量信息。
    /// </summary>
    private sealed class StreamingUsageInfo
    {
        /// <summary>
        /// 上游返回的输入 token 数。
        /// </summary>
        public int UpstreamInput { get; set; }
        /// <summary>
        /// 上游返回的输出 token 数。
        /// </summary>
        public int UpstreamOutput { get; set; }
        /// <summary>
        /// 上游返回的缓存命中 token 数。
        /// </summary>
        public int UpstreamCached { get; set; }
        /// <summary>
        /// 上游返回的缓存写入 token 数。
        /// </summary>
        public int CacheCreation { get; set; }
    }

    /// <summary>
    /// 在 Anthropic 流式响应缺少结束事件时补齐 message_delta 和 message_stop。
    /// </summary>
    public static string EnsureAnthropicStreamClosed(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        var normalized = responseBody.Replace("\r\n", "\n");
        if (normalized.Contains("event: message_stop\n", StringComparison.OrdinalIgnoreCase))
        {
            return responseBody;
        }

        var builder = new StringBuilder(responseBody);
        if (builder.Length > 0 && !responseBody.EndsWith("\n\n", StringComparison.Ordinal))
        {
            if (!responseBody.EndsWith("\n", StringComparison.Ordinal))
            {
                builder.Append('\n');
            }

            builder.Append('\n');
        }

        AppendSseEvent(builder, "message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["cache_creation_input_tokens"] = 0,
                ["cache_read_input_tokens"] = cachedTokens,
                ["output_tokens"] = outputTokens
            },
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = MapOpenAiFinishReason(null)
            }
        });
        AppendSseEvent(builder, "message_stop", new JsonObject
        {
            ["type"] = "message_stop"
        });
        return builder.ToString();
    }

    /// <summary>
    /// 解析 Anthropic assistant 内容块，拆分出文本、工具调用和图片内容。
    /// </summary>
    private static (string? TextContent, JsonArray ToolUseBlocks, JsonArray ImageBlocks) ParseAnthropicContentBlocks(JsonNode? content)
    {
        string? textContent = null;
        var toolUseBlocks = new JsonArray();
        var imageBlocks = new JsonArray();

        if (content is null)
            return (null, toolUseBlocks, imageBlocks);

        if (content is JsonValue value)
        {
            try { textContent = value.GetValue<string>(); } catch { textContent = value.ToJsonString(); }
            return (textContent, toolUseBlocks, imageBlocks);
        }

        if (content is not JsonArray blocks)
            return (content.ToJsonString(), toolUseBlocks, imageBlocks);

        var textParts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is not JsonObject blockObj) continue;
            var type = blockObj["type"]?.GetValue<string>() ?? "";

            switch (type)
            {
                case "text":
                    var t = blockObj["text"]?.GetValue<string>();
                    if (t is not null) textParts.Add(t);
                    break;
                case "tool_use":
                    toolUseBlocks.Add(new JsonObject
                    {
                        ["id"] = blockObj["id"]?.DeepClone(),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = blockObj["name"]?.DeepClone(),
                            ["arguments"] = blockObj["input"]?.ToJsonString() ?? "{}"
                        }
                    });
                    break;
                case "image":
                    if (blockObj["source"] is JsonObject src)
                    {
                        var mediaType = src["media_type"]?.GetValue<string>() ?? "image/png";
                        var data = src["data"]?.GetValue<string>() ?? "";
                        imageBlocks.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{data}" }
                        });
                    }
                    break;
            }
        }

        if (textParts.Count > 0)
            textContent = string.Join("\n", textParts);

        return (textContent, toolUseBlocks, imageBlocks);
    }

    /// <summary>
    /// 解析 Anthropic user 内容块，拆分出文本、工具结果和图片内容。
    /// </summary>
    private static (string? TextContent, JsonArray ImageBlocks, List<JsonObject> ToolResults, JsonArray UserImageBlocks) ParseAnthropicUserContent(JsonNode? content)
    {
        string? textContent = null;
        var imageBlocks = new JsonArray();
        var toolResults = new List<JsonObject>();
        var userImageBlocks = new JsonArray();

        if (content is null)
            return (null, imageBlocks, toolResults, userImageBlocks);

        if (content is JsonValue value)
        {
            try { textContent = value.GetValue<string>(); } catch { textContent = value.ToJsonString(); }
            return (textContent, imageBlocks, toolResults, userImageBlocks);
        }

        if (content is not JsonArray blocks)
            return (content.ToJsonString(), imageBlocks, toolResults, userImageBlocks);

        var textParts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is not JsonObject blockObj) continue;
            var type = blockObj["type"]?.GetValue<string>() ?? "";

            switch (type)
            {
                case "text":
                    var t = blockObj["text"]?.GetValue<string>();
                    if (t is not null) textParts.Add(t);
                    break;
                case "tool_result":
                    var trContent = blockObj["content"];
                    // tool_result content 可能是字符串或数组
                    var trContentStr = SerializeAnthropicToolResultContent(trContent);
                    toolResults.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = blockObj["tool_use_id"]?.DeepClone(),
                        ["content"] = trContentStr
                    });
                    break;
                case "image":
                    if (blockObj["source"] is JsonObject src)
                    {
                        var mediaType = src["media_type"]?.GetValue<string>() ?? "image/png";
                        var data = src["data"]?.GetValue<string>() ?? "";
                        userImageBlocks.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{data}" }
                        });
                    }
                    break;
            }
        }

        if (textParts.Count > 0)
            textContent = string.Join("\n", textParts);

        return (textContent, imageBlocks, toolResults, userImageBlocks);
    }

    /// <summary>
    /// 解析 OpenAI 内容并转换为 Anthropic 可用的文本和媒体块。
    /// </summary>
    private static (string? TextContent, JsonArray ImageBlocks) ParseOpenAiContentToClaudeBlocks(JsonNode? content)
    {
        string? textContent = null;
        var imageBlocks = new JsonArray();

        if (content is null)
            return (null, imageBlocks);

        if (content is JsonValue value)
        {
            try { textContent = value.GetValue<string>(); } catch { textContent = value.ToJsonString(); }
            return (textContent, imageBlocks);
        }

        if (content is not JsonArray parts)
            return (content.ToJsonString(), imageBlocks);

        var textParts = new List<string>();
        foreach (var part in parts)
        {
            if (part is not JsonObject partObj) continue;
            var type = partObj["type"]?.GetValue<string>() ?? "";

            if (type == "text")
            {
                var t = partObj["text"]?.GetValue<string>();
                if (t is not null) textParts.Add(t);
            }
            else if (type == "image_url")
            {
                var url = partObj["image_url"]?["url"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var commaIdx = url.IndexOf(',');
                    if (commaIdx > 0)
                    {
                        var meta = url[..commaIdx];
                        var data = url[(commaIdx + 1)..];
                        var mediaType = meta.Replace("data:", "", StringComparison.OrdinalIgnoreCase)
                            .Replace(";base64", "", StringComparison.OrdinalIgnoreCase);
                        var blockType = mediaType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) ? "document" : "image";
                        imageBlocks.Add(new JsonObject
                        {
                            ["type"] = blockType,
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = mediaType,
                                ["data"] = data
                            }
                        });
                    }
                }
            }
        }

        if (textParts.Count > 0)
            textContent = string.Join("\n", textParts);

        return (textContent, imageBlocks);
    }

    /// <summary>
    /// 尽量将 OpenAI 内容节点提取为纯文本字符串。
    /// </summary>
    private static string? ExtractOpenAiContentAsString(JsonNode? content)
    {
        if (content is null) return null;
        if (content is JsonValue value)
        {
            try { return value.GetValue<string>(); }
            catch { return value.ToJsonString(); }
        }
        if (content is JsonArray parts)
        {
            var textParts = new List<string>();
            foreach (var part in parts)
            {
                if (part is JsonObject partObj && partObj["type"]?.GetValue<string>() == "text")
                {
                    var t = partObj["text"]?.GetValue<string>();
                    if (t is not null) textParts.Add(t);
                }
            }
            return textParts.Count > 0 ? string.Join("\n", textParts) : null;
        }
        return content.ToJsonString();
    }

    /// <summary>
    /// 将 OpenAI tool 消息内容序列化为可写入 Anthropic 的文本。
    /// </summary>
    private static string SerializeOpenAiToolContent(JsonNode? content)
    {
        if (content is null)
        {
            return "...";
        }

        if (content is JsonValue value)
        {
            try
            {
                return value.GetValue<string>();
            }
            catch
            {
                return value.ToJsonString();
            }
        }

        return content.ToJsonString();
    }

    /// <summary>
    /// 将 Anthropic tool_result 内容整理为 OpenAI 可用的文本。
    /// </summary>
    private static string SerializeAnthropicToolResultContent(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value)
        {
            try
            {
                return value.GetValue<string>();
            }
            catch
            {
                return value.ToJsonString();
            }
        }

        if (content is JsonArray blocks)
        {
            var textParts = new List<string>();
            foreach (var block in blocks)
            {
                if (block is JsonObject blockObj && blockObj["type"]?.GetValue<string>() == "text")
                {
                    var text = blockObj["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textParts.Add(text);
                    }
                }
            }

            if (textParts.Count > 0)
            {
                return string.Join("\n", textParts);
            }
        }

        return content.ToJsonString();
    }

    /// <summary>
    /// 将 Anthropic tools 配置转换为 OpenAI function tools。
    /// </summary>
    private static void ConvertAnthropicToolsToOpenAi(JsonObject rootNode, JsonObject payload)
    {
        if (rootNode["tools"] is not JsonArray tools) return;
        var openAiTools = new JsonArray();

        foreach (var tool in tools)
        {
            if (tool is not JsonObject toolObj) continue;
            var name = toolObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            openAiTools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = toolObj["description"]?.DeepClone(),
                    ["parameters"] = toolObj["input_schema"]?.DeepClone()
                }
            });
        }

        if (openAiTools.Count > 0)
            payload["tools"] = openAiTools;
    }

    /// <summary>
    /// 将 Anthropic 的 tool_choice 配置转换为 OpenAI 格式。
    /// </summary>
    private static void ConvertAnthropicToolChoiceToOpenAi(JsonObject rootNode, JsonObject payload)
    {
        if (rootNode["tool_choice"] is not JsonNode tc) return;

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
            return;
        }

        if (tc is JsonObject tcObj)
        {
            var type = tcObj["type"]?.GetValue<string>();
            if (type == "tool" && tcObj["name"] is not null)
            {
                payload["tool_choice"] = new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tcObj["name"]!.DeepClone() }
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

            // disable_parallel_tool_use → parallel_tool_calls
            if (tcObj["disable_parallel_tool_use"] is JsonNode dptu)
            {
                var disabled = dptu.GetValue<bool>();
                payload["parallel_tool_calls"] = !disabled;
            }
        }
    }

    /// <summary>
    /// 将 OpenAI function tools 配置转换为 Anthropic tools。
    /// </summary>
    private static void ConvertOpenAiToolsToAnthropic(JsonObject rootNode, JsonObject payload)
    {
        if (rootNode["tools"] is not JsonArray tools) return;
        var claudeTools = new JsonArray();

        foreach (var tool in tools)
        {
            if (tool is not JsonObject toolObj) continue;
            var type = toolObj["type"]?.GetValue<string>();
            if (type != "function") continue;

            var func = toolObj["function"];
            if (func is not JsonObject funcObj) continue;
            var name = funcObj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            claudeTools.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = funcObj["description"]?.DeepClone(),
                ["input_schema"] = funcObj["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
            });
        }

        if (claudeTools.Count > 0)
            payload["tools"] = claudeTools;
    }

    /// <summary>
    /// 将 OpenAI 的 tool_choice 配置转换为 Anthropic 格式。
    /// </summary>
    private static void ConvertOpenAiToolChoiceToAnthropic(JsonObject rootNode, JsonObject payload)
    {
        var tc = rootNode["tool_choice"];
        var ptc = rootNode["parallel_tool_calls"];

        if (tc is null && ptc is null) return;

        JsonNode? claudeTc = null;

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
            // { type: "function", function: { name: "..." } }
            if (tcObj["function"] is JsonObject func && func["name"] is not null)
            {
                claudeTc = new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = func["name"]!.DeepClone()
                };
            }
        }

        if (claudeTc is null && ptc is not null)
            claudeTc = new JsonObject { ["type"] = "auto" };

        // parallel_tool_calls → disable_parallel_tool_use
        if (ptc is not null && claudeTc is JsonObject tcResultObj)
        {
            var parallel = ptc.GetValue<bool>();
            var tcType = tcResultObj["type"]?.GetValue<string>();
            if (tcType != "none")
                tcResultObj["disable_parallel_tool_use"] = !parallel;
        }

        if (claudeTc is not null)
            payload["tool_choice"] = claudeTc;
    }

    /// <summary>
    /// 将 OpenAI 的 web_search_options 转换为 Anthropic web_search 工具配置。
    /// </summary>
    private static void ConvertOpenAiWebSearchToAnthropic(JsonObject rootNode, JsonObject payload)
    {
        if (rootNode["web_search_options"] is not JsonObject wsOpts) return;

        var webSearchTool = new JsonObject
        {
            ["type"] = "web_search_20250305",
            ["name"] = "web_search"
        };

        // search_context_size → max_uses
        if (wsOpts["search_context_size"] is JsonNode scs)
        {
            var size = scs.GetValue<string>();
            webSearchTool["max_uses"] = size switch
            {
                "low" => 1,
                "medium" => 5,
                _ => 10
            };
        }

        // user_location
        if (wsOpts["user_location"] is JsonNode ul)
        {
            try
            {
                var ulObj = ul as JsonObject ?? JsonNode.Parse(ul.ToJsonString()) as JsonObject;
                if (ulObj?["approximate"] is JsonObject approx)
                {
                    webSearchTool["user_location"] = new JsonObject
                    {
                        ["type"] = "approximate",
                        ["timezone"] = approx["timezone"]?.DeepClone(),
                        ["country"] = approx["country"]?.DeepClone(),
                        ["region"] = approx["region"]?.DeepClone(),
                        ["city"] = approx["city"]?.DeepClone()
                    };
                }
            }
            catch { }
        }

        // 合并到已有 tools 数组或新建
        if (payload["tools"] is JsonArray existingTools)
        {
            existingTools.Add(webSearchTool);
        }
        else
        {
            payload["tools"] = new JsonArray { webSearchTool };
        }
    }

    /// <summary>
    /// 暂存同一条 OpenAI 消息转换后的 Anthropic 角色和内容块。
    /// </summary>
    private sealed class OpenAiMessageAccumulator
    {
        /// <summary>
        /// 当前暂存消息的角色。
        /// </summary>
        public string Role { get; init; } = "user";
        /// <summary>
        /// 当前暂存消息累积的内容块。
        /// </summary>
        public List<JsonObject> Blocks { get; } = [];
    }

    /// <summary>
    /// 将暂存消息合并或追加到 Anthropic 消息数组中。
    /// </summary>
    private static void AddAccumulatorToMessages(JsonArray claudeMessages, OpenAiMessageAccumulator accumulator, ref OpenAiMessageAccumulator? lastAccumulator)
    {
        if (accumulator.Blocks.Count == 0)
        {
            lastAccumulator = null;
            return;
        }

        // 同角色可以合并（仅当两者都没有 tool_result 时直接追加）
        if (lastAccumulator is not null && lastAccumulator.Role == accumulator.Role)
        {
            // 检查上一条消息是否有 tool_result blocks，如果有则不能合并
            var lastMsg = claudeMessages[claudeMessages.Count - 1] as JsonObject;
            var lastContent = lastMsg?["content"];
            bool lastHasToolResult = false;
            if (lastContent is JsonArray lastBlocks)
            {
                foreach (var b in lastBlocks)
                {
                    if (b is JsonObject bo && bo["type"]?.GetValue<string>() == "tool_result")
                    { lastHasToolResult = true; break; }
                }
            }

            if (!lastHasToolResult)
            {
                // 追加 blocks 到上一条消息
                if (lastContent is JsonArray arr)
                {
                    foreach (var b in accumulator.Blocks) arr.Add(b.DeepClone());
                }
                else
                {
                    // 上一条是纯文本，转为数组
                    var newArr = new JsonArray();
                    if (lastMsg?["content"] is not null)
                        newArr.Add(new JsonObject { ["type"] = "text", ["text"] = lastMsg!["content"]!.ToJsonString() });
                    foreach (var b in accumulator.Blocks) newArr.Add(b.DeepClone());
                    lastMsg!["content"] = newArr;
                }
                lastAccumulator = accumulator;
                return;
            }
        }

        // 独立消息
        var msg = new JsonObject { ["role"] = accumulator.Role };
        if (accumulator.Blocks.Count == 1 && accumulator.Blocks[0]["type"]?.GetValue<string>() == "text")
        {
            msg["content"] = accumulator.Blocks[0]["text"]?.DeepClone();
        }
        else
        {
            var arr = new JsonArray();
            foreach (var b in accumulator.Blocks) arr.Add(b.DeepClone());
            msg["content"] = arr;
        }

        claudeMessages.Add(msg);
        lastAccumulator = accumulator;
    }

    /// <summary>
    /// 将 Anthropic 的停止原因映射为 OpenAI finish_reason。
    /// </summary>
    private static string MapAnthropicStopReason(string? stopReason)
    {
        return stopReason?.ToLowerInvariant() switch
        {
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            "stop_sequence" => "stop",
            "refusal" => "content_filter",
            _ => "stop"
        };
    }

    /// <summary>
    /// 向字符串构建器追加一个 SSE 事件。
    /// </summary>
    private static void AppendSseEvent(StringBuilder builder, string eventName, JsonObject payload)
    {
        builder.Append("event: ").Append(eventName).Append('\n');
        builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
    }

    /// <summary>
    /// 在需要时关闭当前的 thinking 内容块。
    /// </summary>
    private static void CloseThinkingBlockIfNeeded(StringBuilder builder, AnthropicOpenAiStreamState state)
    {
        if (state.ThinkingIndex < 0 || state.ThinkingClosed)
        {
            return;
        }

        AppendSseEvent(builder, "content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = state.ThinkingIndex
        });
        state.ThinkingClosed = true;
    }

    /// <summary>
    /// 在需要时关闭当前的 text 内容块。
    /// </summary>
    private static void CloseTextBlockIfNeeded(StringBuilder builder, AnthropicOpenAiStreamState state)
    {
        if (state.TextIndex < 0 || state.TextClosed)
        {
            return;
        }

        AppendSseEvent(builder, "content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = state.TextIndex
        });
        state.TextClosed = true;
    }

    /// <summary>
    /// 关闭所有已开始但尚未结束的工具调用内容块。
    /// </summary>
    private static void CloseToolCallBlocks(StringBuilder builder, AnthropicOpenAiStreamState state)
    {
        foreach (var toolCallState in state.ToolCalls.OrderBy(x => x.Key).Select(x => x.Value))
        {
            if (!toolCallState.Started || toolCallState.Closed)
            {
                continue;
            }

            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = toolCallState.ContentIndex
            });
            toolCallState.Closed = true;
        }
    }

    /// <summary>
    /// 将 OpenAI 工具调用增量追加为 Anthropic 的 tool_use 事件。
    /// </summary>
    private static void AppendToolCallDelta(StringBuilder builder, AnthropicOpenAiStreamState state, OpenAiToolCallDelta toolCallDelta)
    {
        if (!state.ToolCalls.TryGetValue(toolCallDelta.Index, out var toolCallState))
        {
            toolCallState = new AnthropicToolCallBlockState
            {
                ContentIndex = state.NextContentIndex++
            };
            state.ToolCalls[toolCallDelta.Index] = toolCallState;
        }

        if (!string.IsNullOrWhiteSpace(toolCallDelta.Id))
        {
            toolCallState.ToolUseId = toolCallDelta.Id;
        }

        if (!string.IsNullOrWhiteSpace(toolCallDelta.Name))
        {
            toolCallState.Name = toolCallDelta.Name;
        }

        if (!toolCallState.Started)
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = toolCallState.ContentIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = toolCallState.ToolUseId,
                    ["name"] = toolCallState.Name,
                    ["input"] = new JsonObject()
                }
            });
            toolCallState.Started = true;
        }

        if (!string.IsNullOrEmpty(toolCallDelta.ArgumentsDelta))
        {
            AppendSseEvent(builder, "content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = toolCallState.ContentIndex,
                ["delta"] = new JsonObject
                {
                    ["type"] = "input_json_delta",
                    ["partial_json"] = toolCallDelta.ArgumentsDelta
                }
            });
        }
    }

    /// <summary>
    /// 从 OpenAI choice 增量中提取工具调用片段列表。
    /// </summary>
    private static List<OpenAiToolCallDelta> GetToolCallDeltas(JsonElement choice)
    {
        var toolCallDeltas = new List<OpenAiToolCallDelta>();
        if (!choice.TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() == 0)
        {
            return toolCallDeltas;
        }

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            var index = toolCall.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                ? indexElement.GetInt32()
                : 0;
            var id = toolCall.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
            var name = string.Empty;
            var argumentsDelta = string.Empty;
            if (toolCall.TryGetProperty("function", out var functionElement) && functionElement.ValueKind == JsonValueKind.Object)
            {
                if (functionElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                {
                    name = nameElement.GetString() ?? string.Empty;
                }

                if (functionElement.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String)
                {
                    argumentsDelta = argumentsElement.GetString() ?? string.Empty;
                }
            }

            toolCallDeltas.Add(new OpenAiToolCallDelta(index, id, name, argumentsDelta));
        }

        return toolCallDeltas;
    }

    /// <summary>
    /// 表示单个 OpenAI 工具调用增量片段。
    /// </summary>
    private readonly record struct OpenAiToolCallDelta(int Index, string Id, string Name, string ArgumentsDelta);

    /// <summary>
    /// 克隆节点并确保返回 JsonArray 实例。
    /// </summary>
    private static JsonArray CloneArray(JsonNode? node)
    {
        return node?.DeepClone() as JsonArray ?? [];
    }

    /// <summary>
    /// 当源对象包含指定属性时复制到目标对象。
    /// </summary>
    private static void CopyNodeIfPresent(JsonObject source, JsonObject target, string propertyName)
    {
        if (source[propertyName] is not null)
        {
            target[propertyName] = source[propertyName]!.DeepClone();
        }
    }

    /// <summary>
    /// 从 system 节点中提取可用的系统提示文本。
    /// </summary>
    private static string ExtractSystemContent(JsonNode systemNode)
    {
        return systemNode switch
        {
            JsonValue value => value.ToJsonString().Trim('"'),
            JsonArray array => string.Join("\n", array.Select(ExtractTextFromNode).Where(x => !string.IsNullOrWhiteSpace(x))),
            JsonObject obj => ExtractTextFromNode(obj),
            _ => systemNode.ToJsonString()
        };
    }

    /// <summary>
    /// 仅替换请求体中的模型名称。
    /// </summary>
    private static string ReplaceModelName(string requestBody, string targetModelName)
    {
        try
        {
            var rootNode = JsonNode.Parse(requestBody) as JsonObject;
            if (rootNode is null)
            {
                return requestBody;
            }

            rootNode["model"] = targetModelName;
            return rootNode.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }

    /// <summary>
    /// 替换 OpenAI 请求中的模型名称，并在流式场景补齐 usage 配置。
    /// </summary>
    private static string ReplaceOpenAiModelAndEnsureStreamUsage(string requestBody, string targetModelName, bool enableStreaming)
    {
        try
        {
            var rootNode = JsonNode.Parse(requestBody) as JsonObject;
            if (rootNode is null)
            {
                return requestBody;
            }

            rootNode["model"] = targetModelName;

            var streamRequested = enableStreaming;
            if (!streamRequested && rootNode["stream"] is JsonValue streamNode && streamNode.TryGetValue<bool>(out var streamEnabled))
            {
                streamRequested = streamEnabled;
            }

            if (streamRequested)
            {
                // OpenAI 直传流式场景默认补 usage，避免 UsageLogs 与调试页里 token 永远是 0。
                rootNode["stream_options"] = new JsonObject { ["include_usage"] = true };
            }

            return rootNode.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }
    /// <summary>
    /// 从 OpenAI 普通响应中提取正文文本和推理文本。
    /// </summary>
    private static (string ContentText, string ReasoningText) ExtractOpenAiResponseText(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return (responseBody, string.Empty);
            }

            var message = GetPreferredOpenAiMessage(choices);
            if (message.ValueKind == JsonValueKind.Object)
            {
                var reasoning = ExtractReasoningFromElement(message);
                var content = ExtractContentFromMessage(message);
                return (content, reasoning);
            }

            return (responseBody, string.Empty);
        }
        catch
        {
            return (responseBody, string.Empty);
        }
    }

    /// <summary>
    /// 从 Anthropic 普通响应中提取正文文本和推理文本。
    /// </summary>
    private static (string ContentText, string ReasoningText) ExtractAnthropicResponseText(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                return (responseBody, string.Empty);
            }

            var textParts = new List<string>();
            var reasoningParts = new List<string>();
            foreach (var item in contentArray.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : string.Empty;
                if (string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
                {
                    AppendIfNotEmpty(reasoningParts, ExtractElementText(item, "thinking", "text", "content"));
                    continue;
                }

                AppendIfNotEmpty(textParts, ExtractElementText(item, "text", "content"));
            }

            return (string.Join("\n", textParts), string.Join("\n", reasoningParts));
        }
        catch
        {
            return (responseBody, string.Empty);
        }
    }

    /// <summary>
    /// 从 OpenAI 流式响应中提取完整正文文本和推理文本。
    /// </summary>
    private static (string? ContentText, string ReasoningText) ExtractOpenAiStreamingText(string responseBody)
    {
        var textBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var sawContentDelta = false;

        foreach (var rawLine in responseBody.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var jsonText = line["data: ".Length..];
            if (string.Equals(jsonText, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(jsonText);
                if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    continue;
                }

                foreach (var choice in EnumerateOpenAiChoices(document.RootElement))
                {
                    var delta = choice.TryGetProperty("delta", out var deltaElement) ? deltaElement : default;
                    var content = ExtractDeltaContent(delta);
                    if (content is not null)
                    {
                        sawContentDelta = true;
                        textBuilder.Append(content);
                    }

                    AppendIfNotEmpty(reasoningBuilder, ExtractReasoningFromElement(delta));
                }
            }
            catch
            {
            }
        }

        return (sawContentDelta ? textBuilder.ToString() : null, reasoningBuilder.ToString());
    }

    /// <summary>
    /// 从 Anthropic 流式响应中提取完整正文文本和推理文本。
    /// </summary>
    private static (string? ContentText, string ReasoningText) ExtractAnthropicStreamingText(string responseBody)
    {
        var textBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var currentEvent = string.Empty;
        var dataLines = new List<string>();
        var sawContentDelta = false;

        foreach (var rawLine in responseBody.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("event: ", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line["event: ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data: ".Length..]);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line) || dataLines.Count == 0)
            {
                continue;
            }

            ProcessAnthropicSseBlock(currentEvent, dataLines, textBuilder, reasoningBuilder, ref sawContentDelta);
            currentEvent = string.Empty;
            dataLines.Clear();
        }

        if (dataLines.Count > 0)
        {
            ProcessAnthropicSseBlock(currentEvent, dataLines, textBuilder, reasoningBuilder, ref sawContentDelta);
        }

        return (sawContentDelta ? textBuilder.ToString() : null, reasoningBuilder.ToString());
    }

    /// <summary>
    /// 处理单个 Anthropic SSE 数据块并提取文本或推理内容。
    /// </summary>
    private static void ProcessAnthropicSseBlock(string eventName, List<string> dataLines, StringBuilder textBuilder, StringBuilder reasoningBuilder, ref bool sawContentDelta)
    {
        var data = string.Join("\n", dataLines);
        if (data == "[DONE]")
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("delta", out var delta))
            {
                return;
            }

            var deltaType = delta.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : string.Empty;
            if (string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(deltaType, "thinking_delta", StringComparison.OrdinalIgnoreCase))
            {
                AppendIfNotEmpty(reasoningBuilder, ExtractElementText(delta, "thinking", "text", "content"));
                return;
            }

            // signature_delta 事件：部分模型发送签名信息，视为换行
            if (string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(deltaType, "signature_delta", StringComparison.OrdinalIgnoreCase))
            {
                reasoningBuilder.Append('\n');
                return;
            }

            if (string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase))
            {
                var text = ExtractElementText(delta, "text", "content");
                if (text is not null)
                {
                    sawContentDelta = true;
                    textBuilder.Append(text);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 枚举响应中的 OpenAI choices 数组。
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateOpenAiChoices(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            yield return choice;
        }
    }

    /// <summary>
    /// 从 choices 中选择最适合作为主结果的项。
    /// </summary>
    private static JsonElement? GetPreferredOpenAiChoice(JsonElement choices)
    {
        JsonElement? fallback = null;
        foreach (var choice in choices.EnumerateArray())
        {
            fallback ??= choice;
            if (choice.TryGetProperty("message", out var message))
            {
                var hasToolCalls = message.TryGetProperty("tool_calls", out var toolCalls) &&
                                   toolCalls.ValueKind == JsonValueKind.Array &&
                                   toolCalls.GetArrayLength() > 0;
                var hasReasoning = !string.IsNullOrWhiteSpace(ExtractReasoningFromElement(message));
                var hasContent = !string.IsNullOrWhiteSpace(ExtractContentFromMessage(message));
                if (hasToolCalls || hasReasoning || hasContent)
                {
                    return choice;
                }
            }
        }

        return fallback;
    }

    /// <summary>
    /// 获取首个包含有效内容的 OpenAI message。
    /// </summary>
    private static JsonElement GetPreferredOpenAiMessage(JsonElement choices)
    {
        var preferredChoice = GetPreferredOpenAiChoice(choices);
        if (preferredChoice is { } choice && choice.TryGetProperty("message", out var message))
        {
            return message;
        }

        return default;
    }

    /// <summary>
    /// 从 OpenAI message 中提取非推理类正文内容。
    /// </summary>
    private static string ExtractContentFromMessage(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var contentElement))
        {
            return string.Empty;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            var itemType = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
            if (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(itemType, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendIfNotEmpty(parts, ExtractElementText(item, "text", "content"));
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// 从响应节点中提取 reasoning 或 thinking 文本。
    /// </summary>
    private static string ExtractReasoningFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (element.TryGetProperty("reasoning_content", out var reasoningContent) && reasoningContent.ValueKind == JsonValueKind.String)
        {
            return reasoningContent.GetString() ?? string.Empty;
        }

        if (element.TryGetProperty("reasoning", out var reasoning))
        {
            return ExtractElementText(reasoning, "text", "content", "summary_text", "reasoning") ?? string.Empty;
        }

        if (element.TryGetProperty("thinking", out var thinking))
        {
            return ExtractElementText(thinking, "text", "content", "thinking") ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// 从增量节点中提取文本 content 字段。
    /// </summary>
    private static string? ExtractDeltaContent(JsonElement delta)
    {
        if (delta.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        return null;
    }

    /// <summary>
    /// 将 OpenAI 的 finish_reason 映射为 Anthropic 停止原因。
    /// </summary>
    private static string MapOpenAiFinishReason(string? finishReason)
    {
        return finishReason?.ToLowerInvariant() switch
        {
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            "content_filter" => "refusal",
            "stop" => "end_turn",
            _ => "end_turn"
        };
    }

    /// <summary>
    /// 按协议类型从 usage 节点中提取输入、缓存和输出 token 数。
    /// </summary>
    private static (int InputTokens, int CachedTokens, int OutputTokens) ExtractUsageFromElement(JsonElement usage, string protocolType)
    {
        if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var input = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            var cached = 0;
            if (usage.TryGetProperty("cache_read_input_tokens", out var readCache))
            {
                cached += readCache.GetInt32();
            }

            if (usage.TryGetProperty("cache_creation_input_tokens", out var createCache))
            {
                cached += createCache.GetInt32();
            }

            var output = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            return (input, cached, output);
        }

        var prompt = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
        var promptDetails = usage.TryGetProperty("prompt_tokens_details", out var ptd) ? ptd : default;
        var cachedTokens = promptDetails.ValueKind == JsonValueKind.Object && promptDetails.TryGetProperty("cached_tokens", out var ct)
            ? ct.GetInt32()
            : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var completionTokens) ? completionTokens.GetInt32() : 0;
        return (prompt, cachedTokens, completion);
    }

    /// <summary>
    /// 按给定属性名顺序从 JsonElement 中提取文本。
    /// </summary>
    private static string? ExtractElementText(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var propertyValue))
            {
                continue;
            }

            if (propertyValue.ValueKind == JsonValueKind.String)
            {
                return propertyValue.GetString() ?? string.Empty;
            }

            if (propertyValue.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in propertyValue.EnumerateArray())
                {
                    var text = ExtractElementText(item, "text", "content", "thinking");
                    if (text is not null)
                    {
                        parts.Add(text);
                    }
                }
                return string.Join("\n", parts);
            }
        }

        return null;
    }

    /// <summary>
    /// 递归从 JsonNode 中提取最合适的文本内容。
    /// </summary>
    private static string ExtractTextFromNode(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            try
            {
                return value.GetValue<string>();
            }
            catch
            {
                return value.ToJsonString();
            }
        }

        if (node is JsonObject obj)
        {
            foreach (var propertyName in new[] { "text", "content", "thinking" })
            {
                if (obj[propertyName] is JsonNode child)
                {
                    var text = ExtractTextFromNode(child);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        if (node is JsonArray array)
        {
            return string.Join("\n", array.Select(ExtractTextFromNode).Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        return node.ToJsonString();
    }

    /// <summary>
    /// 当文本非空时追加到字符串列表中。
    /// </summary>
    private static void AppendIfNotEmpty(List<string> parts, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(text);
        }
    }

    /// <summary>
    /// 当文本非空时追加到字符串构建器中。
    /// </summary>
    private static void AppendIfNotEmpty(StringBuilder builder, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.Append(text);
        }
    }
}
