using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Protocol;

/// <summary>
/// Gemini GenerateContent 协议桥（响应方向）。移植自 gcli2api
/// （anthropic2gemini.gemini_to_anthropic_response / gemini_stream_to_anthropic_stream、
/// openai2gemini.convert_gemini_to_openai_response / convert_gemini_to_openai_stream）。
/// <para>
/// 上游响应（含 SSE 流块）统一包一层 {"response": {...}}（v1internal 封套），此处先解包再转候选内容。
/// usage 口径：input = promptTokenCount - cachedContentTokenCount（新输入，不含缓存）、
/// cached = cachedContentTokenCount、output = candidatesTokenCount + thoughtsTokenCount（思考 token 同样计费）。
/// </para>
/// </summary>
public static partial class ProxyProtocolBridge
{
    /// <summary>
    /// Gemini → Anthropic 流式转换状态（对齐 gcli2api gemini_stream_to_anthropic_stream 的局部变量组）。
    /// </summary>
    public sealed class GeminiToAnthropicStreamState
    {
        /// <summary>Anthropic 消息 ID。</summary>
        public string MessageId { get; } = $"msg_{Guid.NewGuid():N}";

        /// <summary>是否已发送 message_start。</summary>
        public bool MessageStartSent { get; set; }

        /// <summary>当前打开的内容块类型（thinking/text），null 表示无打开块。</summary>
        public string? CurrentBlockType { get; set; }

        /// <summary>当前打开块在 Anthropic 内容数组中的索引。</summary>
        public int CurrentBlockIndex { get; set; } = -1;

        /// <summary>当前 thinking 块携带的 thoughtSignature（签名变化时切块）。</summary>
        public string? CurrentThinkingSignature { get; set; }

        /// <summary>流中是否出现过工具调用。</summary>
        public bool HasToolUse { get; set; }

        /// <summary>上游 finishReason（首个非空值）。</summary>
        public string? FinishReason { get; set; }

        /// <summary>是否已输出收尾事件。</summary>
        public bool Completed { get; set; }

        /// <summary>输入 token（不含缓存）。</summary>
        public int InputTokens { get; set; }

        /// <summary>缓存命中 token。</summary>
        public int CachedTokens { get; set; }

        /// <summary>输出 token（candidates + thoughts）。</summary>
        public int OutputTokens { get; set; }

        /// <summary>candidates 输出 token，用于在部分 usageMetadata 到达时保留已知分量。</summary>
        public int CandidatesTokens { get; set; }

        /// <summary>thoughts 输出 token，用于在部分 usageMetadata 到达时保留已知分量。</summary>
        public int ThoughtsTokens { get; set; }
    }

    /// <summary>
    /// Gemini → OpenAI 流式转换共享的用量与完成状态。
    /// </summary>
    public sealed class GeminiToOpenAiStreamState
    {
        /// <summary>输入 token（不含缓存）。</summary>
        public int InputTokens { get; set; }

        /// <summary>缓存命中 token。</summary>
        public int CachedTokens { get; set; }

        /// <summary>输出 token（candidates + thoughts）。</summary>
        public int OutputTokens { get; set; }

        /// <summary>candidates 输出 token，用于在部分 usageMetadata 到达时保留已知分量。</summary>
        public int CandidatesTokens { get; set; }

        /// <summary>thoughts 输出 token，用于在部分 usageMetadata 到达时保留已知分量。</summary>
        public int ThoughtsTokens { get; set; }

        /// <summary>上游 finishReason（首个非空值）。</summary>
        public string? FinishReason { get; set; }

        /// <summary>是否已输出收尾块。</summary>
        public bool Completed { get; set; }

        /// <summary>下一个 tool_call 索引：跨块递增，避免不同块的工具调用共享 index 0 互相覆盖。</summary>
        public int NextToolCallIndex { get; set; }
    }

    /// <summary>
    /// 把 Gemini 非流式响应转换为 Anthropic Messages 非流式响应。
    /// 解析失败返回 null（调用方按转换失败处理，保留 fallback 机会）。
    /// </summary>
    public static string? BuildAnthropicResponseFromGemini(string geminiBody, string modelName)
    {
        try
        {
            var root = JsonNode.Parse(geminiBody) as JsonObject;
            if (root is null)
            {
                return null;
            }

            var response = UnwrapGeminiResponse(root);
            var candidate = (response["candidates"] as JsonArray)?[0] as JsonObject;
            var parts = (candidate?["content"] as JsonObject)?["parts"] as JsonArray;
            var usageMetadata = response["usageMetadata"] as JsonObject ?? candidate?["usageMetadata"] as JsonObject;

            var content = new JsonArray();
            var hasToolUse = false;
            if (parts is not null)
            {
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part)
                    {
                        continue;
                    }

                    if (part["thought"]?.GetValueKind() == JsonValueKind.True)
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        var thinkingBlock = new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = part["text"]?.GetValue<string>() ?? string.Empty
                        };
                        if (part["thoughtSignature"]?.GetValue<string>() is { Length: > 0 } signature
                            && !string.Equals(signature, SkipThoughtSignatureValidator, StringComparison.Ordinal))
                        {
                            thinkingBlock["thoughtSignature"] = signature;
                        }

                        content.Add(thinkingBlock);
                    }
                    else if (part.ContainsKey("text"))
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        content.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = part["text"]?.GetValue<string>() ?? string.Empty
                        });
                    }
                    else if (part["functionCall"] is JsonObject functionCall)
                    {
                        hasToolUse = true;
                        var id = functionCall["id"]?.GetValue<string>();
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = string.IsNullOrEmpty(id) ? $"toolu_{Guid.NewGuid():N}" : id,
                            ["name"] = functionCall["name"]?.GetValue<string>() ?? string.Empty,
                            ["input"] = RemoveNullFields(functionCall["args"]?.DeepClone() ?? new JsonObject())
                        });
                    }
                    else if (part["inlineData"] is JsonObject inline)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = inline["mimeType"]?.GetValue<string>() ?? "image/png",
                                ["data"] = inline["data"]?.GetValue<string>() ?? string.Empty
                            }
                        });
                    }
                }
            }

            if (content.Count == 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = string.Empty });
            }

            var usage = BuildAnthropicUsageFromGemini(usageMetadata);
            return new JsonObject
            {
                ["id"] = $"msg_{Guid.NewGuid():N}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = modelName,
                ["content"] = content,
                ["stop_reason"] = MapGeminiFinishToAnthropicStop(candidate?["finishReason"]?.GetValue<string>(), hasToolUse),
                ["stop_sequence"] = null,
                ["usage"] = usage
            }.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 Gemini 非流式响应转换为 OpenAI Chat Completions 非流式响应。
    /// </summary>
    public static string? BuildOpenAiResponseFromGemini(string geminiBody, string modelName)
    {
        try
        {
            var root = JsonNode.Parse(geminiBody) as JsonObject;
            if (root is null)
            {
                return null;
            }

            var response = UnwrapGeminiResponse(root);
            var candidate = (response["candidates"] as JsonArray)?[0] as JsonObject;
            var parts = (candidate?["content"] as JsonObject)?["parts"] as JsonArray;

            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            JsonArray? toolCalls = null;
            var hasToolUse = false;

            if (parts is not null)
            {
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part)
                    {
                        continue;
                    }

                    if (part["thought"]?.GetValueKind() == JsonValueKind.True)
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        if (reasoningBuilder.Length > 0)
                        {
                            reasoningBuilder.Append("\n\n");
                        }

                        reasoningBuilder.Append(part["text"]?.GetValue<string>() ?? string.Empty);
                    }
                    else if (part.ContainsKey("text"))
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        textBuilder.Append(part["text"]?.GetValue<string>() ?? string.Empty);
                    }
                    else if (part["functionCall"] is JsonObject functionCall)
                    {
                        hasToolUse = true;
                        toolCalls ??= new JsonArray();
                        var id = functionCall["id"]?.GetValue<string>();
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = string.IsNullOrEmpty(id) ? $"call_{Guid.NewGuid():N}" : id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = functionCall["name"]?.GetValue<string>() ?? string.Empty,
                                ["arguments"] = (functionCall["args"] as JsonObject ?? new JsonObject()).ToJsonString()
                            }
                        });
                    }
                    else if (part["inlineData"] is JsonObject inline)
                    {
                        var mime = inline["mimeType"]?.GetValue<string>() ?? "image/png";
                        textBuilder.Append($"![gemini-generated-content](data:{mime};base64,{inline["data"]?.GetValue<string>() ?? string.Empty})");
                    }
                }
            }

            var finishReasonGemini = candidate?["finishReason"]?.GetValue<string>();
            var message = new JsonObject { ["role"] = "assistant" };
            if (toolCalls is { Count: > 0 })
            {
                message["tool_calls"] = toolCalls;
                message["content"] = textBuilder.Length > 0 ? textBuilder.ToString() : null;
            }
            else
            {
                message["content"] = textBuilder.ToString();
            }

            if (reasoningBuilder.Length > 0)
            {
                message["reasoning_content"] = reasoningBuilder.ToString();
            }

            var finishReason = hasToolUse && string.Equals(finishReasonGemini, "STOP", StringComparison.OrdinalIgnoreCase)
                ? "tool_calls"
                : MapGeminiFinishToOpenAi(finishReasonGemini);

            var usageMetadata = response["usageMetadata"] as JsonObject;
            var payload = new JsonObject
            {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = modelName,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = finishReason
                })
            };

            if (usageMetadata is not null)
            {
                var usage = BuildAnthropicUsageFromGemini(usageMetadata);
                payload["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = usage["input_tokens"]?.DeepClone(),
                    ["completion_tokens"] = usage["output_tokens"]?.DeepClone(),
                    ["total_tokens"] = (usage["input_tokens"]?.GetValue<int>() ?? 0) + (usage["output_tokens"]?.GetValue<int>() ?? 0),
                    ["prompt_tokens_details"] = new JsonObject
                    {
                        ["cached_tokens"] = usage["cache_read_input_tokens"]?.DeepClone() ?? 0
                    }
                };
            }

            return payload.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 处理单个 Gemini SSE data 负载，转换为 Anthropic SSE 事件文本（可能 0..N 个事件）。
    /// 返回值可直接写回客户端；流结束时再调用 <see cref="CompleteGeminiToAnthropicStream"/> 补收尾事件。
    /// </summary>
    public static string ConvertGeminiSseChunkToAnthropic(string dataPayload, string modelName, GeminiToAnthropicStreamState state)
    {
        // builder 声明在 try 外：单块中途异常时已生成的事件仍可返回，避免客户端事件序列缺块。
        var builder = new StringBuilder();
        try
        {
            var root = JsonNode.Parse(dataPayload) as JsonObject;
            if (root is null)
            {
                return string.Empty;
            }

            var response = UnwrapGeminiResponse(root);
            var candidate = (response["candidates"] as JsonArray)?[0] as JsonObject;
            var parts = (candidate?["content"] as JsonObject)?["parts"] as JsonArray;

            if (response["usageMetadata"] is JsonObject usageMetadata)
            {
                UpdateGeminiUsage(
                    usageMetadata,
                    state.InputTokens,
                    state.CachedTokens,
                    state.CandidatesTokens,
                    state.ThoughtsTokens,
                    out var input,
                    out var cached,
                    out var candidates,
                    out var thoughts);
                state.InputTokens = input;
                state.CachedTokens = cached;
                state.CandidatesTokens = candidates;
                state.ThoughtsTokens = thoughts;
                state.OutputTokens = candidates + thoughts;
            }

            if (!state.MessageStartSent)
            {
                state.MessageStartSent = true;
                AppendSseEvent(builder, "message_start", new JsonObject
                {
                    ["type"] = "message_start",
                    ["message"] = new JsonObject
                    {
                        ["id"] = state.MessageId,
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["model"] = modelName,
                        ["content"] = new JsonArray(),
                        ["stop_reason"] = null,
                        ["stop_sequence"] = null,
                        ["usage"] = BuildAnthropicUsageObject(state.InputTokens, state.CachedTokens, state.OutputTokens)
                    }
                });
            }

            if (parts is not null)
            {
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part)
                    {
                        continue;
                    }

                    if (part["thought"]?.GetValueKind() == JsonValueKind.True)
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        var thinkingText = part["text"]?.GetValue<string>() ?? string.Empty;
                        var signature = part["thoughtSignature"]?.GetValue<string>();

                        if (state.CurrentBlockType != "thinking"
                            || (!string.IsNullOrEmpty(signature) && !string.Equals(signature, state.CurrentThinkingSignature, StringComparison.Ordinal)))
                        {
                            CloseCurrentAnthropicBlock(builder, state);
                            state.CurrentBlockIndex++;
                            state.CurrentBlockType = "thinking";
                            state.CurrentThinkingSignature = signature;
                            var block = new JsonObject { ["type"] = "thinking", ["thinking"] = string.Empty };
                            if (!string.IsNullOrEmpty(signature)
                                && !string.Equals(signature, SkipThoughtSignatureValidator, StringComparison.Ordinal))
                            {
                                block["thoughtSignature"] = signature;
                            }

                            AppendSseEvent(builder, "content_block_start", new JsonObject
                            {
                                ["type"] = "content_block_start",
                                ["index"] = state.CurrentBlockIndex,
                                ["content_block"] = block
                            });
                        }

                        if (thinkingText.Length > 0)
                        {
                            AppendSseEvent(builder, "content_block_delta", new JsonObject
                            {
                                ["type"] = "content_block_delta",
                                ["index"] = state.CurrentBlockIndex,
                                ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinkingText }
                            });
                        }

                        continue;
                    }

                    if (part.ContainsKey("text"))
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        var text = part["text"]?.GetValue<string>() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        if (state.CurrentBlockType != "text")
                        {
                            CloseCurrentAnthropicBlock(builder, state);
                            state.CurrentBlockIndex++;
                            state.CurrentBlockType = "text";
                            AppendSseEvent(builder, "content_block_start", new JsonObject
                            {
                                ["type"] = "content_block_start",
                                ["index"] = state.CurrentBlockIndex,
                                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = string.Empty }
                            });
                        }

                        AppendSseEvent(builder, "content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = state.CurrentBlockIndex,
                            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
                        });

                        continue;
                    }

                    if (part["functionCall"] is JsonObject functionCall)
                    {
                        CloseCurrentAnthropicBlock(builder, state);
                        state.HasToolUse = true;
                        state.CurrentBlockIndex++;
                        var id = functionCall["id"]?.GetValue<string>();
                        var toolId = string.IsNullOrEmpty(id) ? $"toolu_{Guid.NewGuid():N}" : id;

                        AppendSseEvent(builder, "content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start",
                            ["index"] = state.CurrentBlockIndex,
                            ["content_block"] = new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = toolId,
                                ["name"] = functionCall["name"]?.GetValue<string>() ?? string.Empty,
                                ["input"] = new JsonObject()
                            }
                        });

                        AppendSseEvent(builder, "content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = state.CurrentBlockIndex,
                            ["delta"] = new JsonObject
                            {
                                ["type"] = "input_json_delta",
                                ["partial_json"] = RemoveNullFields(functionCall["args"]?.DeepClone() ?? new JsonObject()).ToJsonString()
                            }
                        });

                        AppendSseEvent(builder, "content_block_stop", new JsonObject
                        {
                            ["type"] = "content_block_stop",
                            ["index"] = state.CurrentBlockIndex
                        });
                    }
                }
            }

            if (candidate?["finishReason"]?.GetValue<string>() is { Length: > 0 } finishReason)
            {
                state.FinishReason ??= finishReason;
            }

            return builder.ToString();
        }
        catch
        {
            // 单块部分事件已生成时照常返回，避免客户端事件序列缺块；完全失败才返回空。
            return builder.ToString();
        }
    }

    /// <summary>
    /// 输出 Gemini → Anthropic 流式转换的收尾事件（关闭当前块 + message_delta + message_stop）。幂等。
    /// </summary>
    public static string CompleteGeminiToAnthropicStream(GeminiToAnthropicStreamState state)
    {
        if (state.Completed)
        {
            return string.Empty;
        }

        state.Completed = true;
        var builder = new StringBuilder();

        if (!state.MessageStartSent)
        {
            // 空流兜底：确保客户端至少收到完整事件序列。
            state.MessageStartSent = true;
            AppendSseEvent(builder, "message_start", new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = state.MessageId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["stop_sequence"] = null,
                    ["usage"] = BuildAnthropicUsageObject(state.InputTokens, state.CachedTokens, state.OutputTokens)
                }
            });
        }

        CloseCurrentAnthropicBlock(builder, state);
        AppendSseEvent(builder, "message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject
            {
                ["stop_reason"] = MapGeminiFinishToAnthropicStop(state.FinishReason, state.HasToolUse),
                ["stop_sequence"] = null
            },
            ["usage"] = BuildAnthropicUsageObject(state.InputTokens, state.CachedTokens, state.OutputTokens)
        });
        AppendSseEvent(builder, "message_stop", new JsonObject { ["type"] = "message_stop" });

        return builder.ToString();
    }

    /// <summary>
    /// 把聚合的 Gemini SSE 文本整段转换为 Anthropic SSE 文本（非流式客户端消费流式上游时使用）。
    /// </summary>
    public static string? BuildAnthropicStreamingResponseFromGemini(string geminiSseText, string modelName)
    {
        var state = new GeminiToAnthropicStreamState();
        var builder = new StringBuilder();
        foreach (var payload in ExtractGeminiSseDataPayloads(geminiSseText))
        {
            builder.Append(ConvertGeminiSseChunkToAnthropic(payload, modelName, state));
        }

        builder.Append(CompleteGeminiToAnthropicStream(state));
        return builder.ToString();
    }

    /// <summary>
    /// 把聚合的 Gemini SSE 文本整段转换为 OpenAI Chat Completions SSE 文本。
    /// </summary>
    public static string? BuildOpenAiStreamingResponseFromGemini(string geminiSseText, string modelName)
    {
        var state = new GeminiToOpenAiStreamState();
        var responseId = $"chatcmpl-{Guid.NewGuid():N}";
        var builder = new StringBuilder();
        foreach (var payload in ExtractGeminiSseDataPayloads(geminiSseText))
        {
            var chunk = ConvertGeminiSseChunkToOpenAi(payload, modelName, responseId, state);
            if (chunk is not null)
            {
                builder.Append(chunk);
            }
        }

        var finish = CompleteGeminiToOpenAiStream(modelName, responseId, state);
        if (finish is not null)
        {
            builder.Append(finish);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 处理单个 Gemini SSE data 负载，转换为 OpenAI chat.completion.chunk SSE 文本。无可输出内容时返回 null。
    /// </summary>
    public static string? ConvertGeminiSseChunkToOpenAi(string dataPayload, string modelName, string responseId, GeminiToOpenAiStreamState state)
    {
        try
        {
            var root = JsonNode.Parse(dataPayload) as JsonObject;
            if (root is null)
            {
                return null;
            }

            var response = UnwrapGeminiResponse(root);
            var candidate = (response["candidates"] as JsonArray)?[0] as JsonObject;
            var parts = (candidate?["content"] as JsonObject)?["parts"] as JsonArray;

            if (response["usageMetadata"] is JsonObject usageMetadata)
            {
                UpdateGeminiUsage(
                    usageMetadata,
                    state.InputTokens,
                    state.CachedTokens,
                    state.CandidatesTokens,
                    state.ThoughtsTokens,
                    out var input,
                    out var cached,
                    out var candidates,
                    out var thoughts);
                state.InputTokens = input;
                state.CachedTokens = cached;
                state.CandidatesTokens = candidates;
                state.ThoughtsTokens = thoughts;
                state.OutputTokens = candidates + thoughts;
            }

            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            JsonArray? toolCalls = null;
            if (parts is not null)
            {
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part)
                    {
                        continue;
                    }

                    if (part["thought"]?.GetValueKind() == JsonValueKind.True)
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        reasoningBuilder.Append(part["text"]?.GetValue<string>() ?? string.Empty);
                    }
                    else if (part.ContainsKey("text"))
                    {
                        if (IsGeminiThoughtSignaturePlaceholder(part))
                        {
                            continue;
                        }

                        textBuilder.Append(part["text"]?.GetValue<string>() ?? string.Empty);
                    }
                    else if (part["functionCall"] is JsonObject functionCall)
                    {
                        toolCalls ??= new JsonArray();
                        var id = functionCall["id"]?.GetValue<string>();
                        toolCalls.Add(new JsonObject
                        {
                            ["index"] = state.NextToolCallIndex++,
                            ["id"] = string.IsNullOrEmpty(id) ? $"call_{Guid.NewGuid():N}" : id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = functionCall["name"]?.GetValue<string>() ?? string.Empty,
                                ["arguments"] = (functionCall["args"] as JsonObject ?? new JsonObject()).ToJsonString()
                            }
                        });
                    }
                }
            }

            if (candidate?["finishReason"]?.GetValue<string>() is { Length: > 0 } finishReason)
            {
                state.FinishReason ??= finishReason;
            }

            var hasDelta = textBuilder.Length > 0 || reasoningBuilder.Length > 0 || toolCalls is { Count: > 0 };
            if (!hasDelta)
            {
                return null;
            }

            var delta = new JsonObject();
            if (toolCalls is { Count: > 0 })
            {
                delta["tool_calls"] = toolCalls;
            }

            if (textBuilder.Length > 0)
            {
                delta["content"] = textBuilder.ToString();
            }

            if (reasoningBuilder.Length > 0)
            {
                delta["reasoning_content"] = reasoningBuilder.ToString();
            }

            var payload = new JsonObject
            {
                ["id"] = responseId,
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = modelName,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = null
                })
            };

            return $"data: {payload.ToJsonString()}\n\n";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 输出 Gemini → OpenAI 流式转换的收尾块（finish_reason + usage + [DONE]）。幂等。
    /// </summary>
    public static string? CompleteGeminiToOpenAiStream(string modelName, string responseId, GeminiToOpenAiStreamState state)
    {
        if (state.Completed)
        {
            return null;
        }

        state.Completed = true;
        var finishReason = MapGeminiFinishToOpenAi(state.FinishReason);
        var usage = new JsonObject
        {
            ["prompt_tokens"] = state.InputTokens,
            ["completion_tokens"] = state.OutputTokens,
            ["total_tokens"] = state.InputTokens + state.OutputTokens,
            ["prompt_tokens_details"] = new JsonObject
            {
                ["cached_tokens"] = state.CachedTokens
            }
        };
        var payload = new JsonObject
        {
            ["id"] = responseId,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = modelName,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject(),
                ["finish_reason"] = finishReason
            }),
            ["usage"] = usage
        };

        return $"data: {payload.ToJsonString()}\n\ndata: [DONE]\n\n";
    }

    // —— 公共辅助 ——

    /// <summary>
    /// 解包 v1internal 的 {"response": {...}} 封套；无封套时原样返回。
    /// </summary>
    public static JsonObject UnwrapGeminiResponse(JsonObject root)
        => root["response"] as JsonObject ?? root;

    /// <summary>
    /// 从聚合 SSE 文本中提取全部 data 负载（跳过 [DONE] 与非法行）。
    /// </summary>
    public static IEnumerable<string> ExtractGeminiSseDataPayloads(string sseText)
    {
        foreach (var line in sseText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = trimmed[5..].TrimStart();
            if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return payload;
        }
    }

    private static bool IsGeminiThoughtSignaturePlaceholder(JsonObject part)
    {
        if (!string.Equals(part["thoughtSignature"]?.GetValue<string>(), SkipThoughtSignatureValidator, StringComparison.Ordinal))
        {
            return false;
        }

        if (part.ContainsKey("functionCall") || part.ContainsKey("functionResponse"))
        {
            return false;
        }

        var text = part["text"]?.GetValue<string>()?.Trim();
        return text is "..." or "…";
    }

    private static void CloseCurrentAnthropicBlock(StringBuilder builder, GeminiToAnthropicStreamState state)
    {
        if (state.CurrentBlockType is null)
        {
            return;
        }

        AppendSseEvent(builder, "content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = state.CurrentBlockIndex
        });
        state.CurrentBlockType = null;
        state.CurrentThinkingSignature = null;
    }

    private static string MapGeminiFinishToAnthropicStop(string? finishReason, bool hasToolUse)
    {
        if (hasToolUse && string.Equals(finishReason, "STOP", StringComparison.OrdinalIgnoreCase))
        {
            return "tool_use";
        }

        return string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase) ? "max_tokens" : "end_turn";
    }

    private static string MapGeminiFinishToOpenAi(string? finishReason)
    {
        return finishReason?.ToUpperInvariant() switch
        {
            "MAX_TOKENS" => "length",
            "SAFETY" => "content_filter",
            "RECITATION" => "content_filter",
            _ => "stop"
        };
    }

    private static JsonObject BuildAnthropicUsageObject(int inputTokens, int cachedTokens, int outputTokens)
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens
        };
        if (cachedTokens > 0)
        {
            usage["cache_read_input_tokens"] = cachedTokens;
        }

        return usage;
    }

    private static JsonObject BuildAnthropicUsageFromGemini(JsonObject? usageMetadata)
    {
        UpdateGeminiUsage(
            usageMetadata,
            0,
            0,
            0,
            0,
            out var input,
            out var cached,
            out var candidates,
            out var thoughts);
        var output = candidates + thoughts;
        return BuildAnthropicUsageObject(input, cached, output);
    }

    /// <summary>
    /// 从 usageMetadata 合并用量（对新输入口径）：input = prompt - cached、output = candidates + thoughts。
    /// 上游流可能在不同块返回不完整字段；缺失字段必须保留上一块的值，不能把累计用量覆盖成 0。
    /// </summary>
    private static void UpdateGeminiUsage(
        JsonObject? usageMetadata,
        int currentInput,
        int currentCached,
        int currentCandidates,
        int currentThoughts,
        out int inputTokens,
        out int cachedTokens,
        out int candidatesTokens,
        out int thoughtsTokens)
    {
        inputTokens = currentInput;
        cachedTokens = currentCached;
        candidatesTokens = currentCandidates;
        thoughtsTokens = currentThoughts;
        if (usageMetadata is null)
        {
            return;
        }

        if (TryReadGeminiUsageInteger(usageMetadata, "promptTokenCount", "prompt_token_count", out var prompt))
        {
            inputTokens = Math.Max(0, prompt - cachedTokens);
        }

        if (TryReadGeminiUsageInteger(usageMetadata, "cachedContentTokenCount", "cached_content_token_count", out var cached))
        {
            cachedTokens = cached;
            if (TryReadGeminiUsageInteger(usageMetadata, "promptTokenCount", "prompt_token_count", out prompt))
            {
                inputTokens = Math.Max(0, prompt - cachedTokens);
            }
        }

        if (TryReadGeminiUsageInteger(usageMetadata, "candidatesTokenCount", "candidates_token_count", out var candidates))
        {
            candidatesTokens = candidates;
        }

        if (TryReadGeminiUsageInteger(usageMetadata, "thoughtsTokenCount", "thoughts_token_count", out var thoughts))
        {
            thoughtsTokens = thoughts;
        }
    }

    private static bool TryReadGeminiUsageInteger(JsonObject usageMetadata, string camelCaseName, string snakeCaseName, out int parsedValue)
    {
        if (!usageMetadata.TryGetPropertyValue(camelCaseName, out var node)
            && !usageMetadata.TryGetPropertyValue(snakeCaseName, out node))
        {
            parsedValue = 0;
            return false;
        }

        if (node is null)
        {
            parsedValue = 0;
            return false;
        }

        try
        {
            parsedValue = Math.Max(0, node.GetValue<int>());
            return true;
        }
        catch
        {
        }

        try
        {
            parsedValue = (int)Math.Clamp(node.GetValue<long>(), 0L, int.MaxValue);
            return true;
        }
        catch
        {
            try
            {
                if (int.TryParse(node.GetValue<string>(), out var parsed))
                {
                    parsedValue = Math.Max(0, parsed);
                    return true;
                }

                parsedValue = 0;
                return false;
            }
            catch
            {
                parsedValue = 0;
                return false;
            }
        }
    }

    /// <summary>
    /// 递归重建工具入参并移除其中的 null 字段/元素（Roo/Kilo 会把 null 当真实入参执行，对齐 gcli2api）。
    /// 重建过程中叶子节点必须克隆，避免"节点已有父级"异常。
    /// </summary>
    private static JsonNode RemoveNullFields(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var cleaned = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (value is null || value.GetValueKind() == JsonValueKind.Null)
                    {
                        continue;
                    }

                    cleaned[key] = RemoveNullFields(value);
                }

                return cleaned;
            }
            case JsonArray array:
            {
                var cleaned = new JsonArray();
                foreach (var item in array)
                {
                    if (item is null || item.GetValueKind() == JsonValueKind.Null)
                    {
                        continue;
                    }

                    cleaned.Add(RemoveNullFields(item));
                }

                return cleaned;
            }
            default:
                return node.DeepClone();
        }
    }
}
