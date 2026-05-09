using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Web.Services;

// 代理协议桥接器，负责在客户端协议与目标站点协议不一致时转换请求和响应。
public static class ProxyProtocolBridge
{
    public sealed class AnthropicOpenAiStreamState
    {
        public string MessageId { get; set; } = $"msg_{Guid.NewGuid():N}";
        public int NextContentIndex { get; set; }
        public int ThinkingIndex { get; set; } = -1;
        public int TextIndex { get; set; } = -1;
        public bool ThinkingClosed { get; set; }
        public bool TextClosed { get; set; }
        public bool HadAnyContent { get; set; }
        public bool ReceivedDoneEvent { get; set; }
        public int InputTokens { get; set; }
        public int CachedTokens { get; set; }
        public int OutputTokens { get; set; }
        public string StopReason { get; set; } = "end_turn";
        public Dictionary<int, AnthropicToolCallBlockState> ToolCalls { get; } = [];
    }

    public sealed class AnthropicToolCallBlockState
    {
        public int ContentIndex { get; init; }
        public string ToolUseId { get; set; } = $"toolu_{Guid.NewGuid():N}";
        public string Name { get; set; } = string.Empty;
        public bool Started { get; set; }
        public bool Closed { get; set; }
    }

    public static string PrepareRequestBody(
        string clientProtocol,
        string targetProtocol,
        string requestBody,
        string targetModelName,
        bool enableStreaming)
    {
        if (string.Equals(clientProtocol, targetProtocol, StringComparison.OrdinalIgnoreCase))
        {
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
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return builder.ToString();
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finishReason) && finishReason.ValueKind == JsonValueKind.String)
            {
                state.StopReason = MapOpenAiFinishReason(finishReason.GetString());
            }

            if (!choice.TryGetProperty("delta", out var delta))
            {
                return builder.ToString();
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

            if (TryGetToolCallDelta(choice, out var toolCallDelta))
            {
                state.HadAnyContent = true;
                CloseThinkingBlockIfNeeded(builder, state);
                CloseTextBlockIfNeeded(builder, state);
                AppendToolCallDelta(builder, state, toolCallDelta);
            }

            var contentText = ExtractDeltaContent(delta);
            if (contentText is not null)
            {
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
                ["cache_creation_input_tokens"] = 0,
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

    // 跨协议转到 OpenAI 站点时，把 Anthropic messages 请求映射成 chat completions。
    private static string BuildOpenAiRequestFromAnthropic(JsonObject rootNode, string targetModelName, bool enableStreaming)
    {
        var messages = CloneArray(rootNode["messages"]);
        var systemNode = rootNode["system"];
        if (systemNode is not null)
        {
            messages.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = ExtractSystemContent(systemNode)
            });
        }

        var payload = new JsonObject
        {
            ["model"] = targetModelName,
            ["messages"] = messages,
            ["stream"] = enableStreaming,
            ["max_tokens"] = rootNode["max_tokens"]?.DeepClone() ?? 4096
        };

        if (enableStreaming)
        {
            payload["stream_options"] = new JsonObject
            {
                ["include_usage"] = true
            };
        }

        CopyNodeIfPresent(rootNode, payload, "temperature");
        CopyNodeIfPresent(rootNode, payload, "top_p");
        CopyNodeIfPresent(rootNode, payload, "metadata");
        CopyNodeIfPresent(rootNode, payload, "tools");
        CopyNodeIfPresent(rootNode, payload, "tool_choice");

        if (rootNode["stop_sequences"] is not null)
        {
            payload["stop"] = rootNode["stop_sequences"]!.DeepClone();
        }

        if (rootNode["thinking"] is not null)
        {
            // Anthropic thinking 在 OpenAI 兼容协议里降级为高强度 reasoning。
            payload["reasoning_effort"] = "high";
        }

        return payload.ToJsonString();
    }

    // 跨协议转到 Anthropic 站点时，把 OpenAI chat completions 请求映射成 messages。
    private static string BuildAnthropicRequestFromOpenAi(JsonObject rootNode, string targetModelName, bool enableStreaming)
    {
        var payload = new JsonObject
        {
            ["model"] = targetModelName,
            ["messages"] = CloneArray(rootNode["messages"]),
            ["stream"] = enableStreaming,
            ["max_tokens"] = rootNode["max_tokens"]?.DeepClone() ?? 4096
        };

        CopyNodeIfPresent(rootNode, payload, "temperature");
        CopyNodeIfPresent(rootNode, payload, "top_p");
        CopyNodeIfPresent(rootNode, payload, "metadata");
        CopyNodeIfPresent(rootNode, payload, "tools");
        CopyNodeIfPresent(rootNode, payload, "tool_choice");
        CopyNodeIfPresent(rootNode, payload, "system");

        if (rootNode["stop"] is not null)
        {
            payload["stop_sequences"] = rootNode["stop"]!.DeepClone();
        }

        if (rootNode["reasoning_effort"] is not null)
        {
            payload["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = 2048
            };
        }

        return payload.ToJsonString();
    }

    // 把 OpenAI 响应包装成 Anthropic messages 响应，保持客户端协议稳定。
    private static string BuildAnthropicResponseFromOpenAi(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        var (contentText, reasoningText) = ExtractOpenAiResponseText(responseBody);
        var contentArray = new JsonArray();
        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            contentArray.Add(new JsonObject
            {
                ["type"] = "thinking",
                ["thinking"] = reasoningText
            });
        }

        if (!string.IsNullOrWhiteSpace(contentText))
        {
            contentArray.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = contentText
            });
        }

        return new JsonObject
        {
            ["id"] = $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = modelName,
            ["content"] = contentArray,
            ["stop_reason"] = "end_turn",
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["cache_creation_input_tokens"] = 0,
                ["cache_read_input_tokens"] = cachedTokens,
                ["output_tokens"] = outputTokens
            }
        }.ToJsonString();
    }

    // 把 Anthropic 响应包装成 OpenAI chat completions 响应，兼容 OpenAI 客户端字段。
    private static string BuildOpenAiResponseFromAnthropic(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        var (contentText, reasoningText) = ExtractAnthropicResponseText(responseBody);
        var messageObject = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = contentText
        };

        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            messageObject["reasoning_content"] = reasoningText;
        }

        return new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = modelName,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = messageObject,
                    ["finish_reason"] = "stop"
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = inputTokens,
                ["prompt_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = cachedTokens
                },
                ["completion_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + cachedTokens + outputTokens
            }
        }.ToJsonString();
    }

    // 由于当前代理层会先完整收集上游 SSE，这里统一把 OpenAI 流汇总后再包装成 Anthropic 事件。
    private static string BuildAnthropicStreamingResponseFromOpenAi(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        var (contentText, reasoningText) = ExtractOpenAiStreamingText(responseBody);
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
                    ["input_tokens"] = Math.Max(inputTokens - cachedTokens, 0),
                    ["cache_creation_input_tokens"] = 0,
                    ["cache_read_input_tokens"] = cachedTokens,
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
                ["content_block"] = new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = ""
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
        }

        if (contentText is not null)
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ""
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
                ["stop_reason"] = "end_turn"
            }
        });
        AppendSseEvent(builder, "message_stop", new JsonObject
        {
            ["type"] = "message_stop"
        });

        return builder.ToString();
    }

    // Anthropic SSE 汇总后包装成 OpenAI chunk，保持客户端能正常解析 [DONE]。
    private static string BuildOpenAiStreamingResponseFromAnthropic(string responseBody, string modelName, int inputTokens, int cachedTokens, int outputTokens)
    {
        var (contentText, reasoningText) = ExtractAnthropicStreamingText(responseBody);
        var builder = new StringBuilder();

        if (reasoningText.Length > 0)
        {
            AppendOpenAiChunk(builder, modelName, new JsonObject
            {
                ["reasoning_content"] = reasoningText
            });
        }

        if (contentText is not null)
        {
            AppendOpenAiChunk(builder, modelName, new JsonObject
            {
                ["content"] = contentText
            });
        }

        builder.Append("data: ");
        builder.Append(new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = modelName,
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = inputTokens,
                ["prompt_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = cachedTokens
                },
                ["completion_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + cachedTokens + outputTokens
            }
        }.ToJsonString());
        builder.Append("\n\n");
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

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
                ["stop_reason"] = "end_turn"
            }
        });
        AppendSseEvent(builder, "message_stop", new JsonObject
        {
            ["type"] = "message_stop"
        });
        return builder.ToString();
    }

    private static void AppendSseEvent(StringBuilder builder, string eventName, JsonObject payload)
    {
        builder.Append("event: ").Append(eventName).Append('\n');
        builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
    }

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

    private static bool TryGetToolCallDelta(JsonElement choice, out OpenAiToolCallDelta toolCallDelta)
    {
        toolCallDelta = default;
        if (!choice.TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() == 0)
        {
            return false;
        }

        var firstToolCall = toolCalls[0];
        var index = firstToolCall.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
            ? indexElement.GetInt32()
            : 0;
        var id = firstToolCall.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var name = string.Empty;
        var argumentsDelta = string.Empty;
        if (firstToolCall.TryGetProperty("function", out var functionElement) && functionElement.ValueKind == JsonValueKind.Object)
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

        toolCallDelta = new OpenAiToolCallDelta(index, id, name, argumentsDelta);
        return true;
    }

    private readonly record struct OpenAiToolCallDelta(int Index, string Id, string Name, string ArgumentsDelta);

    private static JsonArray CloneArray(JsonNode? node)
    {
        return node?.DeepClone() as JsonArray ?? [];
    }

    private static void CopyNodeIfPresent(JsonObject source, JsonObject target, string propertyName)
    {
        if (source[propertyName] is not null)
        {
            target[propertyName] = source[propertyName]!.DeepClone();
        }
    }

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

            var message = choices[0].GetProperty("message");
            var reasoning = ExtractReasoningFromElement(message);
            var content = ExtractContentFromMessage(message);
            return (content, reasoning);
        }
        catch
        {
            return (responseBody, string.Empty);
        }
    }

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

                var delta = choices[0].TryGetProperty("delta", out var deltaElement) ? deltaElement : default;
                var content = ExtractDeltaContent(delta);
                if (content is not null)
                {
                    sawContentDelta = true;
                    textBuilder.Append(content);
                }

                AppendIfNotEmpty(reasoningBuilder, ExtractReasoningFromElement(delta));
            }
            catch
            {
            }
        }

        return (sawContentDelta ? textBuilder.ToString() : null, reasoningBuilder.ToString());
    }

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

    private static string MapOpenAiFinishReason(string? finishReason)
    {
        return finishReason?.ToLowerInvariant() switch
        {
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            "content_filter" => "stop_sequence",
            _ => "end_turn"
        };
    }

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

    private static void AppendIfNotEmpty(List<string> parts, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(text);
        }
    }

    private static void AppendIfNotEmpty(StringBuilder builder, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.Append(text);
        }
    }
}
