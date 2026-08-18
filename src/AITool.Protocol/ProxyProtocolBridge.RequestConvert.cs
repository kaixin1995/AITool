using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITool.Protocol;

/// <summary>
/// 负责在 OpenAI 与 Anthropic 协议之间转换请求和响应内容。
/// </summary>
public static partial class ProxyProtocolBridge
{
    /// <summary>
    /// 将 Anthropic 请求体转换为 OpenAI 请求格式。
    /// </summary>
    private static string BuildOpenAiRequestFromAnthropic(JsonObject rootNode, string targetModelName, bool enableStreaming, bool keepReasoning = false)
    {
        var messages = new JsonArray();

        // 将 Anthropic 顶层 system 字段提取为 OpenAI 的第一条 system 消息。
        // 保留引用，后续 messages 数组里若混入额外的 role=system 条目（claude-code 新版会这么做），
        // 需要合并到这里，而不是原样追加——否则会出现 system 穿插在对话中间、甚至以 system 结尾，
        // 违反 OpenAI 规范（system 只能在最前，最后一条必须是 user），导致上游返回 1210。
        JsonObject? systemMessage = null;
        var systemNode = rootNode["system"];
        if (systemNode is not null)
        {
            var systemText = ExtractSystemContent(systemNode);
            if (!string.IsNullOrWhiteSpace(systemText))
            {
                systemMessage = new JsonObject { ["role"] = "system", ["content"] = systemText };
                messages.Add(systemMessage);
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
                    var (textContent, toolUseBlocks, imageBlocks, reasoningText) = ParseAnthropicContentBlocks(content);

                    // keep_reasoning 规则：deepseek 等上游在 thinking 模式 + 工具调用时，
                    // 要求把上一轮 assistant 的思维链以 reasoning_content 字段传回，否则返回 400。
                    // 标准 OpenAI 不认该字段，所以仅在绑定规则时保留。
                    if (keepReasoning && !string.IsNullOrWhiteSpace(reasoningText))
                    {
                        openAiMsg["reasoning_content"] = reasoningText;
                    }

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

                            // img 已是 userImageBlocks 的子节点（JsonNode 不允许双父），克隆后加入。
                            foreach (var img in userImageBlocks)
                            {
                                parts.Add(img?.DeepClone());
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

                // messages 数组里混入的 system 条目（claude-code 新版常见）：合并到开头的 system message，
                // 不能原样追加，否则会破坏 OpenAI 的 messages 顺序规范（system 只能在最前）。
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    // system 内容可能是文本数组，统一使用安全提取逻辑，避免把 JsonArray 当作 JsonValue 读取。
                    var extraSystemText = ExtractOpenAiContentAsString(content) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(extraSystemText))
                    {
                        if (systemMessage is null)
                        {
                            systemMessage = new JsonObject { ["role"] = "system", ["content"] = extraSystemText };
                            messages.Insert(0, systemMessage);
                        }
                        else
                        {
                            var existing = systemMessage["content"]?.GetValue<string>() ?? string.Empty;
                            systemMessage["content"] = existing + "\n\n" + extraSystemText;
                        }
                    }
                    continue;
                }

                // 其他角色直接复制
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

        // 注意：此处不再主动添加 stream_options.include_usage。
        // Anthropic 客户端（claude-code 等）经此转换后常发往 GLM 等 OpenAI 兼容端点，
        // 这些端点不支持 stream_options 字段，会返回 1210（API 调用参数有误）。
        // 同时这些端点在流式结束时本就会自带 usage，token 统计不依赖该字段
        // （见 OpenAiProxyController.Streaming 的 UpdateOpenAiUsageFromPayload）。

        CopyNodeIfPresent(rootNode, payload, "temperature");
        CopyNodeIfPresent(rootNode, payload, "top_p");
        // 注意：不透传 metadata。Anthropic 的 metadata（含 user_id 追踪标识）对模型调用无价值，
        // 且 z.ai/GLM 等 OpenAI 兼容端点不支持该字段，收到会返回 1210（API 调用参数有误）。
        // z.ai 官方 chat completions 字段清单不含 metadata。

        // tools 格式转换：Anthropic tools → OpenAI function tools
        ConvertAnthropicToolsToOpenAi(rootNode, payload);

        // tool_choice 格式转换：Anthropic → OpenAI
        ConvertAnthropicToolChoiceToOpenAi(rootNode, payload);

        // stop_sequences → stop
        if (rootNode["stop_sequences"] is not null)
        {
            payload["stop"] = rootNode["stop_sequences"]!.DeepClone();
        }

        // thinking / output_config → reasoning_effort 分级映射
        // 兼容三套写法（按优先级）：
        //   1) output_config.effort：claude-code 新版（thinking.type=adaptive）的标准载体
        //   2) thinking.type=adaptive：无 output_config 时降级为 high（自适应默认倾向较强思考）
        //   3) thinking.budget_tokens：老式 Anthropic 格式，向后兼容
        // thinking.type=disabled 时不输出 reasoning_effort，避免给不支持 none 的端点发非法值。
        var effort = ResolveEffortFromAnthropicThinking(rootNode);
        if (!string.IsNullOrEmpty(effort))
        {
            payload["reasoning_effort"] = effort;
        }

        return payload.ToJsonString();
    }

    /// <summary>
    /// 从 Anthropic 请求体解析出 reasoning effort 取值。
    /// 返回空字符串表示"不设置"（如显式 disabled）。
    /// 口径与 cc-switch resolve_reasoning_effort 对齐（由反向推导向量测试锁定）：
    /// 1) output_config.effort：low/medium/high 原样；max → xhigh（OpenAI xhigh = 最大档）；未知值忽略。
    /// 2) thinking.type=adaptive → xhigh；enabled+budget：<4000 → low、<16000 → medium、≥16000 → high；
    ///    enabled 无 budget → high；disabled/缺失 → 不设置。
    /// </summary>
    private static string ResolveEffortFromAnthropicThinking(JsonObject rootNode)
    {
        // 1) output_config.effort（新版标准，最高优先级）
        if (rootNode["output_config"] is JsonObject outputConfig &&
            outputConfig["effort"] is JsonValue effortValue &&
            effortValue.TryGetValue<string>(out var rawEffort))
        {
            var normalized = rawEffort.Trim().ToLowerInvariant();
            return normalized switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "max" => "xhigh", // OpenAI xhigh = maximum reasoning effort
                _ => string.Empty // 未知值不注入
            };
        }

        // 2) thinking 对象
        if (rootNode["thinking"] is JsonObject thinkingObj)
        {
            var type = thinkingObj["type"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? string.Empty;

            // 显式关闭：不输出 reasoning_effort
            if (string.Equals(type, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // 自适应模式：最大推理档
            if (string.Equals(type, "adaptive", StringComparison.OrdinalIgnoreCase))
            {
                return "xhigh";
            }

            // 老式 budget_tokens 映射（enabled 或未带 type）
            var budgetTokens = thinkingObj["budget_tokens"]?.GetValue<long>() ?? 0;
            if (budgetTokens > 0)
            {
                return budgetTokens switch
                {
                    < 4_000 => "low",
                    < 16_000 => "medium",
                    _ => "high"
                };
            }

            if (string.Equals(type, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                return "high"; // enabled 但无 budget——保守取强推理
            }
        }

        return string.Empty;
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
