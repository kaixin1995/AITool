using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Web.Services;

// 代理协议桥接器，负责在客户端协议与目标站点协议不一致时转换请求和响应。
public static class ProxyProtocolBridge
{
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
                    ["input_tokens"] = inputTokens
                }
            }
        });

        var contentIndex = 0;
        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = ""
                }
            });
            AppendSseEvent(builder, "content_block_delta", new JsonObject
            {
                ["index"] = contentIndex,
                ["delta"] = new JsonObject
                {
                    ["type"] = "thinking_delta",
                    ["thinking"] = reasoningText
                }
            });
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["index"] = contentIndex
            });
            contentIndex++;
        }

        if (!string.IsNullOrWhiteSpace(contentText))
        {
            AppendSseEvent(builder, "content_block_start", new JsonObject
            {
                ["index"] = contentIndex,
                ["content_block"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ""
                }
            });
            AppendSseEvent(builder, "content_block_delta", new JsonObject
            {
                ["index"] = contentIndex,
                ["delta"] = new JsonObject
                {
                    ["type"] = "text_delta",
                    ["text"] = contentText
                }
            });
            AppendSseEvent(builder, "content_block_stop", new JsonObject
            {
                ["index"] = contentIndex
            });
        }

        AppendSseEvent(builder, "message_delta", new JsonObject
        {
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = "end_turn"
            },
            ["usage"] = new JsonObject
            {
                ["output_tokens"] = outputTokens,
                ["cache_read_input_tokens"] = cachedTokens
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

        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            AppendOpenAiChunk(builder, modelName, new JsonObject
            {
                ["reasoning_content"] = reasoningText
            });
        }

        if (!string.IsNullOrWhiteSpace(contentText))
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

    private static void AppendSseEvent(StringBuilder builder, string eventName, JsonObject payload)
    {
        builder.Append("event: ").Append(eventName).Append('\n');
        builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
    }

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

    private static (string ContentText, string ReasoningText) ExtractOpenAiStreamingText(string responseBody)
    {
        var textBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();

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
                AppendIfNotEmpty(textBuilder, ExtractDeltaContent(delta));
                AppendIfNotEmpty(reasoningBuilder, ExtractReasoningFromElement(delta));
            }
            catch
            {
            }
        }

        return (textBuilder.ToString(), reasoningBuilder.ToString());
    }

    private static (string ContentText, string ReasoningText) ExtractAnthropicStreamingText(string responseBody)
    {
        var textBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var currentEvent = string.Empty;
        var dataLines = new List<string>();

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

            ProcessAnthropicSseBlock(currentEvent, dataLines, textBuilder, reasoningBuilder);
            currentEvent = string.Empty;
            dataLines.Clear();
        }

        if (dataLines.Count > 0)
        {
            ProcessAnthropicSseBlock(currentEvent, dataLines, textBuilder, reasoningBuilder);
        }

        return (textBuilder.ToString(), reasoningBuilder.ToString());
    }

    private static void ProcessAnthropicSseBlock(string eventName, List<string> dataLines, StringBuilder textBuilder, StringBuilder reasoningBuilder)
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
                AppendIfNotEmpty(textBuilder, ExtractElementText(delta, "text", "content"));
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
            return ExtractElementText(reasoning, "text", "content", "summary_text", "reasoning");
        }

        if (element.TryGetProperty("thinking", out var thinking))
        {
            return ExtractElementText(thinking, "text", "content", "thinking");
        }

        return string.Empty;
    }

    private static string ExtractDeltaContent(JsonElement delta)
    {
        if (delta.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ExtractElementText(JsonElement element, params string[] propertyNames)
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
                    AppendIfNotEmpty(parts, ExtractElementText(item, "text", "content", "thinking"));
                }
                return string.Join("\n", parts);
            }
        }

        return string.Empty;
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
