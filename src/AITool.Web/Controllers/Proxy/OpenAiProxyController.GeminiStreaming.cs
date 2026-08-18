using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using AITool.Protocol;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

/// <summary>
/// 承载 Gemini 上游（GeminiCLI / Antigravity）的流式协议桥接逻辑：
/// Gemini SSE → OpenAI Chat / Responses 客户端事件流。
/// </summary>
public sealed partial class OpenAiProxyController
{
    /// <summary>
    /// 把 Gemini 上游流式响应转换为 OpenAI Chat Completions SSE 返回给客户端。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardGeminiStreamAsOpenAiAsync(
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
        var state = new ProxyProtocolBridge.GeminiToOpenAiStreamState();
        var responseId = $"chatcmpl-{Guid.NewGuid():N}";

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

        async Task FlushGeminiBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseDataPayload(pendingSseLines, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var converted = ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(payload, modelName, responseId, state);
            await WriteChunkAsync(converted ?? string.Empty, token);
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushGeminiBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushGeminiBlockAsync(cancellationToken);
        }

        // Gemini 流没有 [DONE] 标记：finishReason 出现即视为正常完成，由收尾块统一结束。
        if (result.Success)
        {
            await WriteChunkAsync(ProxyProtocolBridge.CompleteGeminiToOpenAiStream(modelName, responseId, state) ?? string.Empty, cancellationToken);
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = state.InputTokens;
        result.CachedTokens = state.CachedTokens;
        result.OutputTokens = state.OutputTokens;

        if (result.Success && state.FinishReason is null)
        {
            result.Success = false;
            result.IsStreamInterrupted = startedWriting;
            result.ErrorMessage ??= startedWriting
                ? "stream interrupted before finishReason"
                : "stream ended before any gemini candidate";
        }

        if (state.FinishReason is not null)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
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
            catch { /* 客户端可能已断开，忽略 */ }
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 把 Gemini 上游流式响应转换为 Responses API 事件流返回给客户端。
    /// 链路：Gemini SSE →（协议层状态机）→ Anthropic 事件 →（既有桥）→ Responses 事件。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardGeminiStreamAsResponsesAsync(
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
        var geminiState = new ProxyProtocolBridge.GeminiToAnthropicStreamState();
        var responsesState = new ChatToResponsesStreamState
        {
            Model = forwardRequest.TargetModelName
        };

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

        async Task FlushGeminiBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseDataPayload(pendingSseLines, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 单个 Gemini 块可能产出多个 Anthropic 事件（message_start + block_start + delta...），
            // 逐事件送入既有 Anthropic→Responses 桥。
            var anthropicSse = ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(payload, modelName, geminiState);
            if (string.IsNullOrEmpty(anthropicSse))
            {
                return;
            }

            foreach (var block in anthropicSse.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!TryExtractSseEventPayload(lines, out var eventName, out var eventPayload))
                {
                    continue;
                }

                var responsesChunk = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(eventName, eventPayload, responsesState);
                if (!string.IsNullOrEmpty(responsesChunk))
                {
                    await WriteChunkAsync(responsesChunk, token);
                }
            }
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushGeminiBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushGeminiBlockAsync(cancellationToken);
        }

        // 流结束：补齐 Anthropic 收尾事件并同样桥接为 Responses 终态事件。
        if (result.Success)
        {
            var closing = ProxyProtocolBridge.CompleteGeminiToAnthropicStream(geminiState);
            foreach (var block in closing.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!TryExtractSseEventPayload(lines, out var eventName, out var eventPayload))
                {
                    continue;
                }

                var responsesChunk = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(eventName, eventPayload, responsesState);
                if (!string.IsNullOrEmpty(responsesChunk))
                {
                    await WriteChunkAsync(responsesChunk, cancellationToken);
                }
            }
        }

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = geminiState.InputTokens;
        result.CachedTokens = geminiState.CachedTokens;
        result.OutputTokens = geminiState.OutputTokens;

        if (result.Success && !responsesState.Done && startedWriting)
        {
            result.Success = false;
            result.IsStreamInterrupted = true;
            result.ErrorMessage ??= "stream interrupted before response.completed";
        }

        if (responsesState.Done)
        {
            result.IsStreamInterrupted = false;
            result.ErrorMessage = null;
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
            catch { /* 客户端可能已断开，忽略 */ }
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    /// <summary>
    /// 把 Gemini 上游流式响应桥接为 Responses WebSocket JSON 消息返回给客户端。
    /// 链路：Gemini SSE → Anthropic 事件 → Responses 事件 → WebSocket JSON。
    /// </summary>
    private async Task<StreamForwardOutcome> ForwardGeminiResponsesAsWebSocketAsync(
        WebSocket webSocket,
        ProxyForwardRequest forwardRequest,
        string modelName,
        CancellationToken cancellationToken)
    {
        var responseBuilder = new StringBuilder();
        var pendingSseLines = new List<string>();
        var startedWriting = false;
        var completedOutputJson = "[]";
        var geminiState = new ProxyProtocolBridge.GeminiToAnthropicStreamState();
        var responsesState = new ChatToResponsesStreamState
        {
            Model = forwardRequest.TargetModelName
        };

        async Task WriteAnthropicEventsAsResponsesAsync(string anthropicSse, CancellationToken token)
        {
            foreach (var block in anthropicSse.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!TryExtractSseEventPayload(lines, out var eventName, out var eventPayload))
                {
                    continue;
                }

                var responsesChunk = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(eventName, eventPayload, responsesState);
                if (string.IsNullOrEmpty(responsesChunk))
                {
                    continue;
                }

                foreach (var wsPayload in ExtractWebSocketJsonPayloadsFromSseText(responsesChunk))
                {
                    if (TryExtractResponsesCompletedOutput(wsPayload, out var outputJson))
                    {
                        completedOutputJson = outputJson;
                    }

                    if (responseBuilder.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars) { responseBuilder.AppendLine(wsPayload); }
                    await SendWebSocketJsonPayloadAsync(webSocket, wsPayload, token);
                    startedWriting = true;
                }
            }
        }

        async Task FlushGeminiBlockAsync(CancellationToken token)
        {
            if (!TryExtractSseDataPayload(pendingSseLines, out var payload))
            {
                pendingSseLines.Clear();
                return;
            }

            pendingSseLines.Clear();
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var anthropicSse = ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(payload, modelName, geminiState);
            if (!string.IsNullOrEmpty(anthropicSse))
            {
                await WriteAnthropicEventsAsResponsesAsync(anthropicSse, token);
            }
        }

        var result = await _forwardService.ForwardStreamingAsync(
            forwardRequest,
            async (line, token) =>
            {
                if (string.IsNullOrEmpty(line))
                {
                    await FlushGeminiBlockAsync(token);
                    return;
                }

                pendingSseLines.Add(line);
            },
            cancellationToken);

        if (pendingSseLines.Count > 0)
        {
            await FlushGeminiBlockAsync(cancellationToken);
        }

        // 流结束：补齐 Anthropic 收尾事件并桥接为 Responses 终态。
        await WriteAnthropicEventsAsResponsesAsync(ProxyProtocolBridge.CompleteGeminiToAnthropicStream(geminiState), cancellationToken);

        result.ResponseBody = responseBuilder.ToString();
        result.IsStreaming = true;
        result.HasStartedStreaming = startedWriting;
        result.InputTokens = geminiState.InputTokens;
        result.CachedTokens = geminiState.CachedTokens;
        result.OutputTokens = geminiState.OutputTokens;

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
}
