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

        if (delta.TryGetProperty("content", out contentElement) && contentElement.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in contentElement.EnumerateArray())
            {
                var text = ExtractElementText(item, "text", "content");
                if (text is not null)
                {
                    parts.Add(text);
                }
            }

            if (parts.Count > 0)
            {
                return string.Join("\n", parts);
            }
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
