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
/// 承载 OpenAI 代理流式转发、SSE 透传和跨协议流式桥接逻辑。
/// </summary>
public sealed partial class OpenAiProxyController
{
    /// <summary>
    /// 透传 OpenAI Responses SSE，并逐条转换成 WebSocket JSON 消息返回给客户端。
    /// </summary>
    /// <param name="webSocket">已经完成鉴权并接受的下游 WebSocket 连接。</param>
    /// <param name="forwardRequest">已经完成路由选择和请求体准备的上游转发请求。</param>
    /// <param name="cancellationToken">用于中断当前 WebSocket 转发的取消令牌。</param>
    /// <returns>返回本轮 WebSocket 流式转发结果和可用于续传的输出内容。</returns>
    private async Task<StreamForwardOutcome> ForwardOpenAiResponsesAsWebSocketAsync(
        WebSocket webSocket,
        ProxyForwardRequest forwardRequest,
        CancellationToken cancellationToken)
    {
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var completedOutputJson = "[]";
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;
        var receivedDoneEvent = false;

        async Task FlushSseBlockAsync(CancellationToken token)
        {
            // 上游 SSE 以空行作为事件边界，这里按完整事件块转成一条 WebSocket 消息。
            if (pendingSseLines.Count == 0)
            {
                return;
            }

            if (TryExtractSseDataPayload(pendingSseLines, out var payload))
            {
                if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    receivedDoneEvent = true;
                }
                else
                {
                    UpdateOpenAiUsageFromPayload(payload, ref inputTokens, ref cachedTokens, ref outputTokens);
                    if (TryExtractResponsesCompletedOutput(payload, out var outputJson))
                    {
                        // 保存 response.completed 的 output，供下一轮 response.append 合并上下文。
                        completedOutputJson = outputJson;
                        receivedDoneEvent = true;
                    }
                    if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.AppendLine(payload); }
                    await SendWebSocketJsonPayloadAsync(webSocket, payload, token);
                    startedWriting = true;
                }
            }

            pendingSseLines.Clear();
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushSseBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushSseBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = inputTokens;
        result.CachedTokens = cachedTokens;
        result.OutputTokens = outputTokens;

        if (result.Success && !receivedDoneEvent)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before response.completed"
                : "stream ended before any response event";
        }

        if (receivedDoneEvent)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting,
            CompletedOutputJson = completedOutputJson
        };
    }

    /// <summary>
    /// 将 Anthropic SSE 事件桥接为 Responses WebSocket JSON 消息返回给客户端。
    /// </summary>
    /// <param name="webSocket">已经完成鉴权并接受的下游 WebSocket 连接。</param>
    /// <param name="forwardRequest">已经完成路由选择和 Anthropic 请求体准备的上游转发请求。</param>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="cancellationToken">用于中断当前 WebSocket 转发的取消令牌。</param>
    /// <returns>返回本轮 WebSocket 流式桥接结果和可用于续传的输出内容。</returns>
    private async Task<StreamForwardOutcome> ForwardAnthropicResponsesAsWebSocketAsync(
        WebSocket webSocket,
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var completedOutputJson = "[]";
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;
        var responsesState = new ChatToResponsesStreamState
        {
            Model = forwardRequest.TargetModelName
        };

        async Task FlushAnthropicBlockAsync(CancellationToken token)
        {
            // Anthropic SSE 带 event 名称，必须保留事件类型才能转换为 Responses 事件。
            if (!TryExtractSseEventPayload(pendingSseLines, out var eventName, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.IsNullOrEmpty(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                UpdateAnthropicUsageFromSseEvent(eventName, doc.RootElement, ref inputTokens, ref cachedTokens, ref outputTokens);
            }
            catch
            {
            }

            var responsesSse = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(eventName, payload, responsesState);
            if (string.IsNullOrEmpty(responsesSse))
            {
                return;
            }

            foreach (var wsPayload in ExtractWebSocketJsonPayloadsFromSseText(responsesSse))
            {
                // 桥接后的 Responses SSE 需要拆成单条 JSON，再逐条写入 WebSocket。
                if (TryExtractResponsesCompletedOutput(wsPayload, out var outputJson))
                {
                    completedOutputJson = outputJson;
                }

                if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.AppendLine(wsPayload); }
                await SendWebSocketJsonPayloadAsync(webSocket, wsPayload, token);
                startedWriting = true;
            }
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushAnthropicBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushAnthropicBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = inputTokens;
        result.CachedTokens = cachedTokens;
        result.OutputTokens = outputTokens;

        if (result.Success && !responsesState.Done)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before response.completed"
                : "stream ended before any response event";
        }

        if (responsesState.Done)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting,
            CompletedOutputJson = completedOutputJson
        };
    }

    /// <summary>
    /// 把 Anthropic 流式响应转换为 Responses API 流式事件后返回给客户端。
    /// </summary>
    /// <param name="forwardRequest">已经完成路由选择和 Anthropic 请求体准备的上游转发请求。</param>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="cancellationToken">用于中断当前流式转发的取消令牌。</param>
    /// <returns>返回 Responses SSE 转换后的流式转发结果。</returns>
    private async Task<StreamForwardOutcome> ForwardAnthropicStreamAsResponsesAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        // 先走 Anthropic → OpenAI 的流式转换，收集完整响应后转为 Responses 事件
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;

        // Responses 流式状态
        var responsesState = new ChatToResponsesStreamState
        {
            Model = forwardRequest.TargetModelName
        };

        async Task FlushAnthropicSseBlockAsync(CancellationToken token)
        {
            // Responses 桥接依赖 Anthropic 事件名来生成对应的 response.* 事件。
            if (!TryExtractSseEventPayload(pendingSseLines, out var eventName, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.IsNullOrEmpty(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 直接把 Anthropic SSE 事件转为 Responses 事件
            var responsesChunk = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(eventName, payload, responsesState);
            if (!string.IsNullOrEmpty(responsesChunk))
            {
                if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.Append(responsesChunk); }
                await Response.WriteAsync(responsesChunk, token);
                await Response.Body.FlushAsync(token);
                startedWriting = true;
            }

            // 提取用量
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                UpdateAnthropicUsageFromSseEvent(eventName, root, ref inputTokens, ref cachedTokens, ref outputTokens);
            }
            catch
            {
            }
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushAnthropicSseBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushAnthropicSseBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = inputTokens;
        result.CachedTokens = cachedTokens;
        result.OutputTokens = outputTokens;

        if (result.Success && !responsesState.Done && startedWriting)
        {
            result.Success = false;
            result.IsStreamInterrupted = true;
            result.ErrorMessage ??= "stream interrupted before response.completed";
        }

        // 控制器层检测到 Responses 转换正常完成时，清除基础设施层可能误设的中断标记
        if (responsesState.Done)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
        }

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        // 流中断但已开始写入时，向客户端补发终止信号避免挂起
        if (result.IsStreamInterrupted && startedWriting)
        {
            try
            {
                await Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch { /* 客户端可能已断开，忽略 */ }
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 从 Anthropic SSE 事件中提取用量信息。
    /// </summary>
    /// <param name="eventName">当前 Anthropic SSE 事件名称。</param>
    /// <param name="root">当前 SSE data 行对应的 JSON 根节点。</param>
    /// <param name="inputTokens">保存提取到的输入 token 数。</param>
    /// <param name="cachedTokens">保存提取到的缓存命中 token 数。</param>
    /// <param name="outputTokens">保存提取到的输出 token 数。</param>
    private static void UpdateAnthropicUsageFromSseEvent(string eventName, JsonElement root, ref int inputTokens, ref int cachedTokens, ref int outputTokens)
    {
        if (string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it))
                {
                    inputTokens = it.GetInt32();
                }

                if (usage.TryGetProperty("cache_read_input_tokens", out var ct))
                {
                    cachedTokens = ct.GetInt32();
                }
            }
        }
        else if (string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("output_tokens", out var ot))
                {
                    outputTokens = ot.GetInt32();
                }
            }
        }
    }

    /// <summary>
    /// 透传 OpenAI 原生流式响应，并在透传过程中提取用量信息。
    /// </summary>
    /// <param name="forwardRequest">已经完成路由选择和协议准备的上游转发请求。</param>
    /// <param name="cancellationToken">用于中断当前流式转发的取消令牌。</param>
    /// <param name="sseBlockConverter">可选的 SSE 块转换器，用于在写入客户端前改写单个数据块。</param>
    /// <returns>返回流式转发结果，并标记是否还能继续 fallback 到下一条路由。</returns>
    private async Task<StreamForwardOutcome> ForwardOpenAiStreamPassthroughAsync(
        ProxyForwardRequest forwardRequest,
        CancellationToken cancellationToken,
        Func<string, string>? sseBlockConverter = null)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var receivedDoneEvent = false;
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;

        async Task WriteRawSseBlockAsync(List<string> lines, CancellationToken token)
        {
            if (lines.Count == 0)
            {
                return;
            }

            var chunkBuilder = new StringBuilder();
            foreach (var line in lines)
            {
                chunkBuilder.Append(line).Append('\n');
            }

            chunkBuilder.Append('\n');
            var chunk = chunkBuilder.ToString();
            if (sseBlockConverter is not null)
            {
                // legacy Completions 复用同一透传链路，只在写出前转换单个 SSE 块。
                chunk = sseBlockConverter(chunk);
            }

            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.Append(chunk); }
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task FlushOpenAiSseBlockAsync(CancellationToken token)
        {
            // OpenAI SSE 事件块可能包含 usage 或完成事件，需要先提取统计再原样写出。
            if (pendingSseLines.Count == 0)
            {
                return;
            }

            if (TryExtractSseDataPayload(pendingSseLines, out var payload))
            {
                if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    receivedDoneEvent = true;
                }
                else
                {
                    UpdateOpenAiUsageFromPayload(payload, ref inputTokens, ref cachedTokens, ref outputTokens);

                    // 兼容 Responses API：上游可能以 response.completed 事件而非 [DONE] 结束流
                    if (!receivedDoneEvent)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.TryGetProperty("type", out var typeEl)
                                && string.Equals(typeEl.GetString(), "response.completed", StringComparison.OrdinalIgnoreCase))
                            {
                                receivedDoneEvent = true;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            await WriteRawSseBlockAsync(pendingSseLines, token);
            pendingSseLines.Clear();
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushOpenAiSseBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushOpenAiSseBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = inputTokens;
        result.CachedTokens = cachedTokens;
        result.OutputTokens = outputTokens;

        if (result.Success && !receivedDoneEvent)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before [DONE]"
                : "stream ended before any complete SSE event";
        }

        // 控制器层检测到流正常结束时，清除基础设施层可能误设的中断标记
        if (receivedDoneEvent)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
        }

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        // 流中断但已开始写入时，向客户端补发终止信号避免挂起
        if (result.IsStreamInterrupted && startedWriting)
        {
            try
            {
                await Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch { /* 客户端可能已断开，忽略 */ }
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 透传 OpenAI 流式响应并转换为 legacy Completions SSE 格式。
    /// </summary>
    /// <param name="forwardRequest">已经完成路由选择和请求体转换的上游转发请求。</param>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="cancellationToken">用于中断当前流式转发的取消令牌。</param>
    /// <returns>返回 legacy Completions SSE 转换后的流式转发结果。</returns>
    private async Task<StreamForwardOutcome> ForwardOpenAiStreamAsCompletionsAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        // 先按 Chat Completions 方式走完整透传链路，再逐块改写为 legacy Completions SSE。
        return await ForwardOpenAiStreamPassthroughAsync(
            forwardRequest,
            cancellationToken,
            ProxyProtocolBridge.ConvertChatCompletionSseToCompletionsSse);
    }

    /// <summary>
    /// 把 Anthropic 流式响应转换成 legacy Completions SSE 格式后返回给客户端。
    /// </summary>
    /// <param name="forwardRequest">已经完成路由选择和请求体转换的上游转发请求。</param>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="cancellationToken">用于中断当前流式转发的取消令牌。</param>
    /// <returns>返回 legacy Completions SSE 转换后的流式转发结果。</returns>
    private async Task<StreamForwardOutcome> ForwardAnthropicStreamAsCompletionsAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        // Anthropic 先桥接成 Chat Completions SSE，再通过块转换器降级为 legacy Completions SSE。
        return await ForwardAnthropicStreamAsOpenAiAsync(
            forwardRequest,
            modelName,
            cancellationToken,
            ProxyProtocolBridge.ConvertChatCompletionSseToCompletionsSse);
    }

    /// <summary>
    /// 将 Responses SSE 事件实时转换为 OpenAI Chat Completions SSE 数据块。
    /// </summary>
    /// <param name="forwardRequest">已经完成路由选择和 Responses 请求体准备的上游转发请求。</param>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="cancellationToken">用于中断当前流式转发的取消令牌。</param>
    /// <returns>返回转换后的 OpenAI SSE 流式转发结果。</returns>
    private async Task<StreamForwardOutcome> ForwardResponsesStreamAsOpenAiAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var receivedDoneEvent = false;
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;

        async Task WriteChunkAsync(string chunk, CancellationToken token)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.Append(chunk); }
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task FlushResponsesSseBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseEventPayload(pendingSseLines, out var eventName, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UpdateOpenAiUsageFromPayload(payload, ref inputTokens, ref cachedTokens, ref outputTokens);
            var openAiChunk = ProxyProtocolBridge.ConvertResponsesStreamingToChat($"event: {eventName}\ndata: {payload}\n\n", modelName, inputTokens, cachedTokens, outputTokens);
            if (!string.IsNullOrEmpty(openAiChunk))
            {
                await WriteChunkAsync(openAiChunk, token);
            }

            if (string.Equals(eventName, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                receivedDoneEvent = true;
            }
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushResponsesSseBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushResponsesSseBlockAsync(cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = inputTokens;
        result.CachedTokens = cachedTokens;
        result.OutputTokens = outputTokens;

        if (result.Success && !receivedDoneEvent)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before response.completed"
                : "stream ended before any complete SSE event";
        }

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        if (result.IsStreamInterrupted && startedWriting)
        {
            try
            {
                await Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch
            {
            }
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 将 Anthropic 原生 SSE 事件实时转换为 OpenAI Chat Completions SSE 数据块。
    /// </summary>
    /// <param name="forwardRequest">已经完成路由选择和 Anthropic 请求体准备的上游转发请求。</param>
    /// <param name="modelName">客户端请求使用的模型名称。</param>
    /// <param name="cancellationToken">用于中断当前流式转发的取消令牌。</param>
    /// <param name="sseBlockConverter">可选的 SSE 块转换器，用于复用当前桥接逻辑输出其他 OpenAI 兼容流式格式。</param>
    /// <returns>返回转换后的 OpenAI SSE 流式转发结果。</returns>
    private async Task<StreamForwardOutcome> ForwardAnthropicStreamAsOpenAiAsync(
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken,
        Func<string, string>? sseBlockConverter = null)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
        }

        var state = new AnthropicToOpenAiStreamState();
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;

        async Task WriteChunkAsync(string chunk, CancellationToken token)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            if (sseBlockConverter is not null)
            {
                // 使用可选转换器保持桥接主流程不变，同时支持 legacy Completions 输出格式。
                chunk = sseBlockConverter(chunk);
            }

            if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.Append(chunk); }
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task EnsureRoleChunkAsync(CancellationToken token)
        {
            // OpenAI 流式响应需要先声明 assistant 角色，避免部分客户端拿不到首个角色块。
            if (state.RoleChunkSent)
            {
                return;
            }

            // 先发 role chunk，确保下游按标准 OpenAI SSE 增量消费。
            await WriteChunkAsync(BuildOpenAiDeltaChunk(modelName, new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty
            }), token);
            state.RoleChunkSent = true;
        }

        async Task FlushAnthropicSseBlockAsync(CancellationToken token)
        {
            // Chat Completions 桥接按事件块实时输出，避免等完整响应结束后才返回。
            if (!TryExtractSseEventPayload(pendingSseLines, out var eventName, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (string.Equals(eventName, "message_start", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("usage", out var startUsage))
                    {
                        UpdateAnthropicUsageFromElement(startUsage, state);
                    }

                    await EnsureRoleChunkAsync(token);
                    return;
                }

                if (string.Equals(eventName, "content_block_start", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("content_block", out var contentBlock))
                {
                    var blockType = contentBlock.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : null;
                    if (string.Equals(blockType, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        var blockIndex = root.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                            ? indexElement.GetInt32()
                            : state.ToolCalls.Count;
                        if (!state.ToolCalls.TryGetValue(blockIndex, out var toolCallState))
                        {
                            toolCallState = new AnthropicToolCallState { Index = state.ToolCalls.Count };
                            state.ToolCalls[blockIndex] = toolCallState;
                        }

                        toolCallState.Id = contentBlock.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString() ?? string.Empty
                            : toolCallState.Id;
                        toolCallState.Name = contentBlock.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                            ? nameElement.GetString() ?? string.Empty
                            : toolCallState.Name;

                        await EnsureRoleChunkAsync(token);
                        await WriteChunkAsync(BuildOpenAiToolCallChunk(modelName, new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = toolCallState.Index,
                                ["id"] = toolCallState.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = toolCallState.Name,
                                    ["arguments"] = string.Empty
                                }
                            }
                        }), token);
                    }

                    return;
                }

                if (string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("delta", out var delta))
                {
                    var deltaType = delta.TryGetProperty("type", out var deltaTypeElement)
                        ? deltaTypeElement.GetString()
                        : null;
                    if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = delta.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                            ? textElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(text))
                        {
                            await EnsureRoleChunkAsync(token);
                            await WriteChunkAsync(BuildOpenAiDeltaChunk(modelName, new JsonObject
                            {
                                ["content"] = text
                            }), token);
                        }

                        return;
                    }

                    if (string.Equals(deltaType, "thinking_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var thinking = delta.TryGetProperty("thinking", out var thinkingElement) && thinkingElement.ValueKind == JsonValueKind.String
                            ? thinkingElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            await EnsureRoleChunkAsync(token);
                            await WriteChunkAsync(BuildOpenAiDeltaChunk(modelName, new JsonObject
                            {
                                ["reasoning_content"] = thinking
                            }), token);
                        }

                        return;
                    }

                    if (string.Equals(deltaType, "signature_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (string.Equals(deltaType, "input_json_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var blockIndex = root.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                            ? indexElement.GetInt32()
                            : -1;
                        var partialJson = delta.TryGetProperty("partial_json", out var partialJsonElement) && partialJsonElement.ValueKind == JsonValueKind.String
                            ? partialJsonElement.GetString()
                            : null;
                        if (blockIndex >= 0 &&
                            !string.IsNullOrEmpty(partialJson) &&
                            state.ToolCalls.TryGetValue(blockIndex, out var toolCallState))
                        {
                            await EnsureRoleChunkAsync(token);
                            await WriteChunkAsync(BuildOpenAiToolCallChunk(modelName, new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = toolCallState.Index,
                                    ["function"] = new JsonObject
                                    {
                                        ["arguments"] = partialJson
                                    }
                                }
                            }), token);
                        }

                        return;
                    }
                }

                if (string.Equals(eventName, "message_delta", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("delta", out var messageDelta) &&
                        messageDelta.TryGetProperty("stop_reason", out var stopReasonElement) &&
                        stopReasonElement.ValueKind == JsonValueKind.String)
                    {
                        state.StopReason = stopReasonElement.GetString() ?? state.StopReason;
                    }

                    if (root.TryGetProperty("usage", out var deltaUsage))
                    {
                        UpdateAnthropicUsageFromElement(deltaUsage, state);
                    }

                    return;
                }

                if (string.Equals(eventName, "message_stop", StringComparison.OrdinalIgnoreCase))
                {
                    // 收到 Anthropic 结束事件后补齐 OpenAI finish chunk 和 [DONE] 终止标记。
                    state.ReceivedMessageStop = true;
                    await EnsureRoleChunkAsync(token);
                    var totalInputTokens = state.InputTokens + state.CachedTokens + state.CacheCreationTokens;
                    await WriteChunkAsync(BuildOpenAiFinishChunk(
                        modelName,
                        MapAnthropicStopReason(state.StopReason),
                        totalInputTokens,
                        state.CachedTokens,
                        state.CacheCreationTokens,
                        state.OutputTokens), token);
                    await WriteChunkAsync("data: [DONE]\n\n", token);
                }
            }
            catch
            {
            }
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushAnthropicSseBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushAnthropicSseBlockAsync(cancellationToken);
        }

        var totalPromptTokens = state.InputTokens + state.CachedTokens + state.CacheCreationTokens;
        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = totalPromptTokens;
        result.CachedTokens = state.CachedTokens;
        result.OutputTokens = state.OutputTokens;

        if (result.Success && !state.ReceivedMessageStop)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before message_stop"
                : "stream ended before any complete SSE event";
        }

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        // 流中断但已开始写入时，向客户端补发终止信号避免挂起
        if (result.IsStreamInterrupted && startedWriting)
        {
            try
            {
                await Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch { /* 客户端可能已断开，忽略 */ }
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

}
