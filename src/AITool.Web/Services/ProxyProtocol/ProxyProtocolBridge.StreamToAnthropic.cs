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
}
