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
}
