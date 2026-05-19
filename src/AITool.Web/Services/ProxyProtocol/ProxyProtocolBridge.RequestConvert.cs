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
}
