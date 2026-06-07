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

            if (string.Equals(clientProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceOpenAiModelAndEnsureStreamUsage(requestBody, targetModelName, enableStreaming);
            }

            return ReplaceModelName(requestBody, targetModelName);
        }

        if (string.Equals(clientProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertChatRequestToResponses(requestBody, targetModelName, enableStreaming);
        }

        if (string.Equals(clientProtocol, "Responses", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertResponsesRequestToChat(requestBody, targetModelName, enableStreaming);
        }

        var rootNode = JsonNode.Parse(requestBody) as JsonObject;
        if (rootNode is null)
        {
            return requestBody;
        }

        if (string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            var openAiRequestBody = BuildOpenAiRequestFromAnthropic(rootNode, targetModelName, enableStreaming);
            return ConvertChatRequestToResponses(openAiRequestBody, targetModelName, enableStreaming);
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

        if (string.Equals(clientProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase)
            && string.Equals(upstreamProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            return isStreaming
                ? ConvertResponsesStreamingToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens)
                : ConvertResponsesResponseToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens);
        }

        if (string.Equals(clientProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            && string.Equals(upstreamProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            var openAiResponseBody = isStreaming
                ? ConvertResponsesStreamingToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens)
                : ConvertResponsesResponseToChat(responseBody, modelName, inputTokens, cachedTokens, outputTokens);

            return isStreaming
                ? BuildAnthropicStreamingResponseFromOpenAi(openAiResponseBody, modelName, inputTokens, cachedTokens, outputTokens)
                : BuildAnthropicResponseFromOpenAi(openAiResponseBody, modelName, inputTokens, cachedTokens, outputTokens);
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
}
