using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Conversations;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

/// <summary>
/// 提供 OpenAI 代理入口复用的 WebSocket、SSE、日志和追踪辅助逻辑。
/// </summary>
public sealed partial class OpenAiProxyController
{
    /// <summary>
    /// 接收一条完整的 WebSocket 文本消息。
    /// </summary>
    /// <param name="webSocket">已经建立的下游 WebSocket 连接。</param>
    /// <param name="cancellationToken">用于中断接收过程的取消令牌。</param>
    /// <returns>返回完整文本消息；如果客户端关闭连接则返回空值。</returns>
    private static async Task<string?> ReceiveWebSocketTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var builder = new StringBuilder();
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return builder.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// 归一化 Responses WebSocket 请求，兼容 response.create 与 response.append。
    /// </summary>
    /// <param name="rawRequestJson">客户端当前发送的原始 WebSocket JSON 文本。</param>
    /// <param name="lastRequestJson">上一轮已经归一化的 Responses 请求 JSON。</param>
    /// <param name="lastResponseOutputJson">上一轮 response.completed 中的 output 数组 JSON。</param>
    /// <param name="normalizedRequestJson">输出可直接转发到上游 Responses 链路的 JSON 请求体。</param>
    /// <param name="errorMessage">输出归一化失败时的错误说明。</param>
    /// <returns>如果请求可以归一化并继续处理则返回 true。</returns>
    private static bool TryNormalizeResponsesWebSocketRequest(
        string rawRequestJson,
        string lastRequestJson,
        string lastResponseOutputJson,
        out string? normalizedRequestJson,
        out string? errorMessage)
    {
        normalizedRequestJson = null;
        errorMessage = null;
        try
        {
            var root = JsonNode.Parse(rawRequestJson) as JsonObject;
            if (root is null)
            {
                errorMessage = "WebSocket 请求体格式无效，请检查是否为合法的 JSON";
                return false;
            }

            var requestType = root["type"]?.GetValue<string>()?.Trim();
            if (!string.Equals(requestType, "response.create", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requestType, "response.append", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = $"unsupported websocket request type: {requestType}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(lastRequestJson))
            {
                // 首轮请求必须自带 model，后续 append 才能继承上一轮上下文。
                root.Remove("type");
                root["stream"] = true;
                root["input"] ??= new JsonArray();
                var modelName = root["model"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    errorMessage = "missing model in response.create request";
                    return false;
                }

                normalizedRequestJson = root.ToJsonString();
                return true;
            }

            var lastRoot = JsonNode.Parse(lastRequestJson) as JsonObject;
            if (lastRoot is null)
            {
                errorMessage = "invalid previous websocket request state";
                return false;
            }

            var nextInput = root["input"] as JsonArray;
            if (nextInput is null)
            {
                errorMessage = "websocket request requires array field: input";
                return false;
            }

            root.Remove("type");
            root.Remove("previous_response_id");
            root["stream"] = true;
            root["model"] ??= lastRoot["model"]?.DeepClone();
            root["instructions"] ??= lastRoot["instructions"]?.DeepClone();

            if (ShouldReplaceResponsesWebSocketTranscript(root, nextInput))
            {
                normalizedRequestJson = root.ToJsonString();
                return true;
            }

            // append 默认只带新增输入，这里补回上一轮请求和上一轮输出，维持连续会话。
            var mergedInput = new JsonArray();
            if (lastRoot["input"] is JsonArray lastInput)
            {
                foreach (var item in lastInput)
                {
                    mergedInput.Add(item?.DeepClone());
                }
            }

            if (JsonNode.Parse(lastResponseOutputJson) is JsonArray lastOutput)
            {
                foreach (var item in lastOutput)
                {
                    mergedInput.Add(item?.DeepClone());
                }
            }

            foreach (var item in nextInput)
            {
                mergedInput.Add(item?.DeepClone());
            }

            root["input"] = DeduplicateResponsesWebSocketInputItems(mergedInput);
            normalizedRequestJson = root.ToJsonString();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 判断当前 WebSocket 请求是否已经携带完整转录内容，从而替换本地缓存上下文。
    /// </summary>
    /// <param name="requestRoot">当前已经移除 WebSocket 类型字段的请求根对象。</param>
    /// <param name="nextInput">当前请求携带的 input 数组。</param>
    /// <returns>如果当前 input 已经代表完整上下文则返回 true。</returns>
    private static bool ShouldReplaceResponsesWebSocketTranscript(JsonObject requestRoot, JsonArray nextInput)
    {
        var previousResponseId = requestRoot["previous_response_id"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(previousResponseId))
        {
            return false;
        }

        foreach (var item in nextInput)
        {
            if (item is not JsonObject itemObject)
            {
                continue;
            }

            var itemType = itemObject["type"]?.GetValue<string>()?.Trim();
            if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "custom_tool_call", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "compaction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "compaction_summary", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase))
            {
                var role = itemObject["role"]?.GetValue<string>()?.Trim();
                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 按 input item 的 id 去重，保留最后一次出现的同 id 项。
    /// </summary>
    /// <param name="source">合并上一轮请求、上一轮输出和本轮输入后的 input 数组。</param>
    /// <returns>返回按 id 去重后的 input 数组。</returns>
    private static JsonArray DeduplicateResponsesWebSocketInputItems(JsonArray source)
    {
        // 先记录每个 id 最后出现的位置，后面按原顺序过滤旧项。
        var lastIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < source.Count; index++)
        {
            if (source[index] is not JsonObject itemObject)
            {
                continue;
            }

            var itemId = itemObject["id"]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                lastIndexById[itemId] = index;
            }
        }

        var deduped = new JsonArray();
        for (var index = 0; index < source.Count; index++)
        {
            var node = source[index];
            if (node is not JsonObject itemObject)
            {
                deduped.Add(node?.DeepClone());
                continue;
            }

            var itemId = itemObject["id"]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(itemId) && lastIndexById[itemId] != index)
            {
                continue;
            }

            deduped.Add(node.DeepClone());
        }

        return deduped;
    }

    /// <summary>
    /// 从 Responses SSE 文本中提取适合发送给 WebSocket 客户端的 JSON 消息。
    /// </summary>
    /// <param name="sseText">Responses SSE 文本，可以包含多个事件块。</param>
    /// <returns>返回可直接写入 WebSocket 的 JSON 负载列表。</returns>
    private static List<string> ExtractWebSocketJsonPayloadsFromSseText(string sseText)
    {
        var payloads = new List<string>();
        if (string.IsNullOrWhiteSpace(sseText))
        {
            return payloads;
        }

        var blocks = sseText.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!TryExtractSseDataPayload(lines, out var payload))
            {
                continue;
            }

            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payload))
            {
                // WebSocket 客户端只需要 JSON 事件，SSE 结束标记不作为消息发送。
                continue;
            }

            payloads.Add(payload);
        }

        return payloads;
    }

    /// <summary>
    /// 从 Responses 完成事件中提取 response.output 数组，供下一轮 append 合并上下文。
    /// </summary>
    /// <param name="payload">单个 Responses SSE data 负载 JSON。</param>
    /// <param name="outputJson">输出 response.completed 中的 response.output 数组 JSON。</param>
    /// <returns>如果成功提取 output 数组则返回 true。</returns>
    private static bool TryExtractResponsesCompletedOutput(string payload, out string outputJson)
    {
        outputJson = "[]";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // response.completed 的 output 是下一轮 append 合并 assistant 历史的唯一可靠来源。
            if (root.TryGetProperty("response", out var responseElement)
                && responseElement.TryGetProperty("output", out var outputElement)
                && outputElement.ValueKind == JsonValueKind.Array)
            {
                outputJson = outputElement.GetRawText();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// 发送单条 WebSocket JSON 消息。
    /// </summary>
    /// <param name="webSocket">已经建立的下游 WebSocket 连接。</param>
    /// <param name="payload">准备发送给客户端的 JSON 文本。</param>
    /// <param name="cancellationToken">用于中断发送过程的取消令牌。</param>
    /// <returns>返回 WebSocket 发送操作的异步任务。</returns>
    private static Task SendWebSocketJsonPayloadAsync(WebSocket webSocket, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    /// <summary>
    /// 向 Responses WebSocket 客户端返回统一错误消息。
    /// </summary>
    /// <param name="webSocket">已经建立的下游 WebSocket 连接。</param>
    /// <param name="statusCode">需要回传给客户端的 HTTP 语义状态码。</param>
    /// <param name="message">错误消息文本。</param>
    /// <param name="cancellationToken">用于中断发送过程的取消令牌。</param>
    /// <returns>返回 WebSocket 错误消息发送操作的异步任务。</returns>
    private static Task WriteResponsesWebSocketErrorAsync(WebSocket webSocket, int statusCode, string message, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["type"] = "error",
            ["status"] = statusCode,
            ["error"] = new JsonObject
            {
                ["type"] = "server_error",
                ["message"] = string.IsNullOrWhiteSpace(message) ? "Unknown websocket error" : message
            }
        }.ToJsonString();

        return SendWebSocketJsonPayloadAsync(webSocket, payload, cancellationToken);
    }

    /// <summary>
    /// 根据显式来源标记和 User-Agent 推断请求来源。
    /// </summary>
    /// <param name="request">当前代理入口收到的 HTTP 请求。</param>
    /// <returns>返回用于日志和追踪的请求来源标识。</returns>
    private static string ResolveRequestSource(HttpRequest request)
    {
        var explicitSource = request.Headers.TryGetValue("X-AITool-Source", out var sourceHeader)
            ? sourceHeader.ToString().Trim()
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(explicitSource))
        {
            return explicitSource;
        }

        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "proxy";
        }

        var normalizedUserAgent = userAgent.ToLowerInvariant();
        if (normalizedUserAgent.Contains("claude"))
        {
            return "claude-code";
        }

        if (normalizedUserAgent.Contains("codex"))
        {
            return "codex";
        }

        if (normalizedUserAgent.Contains("open-code") || normalizedUserAgent.Contains("opencode"))
        {
            return "open-code";
        }

        if (normalizedUserAgent.Contains("zcode"))
        {
            return "zcode";
        }

        return "proxy";
    }

    /// <summary>
    /// 从一组 SSE 行中提取合并后的 data 负载。
    /// </summary>
    /// <param name="sseLines">同一个 SSE 事件块内的原始文本行。</param>
    /// <param name="payload">输出合并后的 data 负载文本。</param>
    /// <returns>如果当前事件块包含 data 行则返回 true。</returns>
    private static bool TryExtractSseDataPayload(List<string> sseLines, out string payload)
    {
        payload = string.Empty;
        if (sseLines.Count == 0)
        {
            return false;
        }

        var dataLines = new List<string>();
        foreach (var line in sseLines)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line.Length > 5 ? line[5..] : string.Empty;
            if (data.StartsWith(' '))
            {
                data = data[1..];
            }

            dataLines.Add(data);
        }

        if (dataLines.Count == 0)
        {
            return false;
        }

        payload = string.Join("\n", dataLines);
        return true;
    }

    /// <summary>
    /// 从一组 Anthropic SSE 行中提取事件名和 data 负载。
    /// </summary>
    /// <param name="sseLines">同一个 Anthropic SSE 事件块内的原始文本行。</param>
    /// <param name="eventName">输出 event 行中的事件名称。</param>
    /// <param name="payload">输出合并后的 data 负载文本。</param>
    /// <returns>如果当前事件块包含 data 行则返回 true。</returns>
    private static bool TryExtractSseEventPayload(List<string> sseLines, out string eventName, out string payload)
    {
        eventName = string.Empty;
        payload = string.Empty;
        if (sseLines.Count == 0)
        {
            return false;
        }

        var dataLines = new List<string>();
        foreach (var line in sseLines)
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line.Length > 6 ? line[6..].Trim() : string.Empty;
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line.Length > 5 ? line[5..] : string.Empty;
            if (data.StartsWith(' '))
            {
                data = data[1..];
            }

            dataLines.Add(data);
        }

        if (dataLines.Count == 0)
        {
            return false;
        }

        payload = string.Join("\n", dataLines);
        return true;
    }

    /// <summary>
    /// 从 OpenAI 流式负载中刷新当前流的 token 统计。
    /// </summary>
    /// <param name="jsonText">单个 OpenAI 或 Responses SSE data 负载 JSON。</param>
    /// <param name="inputTokens">保存提取到的输入 token 数。</param>
    /// <param name="cachedTokens">保存提取到的缓存命中 token 数。</param>
    /// <param name="outputTokens">保存提取到的输出 token 数。</param>
    private static void UpdateOpenAiUsageFromPayload(string jsonText, ref int inputTokens, ref int cachedTokens, ref int outputTokens)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;
            var usage = root.TryGetProperty("usage", out var topLevelUsage)
                ? topLevelUsage
                : root.TryGetProperty("response", out var responseElement) && responseElement.TryGetProperty("usage", out var nestedUsage)
                    ? nestedUsage
                    : default;
            if (usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (usage.TryGetProperty("input_tokens", out var inputTokenElement) && inputTokenElement.ValueKind == JsonValueKind.Number)
            {
                inputTokens = inputTokenElement.GetInt32();
            }
            else if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.ValueKind == JsonValueKind.Number)
            {
                inputTokens = promptTokens.GetInt32();
            }

            if (usage.TryGetProperty("output_tokens", out var outputTokenElement) && outputTokenElement.ValueKind == JsonValueKind.Number)
            {
                outputTokens = outputTokenElement.GetInt32();
            }
            else if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.ValueKind == JsonValueKind.Number)
            {
                outputTokens = completionTokens.GetInt32();
            }

            // OpenAI Chat Completions 与 Responses 的缓存字段名称不同，流式统计同时兼容两种格式。
            if (usage.TryGetProperty("input_tokens_details", out var inputTokenDetails) &&
                inputTokenDetails.TryGetProperty("cached_tokens", out var inputCachedTokenElement) &&
                inputCachedTokenElement.ValueKind == JsonValueKind.Number)
            {
                cachedTokens = inputCachedTokenElement.GetInt32();
            }
            else if (usage.TryGetProperty("prompt_tokens_details", out var promptTokenDetails) &&
                     promptTokenDetails.TryGetProperty("cached_tokens", out var cachedTokenElement) &&
                     cachedTokenElement.ValueKind == JsonValueKind.Number)
            {
                cachedTokens = cachedTokenElement.GetInt32();
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 从 Anthropic 用量对象中刷新当前转换状态的 token 统计。
    /// </summary>
    /// <param name="usage">Anthropic 响应事件中的 usage JSON 对象。</param>
    /// <param name="state">正在转换为 OpenAI SSE 的流式状态对象。</param>
    private static void UpdateAnthropicUsageFromElement(JsonElement usage, AnthropicToOpenAiStreamState state)
    {
        if (usage.TryGetProperty("input_tokens", out var inputTokens) && inputTokens.ValueKind == JsonValueKind.Number)
        {
            state.InputTokens = inputTokens.GetInt32();
        }

        if (usage.TryGetProperty("cache_read_input_tokens", out var cachedTokens) && cachedTokens.ValueKind == JsonValueKind.Number)
        {
            state.CachedTokens = cachedTokens.GetInt32();
        }

        if (usage.TryGetProperty("cache_creation_input_tokens", out var cacheCreationTokens) && cacheCreationTokens.ValueKind == JsonValueKind.Number)
        {
            state.CacheCreationTokens = cacheCreationTokens.GetInt32();
        }

        if (usage.TryGetProperty("output_tokens", out var outputTokens) && outputTokens.ValueKind == JsonValueKind.Number)
        {
            state.OutputTokens = outputTokens.GetInt32();
        }
    }

    /// <summary>
    /// 构造一个只包含 delta 内容的 OpenAI SSE 块。
    /// </summary>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="deltaObject">需要写入 choices.delta 的 JSON 对象。</param>
    /// <returns>返回完整的 OpenAI SSE data 块文本。</returns>
    private static string BuildOpenAiDeltaChunk(string modelName, JsonObject deltaObject)
    {
        return BuildOpenAiChunkCore(modelName, deltaObject, null, null);
    }

    /// <summary>
    /// 构造一个包含 tool_calls 增量内容的 OpenAI SSE 块。
    /// </summary>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="toolCalls">需要写入 choices.delta.tool_calls 的工具调用数组。</param>
    /// <returns>返回完整的 OpenAI SSE data 块文本。</returns>
    private static string BuildOpenAiToolCallChunk(string modelName, JsonArray toolCalls)
    {
        return BuildOpenAiChunkCore(modelName, new JsonObject
        {
            ["tool_calls"] = toolCalls
        }, null, null);
    }

    /// <summary>
    /// 构造带有结束原因和用量统计的 OpenAI 收尾 SSE 块。
    /// </summary>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="finishReason">OpenAI choices.finish_reason 字段值。</param>
    /// <param name="inputTokens">本次请求累计输入 token 数。</param>
    /// <param name="cachedTokens">本次请求缓存命中的输入 token 数。</param>
    /// <param name="cacheCreationTokens">本次请求新建缓存消耗的输入 token 数。</param>
    /// <param name="outputTokens">本次请求累计输出 token 数。</param>
    /// <returns>返回带 usage 的 OpenAI SSE 收尾 data 块文本。</returns>
    private static string BuildOpenAiFinishChunk(
        string modelName,
        string finishReason,
        int inputTokens,
        int cachedTokens,
        int cacheCreationTokens,
        int outputTokens)
    {
        return BuildOpenAiChunkCore(
            modelName,
            new JsonObject(),
            finishReason,
            new JsonObject
            {
                ["prompt_tokens"] = inputTokens,
                ["prompt_tokens_details"] = new JsonObject
                {
                    ["cached_tokens"] = cachedTokens,
                    ["cached_creation_tokens"] = cacheCreationTokens
                },
                ["completion_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens
            });
    }

    /// <summary>
    /// 按 OpenAI chat.completion.chunk 结构拼装通用 SSE 负载。
    /// </summary>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="deltaObject">需要写入 choices.delta 的 JSON 对象。</param>
    /// <param name="finishReason">可选的 OpenAI choices.finish_reason 字段值。</param>
    /// <param name="usage">可选的 OpenAI usage JSON 对象。</param>
    /// <returns>返回完整的 OpenAI SSE data 块文本。</returns>
    private static string BuildOpenAiChunkCore(string modelName, JsonObject deltaObject, string? finishReason, JsonObject? usage)
    {
        var payload = new JsonObject
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
                    ["finish_reason"] = finishReason is null ? null : JsonValue.Create(finishReason)
                }
            }
        };

        if (usage is not null)
        {
            payload["usage"] = usage;
        }

        return $"data: {payload.ToJsonString()}\n\n";
    }

    /// <summary>
    /// 将 Anthropic 的停止原因映射成 OpenAI 的 finish_reason。
    /// </summary>
    /// <param name="stopReason">Anthropic message_delta.stop_reason 字段值。</param>
    /// <returns>返回 OpenAI 兼容的 finish_reason 字段值。</returns>
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
    /// 在开发者追踪开启时创建一次请求级追踪记录。
    /// </summary>
    private Guid? TryCreateDeveloperTrace(CachedProxyRuntimeSettings runtimeSettings, string requestSource, string protocolType, string modelName, string requestBody)
    {
        if (!runtimeSettings.DeveloperFeaturesEnabled)
        {
            return null;
        }

        return _traceStore.AddRequest(new DeveloperInvocationTraceRequest
        {
            RequestId = Guid.NewGuid(),
            Source = requestSource,
            UserAgent = Request.Headers.UserAgent.ToString(),
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            ProtocolType = protocolType,
            RequestPath = Request.Path,
            RequestModel = modelName,
            RequestBody = DeveloperInvocationTraceStore.FormatBody(requestBody),
            RequestHeaders = DeveloperInvocationTraceStore.CaptureHeaders(Request.Headers)
        });
    }

    /// <summary>
    /// 安全地创建开发者追踪，避免追踪失败影响正常代理。
    /// </summary>
    private Guid? TryCreateDeveloperTraceSafely(CachedProxyRuntimeSettings runtimeSettings, string requestSource, string protocolType, string modelName, string requestBody)
    {
        try
        {
            return TryCreateDeveloperTrace(runtimeSettings, requestSource, protocolType, modelName, requestBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "创建开发者调用追踪失败，但请求继续转发。Protocol={Protocol}, RequestModel={RequestModel}",
                protocolType,
                modelName);
            return null;
        }
    }

    /// <summary>
    /// 为当前追踪追加一次路由尝试记录。
    /// </summary>
    private Guid AddDeveloperTraceAttempt(Guid? traceId, CachedProxyRouteTarget route, string actualProtocolType)
    {
        if (!traceId.HasValue)
        {
            return Guid.Empty;
        }

        return _traceStore.AddAttempt(traceId.Value, new DeveloperInvocationAttempt
        {
            AttemptedModel = route.UpstreamModelName,
            UpstreamProtocolType = actualProtocolType,
            ForwardingMode = ResolveForwardingMode("OpenAI", actualProtocolType),
            TargetSiteId = route.SiteId,
            TargetSiteName = route.SiteName
        });
    }

    /// <summary>
    /// 安全地记录一次路由尝试，避免追踪异常中断主流程。
    /// </summary>
    private Guid AddDeveloperTraceAttemptSafely(Guid? traceId, CachedProxyRouteTarget route, string actualProtocolType)
    {
        try
        {
            return AddDeveloperTraceAttempt(traceId, route, actualProtocolType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "创建开发者调用追踪尝试失败，但请求继续转发。RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                route.ExternalModelName,
                route.UpstreamModelName);
            return Guid.Empty;
        }
    }

    /// <summary>
    /// 根据客户端协议和上游协议判断当前是直连还是兼容转发。
    /// </summary>
    private static string ResolveForwardingMode(string clientProtocolType, string upstreamProtocolType)
    {
        return string.Equals(clientProtocolType, upstreamProtocolType, StringComparison.OrdinalIgnoreCase)
            ? "direct"
            : "bridge";
    }

    /// <summary>
    /// 从代理请求体中提取思考等级，兼容不同客户端协议的字段命名。
    /// </summary>
    private static string ResolveReasoningEffort(JsonElement rootElement)
    {
        if (TryGetNormalizedString(rootElement, "reasoning_effort", out var directEffort))
        {
            return directEffort;
        }

        if (TryGetNormalizedString(rootElement, "effort", out var effort))
        {
            return effort;
        }

        if (rootElement.TryGetProperty("reasoning", out var reasoningElement) &&
            reasoningElement.ValueKind == JsonValueKind.Object &&
            TryGetNormalizedString(reasoningElement, "effort", out var nestedEffort))
        {
            return nestedEffort;
        }

        if (rootElement.TryGetProperty("output_config", out var outputConfigElement) &&
            outputConfigElement.ValueKind == JsonValueKind.Object &&
            TryGetNormalizedString(outputConfigElement, "effort", out var outputConfigEffort))
        {
            return outputConfigEffort;
        }

        if (rootElement.TryGetProperty("thinking", out var thinkingElement) &&
            thinkingElement.ValueKind == JsonValueKind.Object &&
            thinkingElement.TryGetProperty("budget_tokens", out var budgetTokensElement) &&
            budgetTokensElement.TryGetInt32(out var budgetTokens))
        {
            return budgetTokens switch
            {
                <= 1280 => "low",
                <= 2048 => "medium",
                _ => "high"
            };
        }

        return string.Empty;
    }

    /// <summary>
    /// 读取并规范化请求体中的字符串字段。
    /// </summary>
    private static bool TryGetNormalizedString(JsonElement rootElement, string propertyName, out string value)
    {
        value = string.Empty;
        if (!rootElement.TryGetProperty(propertyName, out var propertyElement) || propertyElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var rawValue = propertyElement.GetString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        value = rawValue;
        return true;
    }

    /// <summary>
    /// 安全地写入用量日志，记录失败时不影响响应返回。
    /// </summary>
    private async Task SafeLogUsageAsync(UsageLogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _usageLogService.LogAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "记录使用日志失败，但请求继续返回。Protocol={Protocol}, RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                entry.ProtocolType,
                entry.RequestModel,
                entry.AttemptedModel);
        }
    }

    /// <summary>
    /// 安全地写入结构化对话记录，失败时不影响主链路。
    /// 有会话标识的工具（claude-code / codex / open-code）按会话分组，
    /// 无会话标识的普通代理请求合并到同一个分组。
    /// </summary>
    /// <param name="assistantContent">流式转发时实时累积的 AI 正文（不受 64KB 诊断副本限制）；非流式或为空时回退到从 <paramref name="responseBody"/> 提取。</param>
    private async Task SafeLogConversationAsync(Guid requestId, Guid accessKeyId, string protocolType, string requestSource, string requestBody, string responseBody, string requestModel, bool isStreaming, string status, int inputTokens, int cachedTokens, int outputTokens, DateTimeOffset requestedAt, CancellationToken cancellationToken, string assistantContent = "")
    {
        try
        {
            var headers = CaptureRequestHeaders();
            var sourceTool = _conversationExtractionService.ResolveSourceTool(
                headers.TryGetValue("X-AITool-Source", out var explicitSource) ? explicitSource : string.Empty,
                Request.Headers.UserAgent.ToString());

            var sessionId = _conversationExtractionService.ExtractSessionId(headers);

            // 合并为一次 JsonDocument.Parse，避免对几 MB 的 requestBody 解析两次导致两份 LOH 副本。
            var (userInput, toolResultOutput) = _conversationExtractionService.ExtractRequestConversationFields(requestBody, protocolType, Request.Path);
            // 优先用流式转发时实时累积的 AI 正文（完整捕获，不受 64KB 诊断副本限制）；
            // 为空时回退到从 responseBody 提取（非流式或兜底场景）。
            var assistantText = !string.IsNullOrWhiteSpace(assistantContent)
                ? assistantContent
                : _conversationExtractionService.ExtractAssistantOutput(responseBody, protocolType, Request.Path);
            var assistantOutputMarkdown = JoinConversationMarkdown(toolResultOutput, assistantText);
            if (string.IsNullOrWhiteSpace(userInput) && string.IsNullOrWhiteSpace(assistantOutputMarkdown))
            {
                return;
            }

            // 有 sessionId 的按 sourceTool:sessionId 分组，无 sessionId 的合并到 sourceTool 这一组。
            var groupKey = !string.IsNullOrWhiteSpace(sessionId)
                ? $"{sourceTool}:{sessionId}"
                : sourceTool;

            await _conversationLogService.LogAsync(new ConversationTurnEntry
            {
                RequestId = requestId,
                CreatedAt = DateTimeOffset.UtcNow,
                UserCreatedAt = requestedAt,
                SourceTool = sourceTool,
                SessionId = sessionId,
                ConversationGroupKey = groupKey,
                AccessKeyId = accessKeyId,
                RequestModel = requestModel,
                ProtocolType = protocolType,
                RequestPath = Request.Path,
                Source = requestSource,
                UserInputText = userInput,
                AssistantOutputMarkdown = assistantOutputMarkdown,
                InputTokens = inputTokens,
                CachedTokens = cachedTokens,
                OutputTokens = outputTokens,
                IsStreaming = isStreaming,
                Status = status,
                MetadataJson = _conversationExtractionService.BuildMetadataJson(
                    Request.Headers.UserAgent.ToString(),
                    headers.TryGetValue("x-app", out var xApp) ? xApp : string.Empty,
                    sessionId)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "记录结构化对话失败，但请求继续返回。Protocol={Protocol}, RequestModel={RequestModel}",
                protocolType,
                requestModel);
        }
    }

    /// <summary>
    /// 合并工具结果和模型回复，避免展示时内容粘连。
    /// </summary>
    private static string JoinConversationMarkdown(params string[] values)
    {
        return string.Join("\n\n", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
    }

    /// <summary>
    /// 复制当前请求头为普通字典，避免跨层依赖 ASP.NET Core 类型。
    /// </summary>
    private Dictionary<string, string> CaptureRequestHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in Request.Headers)
        {
            headers[header.Key] = header.Value.ToString();
        }

        return headers;
    }

    /// <summary>
    /// 安全地读取路由熔断状态。
    /// </summary>
    private bool IsRouteBlockedSafely(Guid routeId)
    {
        try
        {
            return _circuitStore.IsBlocked(routeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "读取熔断状态失败，按未熔断继续转发。RouteId={RouteId}",
                routeId);
            return false;
        }
    }

    /// <summary>
    /// 安全地标记路由调用成功。
    /// </summary>
    private void SafeSucceedRoute(Guid routeId)
    {
        try
        {
            _circuitStore.Succeed(routeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "更新路由成功状态失败，但请求继续返回。RouteId={RouteId}",
                routeId);
        }
    }

    /// <summary>
    /// 安全地累计路由失败状态。
    /// </summary>
    private void SafeBlockRoute(Guid routeId)
    {
        try
        {
            _circuitStore.Block(routeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "更新路由失败状态失败，但继续尝试后续路由。RouteId={RouteId}",
                routeId);
        }
    }

    /// <summary>
    /// 安全地补全一次开发者追踪尝试记录。
    /// </summary>
    private void SafeCompleteDeveloperTraceAttempt(Guid? traceId, Guid traceAttemptId, DeveloperInvocationResult result)
    {
        try
        {
            CompleteDeveloperTraceAttempt(traceId, traceAttemptId, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "完成开发者调用追踪失败，但请求继续返回。TraceId={TraceId}, AttemptId={AttemptId}",
                traceId,
                traceAttemptId);
        }
    }

    /// <summary>
    /// 安全地记录失败的代理请求明细。
    /// </summary>
    private void SafeLogFailedProxyAttempt(
        string requestSource,
        string modelName,
        CachedProxyRouteTarget route,
        string actualProtocolType,
        string preparedRequestBody,
        ProxyForwardResult result)
    {
        try
        {
            LogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "记录失败代理日志失败，但继续后续流程。RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                modelName,
                route.UpstreamModelName);
        }
    }

    /// <summary>
    /// 安全地输出控制台代理摘要日志。
    /// </summary>
    private void SafeWriteConsoleProxyLog(
        string clientProtocol,
        string requestSource,
        string modelName,
        string actualProtocolType,
        string preparedRequestBody,
        ProxyForwardResult result,
        int requestBodyLength)
    {
        try
        {
            Console.WriteLine(ConsoleProxyLogFormatter.BuildSummary(
                clientProtocol,
                requestSource,
                modelName,
                actualProtocolType,
                result.StatusCode,
                result.Success,
                result.IsStreaming,
                result.IsStreamInterrupted,
                result.TotalDurationMs,
                requestBodyLength,
                result.ResponseBody?.Length ?? 0));
        }
        catch
        {
        }
    }

    /// <summary>
    /// 将一次路由尝试的结果写回开发者追踪。
    /// </summary>
    private void CompleteDeveloperTraceAttempt(Guid? traceId, Guid traceAttemptId, DeveloperInvocationResult result)
    {
        if (!traceId.HasValue || traceAttemptId == Guid.Empty)
        {
            return;
        }

        _traceStore.CompleteAttempt(traceId.Value, traceAttemptId, result);
    }

    /// <summary>
    /// 输出一次失败代理尝试的完整上下文日志。
    /// </summary>
    private void LogFailedProxyAttempt(
        string requestSource,
        string modelName,
        CachedProxyRouteTarget route,
        string actualProtocolType,
        string preparedRequestBody,
        ProxyForwardResult result)
    {
        _logger.LogError(
            "代理请求失败\nSource={Source}\nClientProtocol={ClientProtocol}\nUpstreamProtocol={UpstreamProtocol}\nRequestModel={RequestModel}\nAttemptedModel={AttemptedModel}\nSiteName={SiteName}\nBaseUrl={BaseUrl}\nStatusCode={StatusCode}\nIsStreaming={IsStreaming}\nIsStreamInterrupted={IsStreamInterrupted}\nErrorMessage={ErrorMessage}\nRequestBody={RequestBody}\nResponseBody={ResponseBody}",
            requestSource,
            "OpenAI",
            actualProtocolType,
            modelName,
            route.UpstreamModelName,
            route.SiteName,
            route.BaseUrl,
            result.StatusCode,
            result.IsStreaming,
            result.IsStreamInterrupted,
            result.ErrorMessage ?? string.Empty,
            HttpLogFormatter.FormatBody(preparedRequestBody),
            HttpLogFormatter.FormatBody(result.ResponseBody));
    }
}
