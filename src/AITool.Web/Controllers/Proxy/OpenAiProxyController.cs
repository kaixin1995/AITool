using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using AITool.Web.Services;

namespace AITool.Web.Controllers.Proxy;

// OpenAI 协议兼容代理控制器，转发 chat completions 请求并集成熔断机制
[ApiController]
public sealed class OpenAiProxyController : ControllerBase
{
    private sealed class StreamForwardOutcome
    {
        public ProxyForwardResult Result { get; init; } = new();
        public bool CanFallback { get; init; }
    }

    private sealed class AnthropicToOpenAiStreamState
    {
        public bool RoleChunkSent { get; set; }
        public bool ReceivedMessageStop { get; set; }
        public string StopReason { get; set; } = "stop";
        public int InputTokens { get; set; }
        public int CachedTokens { get; set; }
        public int CacheCreationTokens { get; set; }
        public int OutputTokens { get; set; }
        public Dictionary<int, AnthropicToolCallState> ToolCalls { get; } = [];
    }

    private sealed class AnthropicToolCallState
    {
        public int Index { get; init; }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private readonly IProxyForwardService _forwardService;
    private readonly IUsageLogService _usageLogService;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly DeveloperInvocationTraceStore _traceStore;
    private readonly ILogger<OpenAiProxyController> _logger;

    public OpenAiProxyController(
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        RouteCircuitStateStore circuitStore,
        ProxyRequestMetadataCache metadataCache,
        DeveloperInvocationTraceStore traceStore,
        ILogger<OpenAiProxyController> logger)
    {
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _circuitStore = circuitStore;
        _metadataCache = metadataCache;
        _traceStore = traceStore;
        _logger = logger;
    }

    // 返回当前代理对外暴露的模型列表，兼容 OpenAI 和 Anthropic 客户端拉取模型。
    [HttpGet("/v1/models")]
    public async Task<IActionResult> Models(CancellationToken cancellationToken)
    {
        var isAnthropicClient = Request.Headers.ContainsKey("x-api-key")
            || Request.Headers.ContainsKey("anthropic-version");

        string accessToken;
        if (isAnthropicClient)
        {
            accessToken = Request.Headers.TryGetValue("x-api-key", out var keyHeader)
                ? keyHeader.ToString()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                var anthropicAuthHeader = Request.Headers.Authorization.ToString();
                accessToken = anthropicAuthHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? anthropicAuthHeader[7..]
                    : string.Empty;
            }
        }
        else
        {
            var authHeader = Request.Headers.Authorization.ToString();
            accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader[7..]
                : string.Empty;
        }

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        var modelIds = await _metadataCache.GetEnabledModelNamesAsync(cancellationToken);

        if (isAnthropicClient)
        {
            return Ok(new
            {
                data = modelIds.Select(modelId => new
                {
                    type = "model",
                    id = modelId,
                    display_name = modelId,
                    created_at = DateTimeOffset.UtcNow.ToString("O")
                }),
                has_more = false,
                first_id = modelIds.FirstOrDefault(),
                last_id = modelIds.LastOrDefault()
            });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Ok(new
        {
            @object = "list",
            data = modelIds.Select(modelId => new
            {
                id = modelId,
                @object = "model",
                created = now,
                owned_by = "aitool"
            })
        });
    }

    // 代理 OpenAI chat completions 请求，自动跳过熔断站点
    [HttpPost("/v1/chat/completions")]
    public async Task<IActionResult> ChatCompletions(CancellationToken cancellationToken)
    {
        // 读取原始请求体
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken);

        // 解析请求中的模型名称
        string modelName;
        var enableStreaming = false;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            modelName = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
            enableStreaming = doc.RootElement.TryGetProperty("stream", out var streamValue)
                && streamValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                && streamValue.GetBoolean();
        }
        catch
        {
            return BadRequest(new { error = new { message = "Invalid request body" } });
        }

        // 验证访问密钥
        var authHeader = Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : string.Empty;

        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            return Unauthorized(new { error = new { message = "Invalid or missing access key" } });
        }

        // 优先读取显式来源标记，其次退回到 User-Agent 识别常见客户端工具。
        var requestSource = ResolveRequestSource(Request);

        // 读取运行时设置缓存，后台修改后会在短时间内刷新。
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var traceId = TryCreateDeveloperTraceSafely(runtimeSettings, requestSource, "OpenAI", modelName, requestBody);

        // 获取已经和站点信息合并后的候选路由，优先尝试支持 OpenAI 原协议的站点。
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync("OpenAI", modelName, cancellationToken);

        if (allRoutes.Count == 0)
        {
            return NotFound(new { error = new { message = $"No available route for model: {modelName}" } });
        }

        // 按优先级逐个尝试路由，失败则通知熔断器并继续下一个
        ProxyForwardResult? lastResult = null;
        var requestId = Guid.NewGuid();
        var attemptIndex = 0;

        foreach (var route in allRoutes)
        {
            // 跳过已被熔断器屏蔽的路由
            if (IsRouteBlockedSafely(route.RouteId))
                continue;

            attemptIndex++;
            var actualProtocolType = route.ResolveProtocolForClient("OpenAI");
            var traceAttemptId = AddDeveloperTraceAttemptSafely(traceId, route, actualProtocolType);
            var preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "OpenAI",
                actualProtocolType,
                requestBody,
                route.SiteModelName,
                enableStreaming);
            var forwardRequest = new ProxyForwardRequest
            {
                TargetBaseUrl = route.BaseUrl,
                TargetApiKey = route.ApiKey,
                ProtocolType = actualProtocolType,
                TargetModelName = route.SiteModelName,
                RequestBody = requestBody,
                PreparedRequestBody = preparedRequestBody,
                EnableStreaming = enableStreaming,
                RequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
                RetryCount = runtimeSettings.ProxyRetryCount
            };

            if (enableStreaming)
            {
                var streamOutcome = string.Equals(actualProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase)
                    ? await ForwardAnthropicStreamAsOpenAiAsync(forwardRequest, modelName, CancellationToken.None)
                    : await ForwardOpenAiStreamPassthroughAsync(forwardRequest, CancellationToken.None);
                var streamResult = streamOutcome.Result;
                SafeWriteConsoleProxyLog("OpenAI", requestSource, modelName, actualProtocolType, preparedRequestBody, streamResult, requestBody.Length);

                await SafeLogUsageAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    AccessKeyId = accessKey.Id,
                    ProtocolType = "OpenAI",
                    RequestModel = modelName,
                    AttemptedModel = route.UpstreamModelName,
                    TargetSiteId = route.SiteId,
                    Status = streamResult.Success ? "success" : "fail",
                    Source = requestSource,
                    RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                    AttemptIndex = attemptIndex,
                    IsFinalResult = streamResult.Success,
                    FallbackTriggered = !streamResult.Success,
                    ErrorMessage = streamResult.Success ? string.Empty : (streamResult.ErrorMessage ?? string.Empty),
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    IsStreaming = true,
                    IsStreamInterrupted = streamResult.IsStreamInterrupted,
                    FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                    StreamDurationMs = streamResult.StreamDurationMs,
                    TotalDurationMs = streamResult.TotalDurationMs
                }, CancellationToken.None);

                if (streamResult.Success)
                {
                    SafeSucceedRoute(route.RouteId);
                    SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                    {
                        Status = "success",
                        StatusCode = streamResult.StatusCode,
                        ResponseBody = DeveloperInvocationTraceStore.FormatBody(streamResult.ResponseBody),
                        ResponseContentType = "text/event-stream",
                        IsStreaming = true,
                        InputTokens = streamResult.InputTokens,
                        CachedTokens = streamResult.CachedTokens,
                        OutputTokens = streamResult.OutputTokens,
                        TotalDurationMs = streamResult.TotalDurationMs
                    });
                    return new EmptyResult();
                }

                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "fail",
                    StatusCode = streamResult.StatusCode,
                    ErrorMessage = streamResult.ErrorMessage ?? string.Empty,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(streamResult.ResponseBody),
                    ResponseContentType = "text/event-stream",
                    IsStreaming = true,
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    TotalDurationMs = streamResult.TotalDurationMs
                });
                SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, streamResult);
                SafeBlockRoute(route.RouteId);
                lastResult = streamResult;
                if (!streamOutcome.CanFallback)
                {
                    return new EmptyResult();
                }

                continue;
            }

            // 每条路由使用独立超时令牌，不受客户端断连影响，保证后续路由仍能独立尝试。
            var result = await _forwardService.ForwardAsync(forwardRequest, CancellationToken.None);
            SafeWriteConsoleProxyLog("OpenAI", requestSource, modelName, actualProtocolType, preparedRequestBody, result, requestBody.Length);

            await SafeLogUsageAsync(new UsageLogEntry
            {
                RequestId = requestId,
                AccessKeyId = accessKey.Id,
                ProtocolType = "OpenAI",
                RequestModel = modelName,
                AttemptedModel = route.UpstreamModelName,
                TargetSiteId = route.SiteId,
                Status = result.Success ? "success" : "fail",
                Source = requestSource,
                RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,
                AttemptIndex = attemptIndex,
                IsFinalResult = result.Success,
                FallbackTriggered = !result.Success,
                ErrorMessage = result.Success ? string.Empty : (result.ErrorMessage ?? string.Empty),
                InputTokens = result.InputTokens,
                CachedTokens = result.CachedTokens,
                OutputTokens = result.OutputTokens,
                IsStreaming = result.IsStreaming,
                IsStreamInterrupted = result.IsStreamInterrupted,
                FirstTokenLatencyMs = result.FirstTokenLatencyMs,
                StreamDurationMs = result.StreamDurationMs,
                TotalDurationMs = result.TotalDurationMs
            }, cancellationToken);

            if (result.Success)
            {
                // 成功时清除该路由的连续失败计数
                SafeSucceedRoute(route.RouteId);
                var responseBody = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI",
                    actualProtocolType,
                    result.ResponseBody,
                    result.IsStreaming,
                    modelName,
                    result.InputTokens,
                    result.CachedTokens,
                    result.OutputTokens);
                SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
                {
                    Status = "success",
                    StatusCode = result.StatusCode,
                    ResponseBody = DeveloperInvocationTraceStore.FormatBody(responseBody),
                    ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json",
                    IsStreaming = result.IsStreaming,
                    InputTokens = result.InputTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.OutputTokens,
                    TotalDurationMs = result.TotalDurationMs
                });
                // 流式响应以 SSE 格式返回，使用 text/event-stream 内容类型
                var contentType = result.IsStreaming ? "text/event-stream" : "application/json";
                return Content(responseBody, contentType);
            }

            SafeCompleteDeveloperTraceAttempt(traceId, traceAttemptId, new DeveloperInvocationResult
            {
                Status = "fail",
                StatusCode = result.StatusCode,
                ErrorMessage = result.ErrorMessage ?? string.Empty,
                ResponseBody = DeveloperInvocationTraceStore.FormatBody(result.ResponseBody),
                ResponseContentType = result.IsStreaming ? "text/event-stream" : "application/json",
                IsStreaming = result.IsStreaming,
                InputTokens = result.InputTokens,
                CachedTokens = result.CachedTokens,
                OutputTokens = result.OutputTokens,
                TotalDurationMs = result.TotalDurationMs
            });
            SafeLogFailedProxyAttempt(requestSource, modelName, route, actualProtocolType, preparedRequestBody, result);

            // 转发失败，通知熔断器（达到阈值才会真正触发熔断）
            SafeBlockRoute(route.RouteId);
            lastResult = result;
        }

        // 所有路由均失败
        var statusCode = lastResult?.StatusCode > 0 ? lastResult.StatusCode : 502;
        return StatusCode(statusCode,
            new { error = new { message = lastResult?.ErrorMessage ?? "All upstream routes failed" } });
    }

    private async Task<StreamForwardOutcome> ForwardOpenAiStreamPassthroughAsync(
        ProxyForwardRequest forwardRequest,
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
            responseBuilder.Append(chunk);
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task FlushOpenAiSseBlockAsync(CancellationToken token)
        {
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

        if (!result.Success && startedWriting)
        {
            result.IsStreamInterrupted = true;
        }

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    private async Task<StreamForwardOutcome> ForwardAnthropicStreamAsOpenAiAsync(
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

            responseBuilder.Append(chunk);
            await Response.WriteAsync(chunk, token);
            await Response.Body.FlushAsync(token);
            startedWriting = true;
        }

        async Task EnsureRoleChunkAsync(CancellationToken token)
        {
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

        return new StreamForwardOutcome
        {
            Result = result,
            CanFallback = !startedWriting
        };
    }

    // 优先使用自定义来源头，无法识别时再退回通用 proxy。
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

        return "proxy";
    }

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

    private static void UpdateOpenAiUsageFromPayload(string jsonText, ref int inputTokens, ref int cachedTokens, ref int outputTokens)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;
            if (!root.TryGetProperty("usage", out var usage))
            {
                return;
            }

            if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.ValueKind == JsonValueKind.Number)
            {
                inputTokens = promptTokens.GetInt32();
            }

            if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.ValueKind == JsonValueKind.Number)
            {
                outputTokens = completionTokens.GetInt32();
            }

            if (usage.TryGetProperty("prompt_tokens_details", out var promptTokenDetails) &&
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

    private static string BuildOpenAiDeltaChunk(string modelName, JsonObject deltaObject)
    {
        return BuildOpenAiChunkCore(modelName, deltaObject, null, null);
    }

    private static string BuildOpenAiToolCallChunk(string modelName, JsonArray toolCalls)
    {
        return BuildOpenAiChunkCore(modelName, new JsonObject
        {
            ["tool_calls"] = toolCalls
        }, null, null);
    }

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

    private static string ResolveForwardingMode(string clientProtocolType, string upstreamProtocolType)
    {
        return string.Equals(clientProtocolType, upstreamProtocolType, StringComparison.OrdinalIgnoreCase)
            ? "direct"
            : "bridge";
    }

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

    private void CompleteDeveloperTraceAttempt(Guid? traceId, Guid traceAttemptId, DeveloperInvocationResult result)
    {
        if (!traceId.HasValue || traceAttemptId == Guid.Empty)
        {
            return;
        }

        _traceStore.CompleteAttempt(traceId.Value, traceAttemptId, result);
    }

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
