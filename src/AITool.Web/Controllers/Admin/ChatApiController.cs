using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 对话测试用的模型列表项
public sealed class ChatModelItem
{
    // 模型ID
    public Guid ModelId { get; set; }
    // 模型显示名
    public string DisplayName { get; set; } = string.Empty;
    // 关联可用站点数
    public int AvailableSiteCount { get; set; }
}

// 对话测试的单次尝试明细
public sealed class ChatAttemptResult
{
    public int AttemptIndex { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool IsFinalResult { get; set; }
    public bool IsStreaming { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public int FirstTokenLatencyMs { get; set; }
    public int TotalDurationMs { get; set; }
}

// 发送消息的请求体
public sealed class ChatSendRequest
{
    // 思考等级选项
    public static readonly string[] ValidReasoningEfforts = ["low", "medium", "high"];

    // 选择的模型ID
    public Guid ModelId { get; set; }
    // 用户消息内容
    public string Message { get; set; } = string.Empty;
    // 是否开启思考模式
    public bool EnableReasoning { get; set; }
    // 是否启用流式
    public bool EnableStreaming { get; set; }
    // 思考等级：low / medium / high，仅在开启思考模式时生效
    public string ReasoningEffort { get; set; } = "high";
}

// 发送消息的返回结果
public sealed class ChatSendResult
{
    // 是否成功
    public bool Success { get; set; }
    // 请求ID
    public Guid? RequestId { get; set; }
    // AI 回复内容
    public string Content { get; set; } = string.Empty;
    // 思考内容
    public string ReasoningContent { get; set; } = string.Empty;
    // 错误信息
    public string? Error { get; set; }
    // 请求耗时（毫秒）
    public long DurationMs { get; set; }
    // 是否开启思考模式
    public bool ReasoningEnabled { get; set; }
    // 是否流式
    public bool IsStreaming { get; set; }
    // 输入 Token 数
    public int InputTokens { get; set; }
    // 缓存 Token 数
    public int CachedTokens { get; set; }
    // 输出 Token 数
    public int OutputTokens { get; set; }
    // 总 Token 数
    public int TotalTokens { get; set; }
    // 首字耗时（毫秒）
    public int FirstTokenLatencyMs { get; set; }
    // 总耗时（毫秒）
    public int TotalDurationMs { get; set; }
    // 调用尝试明细
    public List<ChatAttemptResult> Attempts { get; set; } = [];
}

// 聊天页流式转发的中间结果
internal sealed class ChatStreamForwardResult
{
    public bool Success { get; set; }
    public bool HadAnyContent { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ReasoningContent { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int FirstTokenLatencyMs { get; set; }
    public int TotalDurationMs { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int StatusCode { get; set; }
}

// 对话测试 API，提供模型选择、普通发送和流式发送功能
[ApiController]
[Route("api/admin/chat")]
public sealed class ChatApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IProxyForwardService _forwardService;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly IUsageLogService _usageLogService;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IHttpClientFactory _httpClientFactory;

    public ChatApiController(
        AppDbContext dbContext,
        IProxyForwardService forwardService,
        RouteCircuitStateStore circuitStore,
        IUsageLogService usageLogService,
        ProxyRequestMetadataCache metadataCache,
        IHttpClientFactory httpClientFactory)
    {
        _dbContext = dbContext;
        _forwardService = forwardService;
        _circuitStore = circuitStore;
        _usageLogService = usageLogService;
        _metadataCache = metadataCache;
        _httpClientFactory = httpClientFactory;
    }

    // 获取可用于对话测试的模型列表
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        var models = await _metadataCache.GetChatModelsAsync(cancellationToken);
        return Ok(models);
    }

    // 非流式发送，返回完整 JSON 结果
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] ChatSendRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "消息不能为空" });

        if (request.ModelId == Guid.Empty)
            return BadRequest(new { message = "请选择模型" });

        var sw = Stopwatch.StartNew();
        var model = await _metadataCache.GetEnabledModelAsync(request.ModelId, cancellationToken);

        if (model is null)
            return Ok(new ChatSendResult { Success = false, Error = "模型不存在或已禁用", ReasoningEnabled = request.EnableReasoning });

        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync(model.ModelName, cancellationToken);

        if (allRoutes.Count > 0)
        {
            var requestId = Guid.NewGuid();
            var attemptIndex = 0;
            var attempts = new List<ChatAttemptResult>();

            foreach (var route in allRoutes)
            {
                if (_circuitStore.IsBlocked(route.RouteId))
                    continue;

                attemptIndex++;
                var requestBody = BuildChatRequestBody(route.ProtocolType, route.SiteModelName, request.Message, request.EnableReasoning, false, request.ReasoningEffort);
                var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
                {
                    TargetBaseUrl = route.BaseUrl,
                    TargetApiKey = route.ApiKey,
                    ProtocolType = route.ProtocolType,
                    TargetModelName = route.SiteModelName,
                    RequestBody = requestBody,
                    PreparedRequestBody = requestBody,
                    EnableStreaming = false,
                    RequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
                    RetryCount = runtimeSettings.ProxyRetryCount
                }, cancellationToken);

                attempts.Add(BuildAttemptResult(attemptIndex, route.SiteName, route.UpstreamModelName, route.SiteModelName, forwardResult, forwardResult.Success));

                await _usageLogService.LogAsync(new UsageLogEntry
                {
                    RequestId = requestId,
                    ProtocolType = route.ProtocolType,
                    RequestModel = model.ModelName,
                    AttemptedModel = route.UpstreamModelName,
                    TargetSiteId = route.SiteId,
                    Status = forwardResult.Success ? "success" : "fail",
                    Source = "chat",
                    RetryCount = forwardResult.Success ? attemptIndex - 1 : attemptIndex,
                    AttemptIndex = attemptIndex,
                    IsFinalResult = forwardResult.Success,
                    FallbackTriggered = !forwardResult.Success,
                    ErrorMessage = forwardResult.Success ? string.Empty : (forwardResult.ErrorMessage ?? string.Empty),
                    InputTokens = forwardResult.InputTokens,
                    CachedTokens = forwardResult.CachedTokens,
                    OutputTokens = forwardResult.OutputTokens,
                    IsStreaming = false,
                    FirstTokenLatencyMs = forwardResult.FirstTokenLatencyMs,
                    StreamDurationMs = forwardResult.StreamDurationMs,
                    TotalDurationMs = forwardResult.TotalDurationMs
                }, cancellationToken);

                if (forwardResult.Success)
                {
                    sw.Stop();
                    _circuitStore.Succeed(route.RouteId);
                    var payload = ExtractChatPayload(forwardResult.ResponseBody, route.ProtocolType);
                    return Ok(BuildSuccessResult(requestId, payload.Content, payload.ReasoningContent, request.EnableReasoning, false, forwardResult, attempts, sw.ElapsedMilliseconds));
                }

                _circuitStore.Block(route.RouteId);
            }

            sw.Stop();
            return Ok(new ChatSendResult
            {
                Success = false,
                RequestId = requestId,
                Error = "所有路由站点均请求失败",
                DurationMs = sw.ElapsedMilliseconds,
                ReasoningEnabled = request.EnableReasoning,
                IsStreaming = false,
                Attempts = attempts
            });
        }

        return await SendFallback(request, model, runtimeSettings, cancellationToken);
    }

    // 流式发送，使用 SSE 将上游内容逐步推给前端。
    [HttpPost("send-stream")]
    public async Task SendStream([FromBody] ChatSendRequest request, CancellationToken cancellationToken)
    {
        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            await WriteSseEventAsync("error", new { message = "消息不能为空" }, cancellationToken);
            return;
        }

        if (request.ModelId == Guid.Empty)
        {
            await WriteSseEventAsync("error", new { message = "请选择模型" }, cancellationToken);
            return;
        }

        var model = await _metadataCache.GetEnabledModelAsync(request.ModelId, cancellationToken);

        if (model is null)
        {
            await WriteSseEventAsync("error", new { message = "模型不存在或已禁用" }, cancellationToken);
            return;
        }

        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync(model.ModelName, cancellationToken);
        if (allRoutes.Count == 0)
        {
            await SendStreamFallbackAsync(request, model, runtimeSettings, cancellationToken);
            return;
        }

        var requestId = Guid.NewGuid();
        var attemptIndex = 0;
        var attempts = new List<ChatAttemptResult>();

        foreach (var route in allRoutes)
        {
            if (_circuitStore.IsBlocked(route.RouteId))
                continue;

            attemptIndex++;
            var streamResult = await ForwardStreamAsync(
                route.ProtocolType,
                route.BaseUrl,
                route.ApiKey,
                route.SiteModelName,
                request.Message,
                request.EnableReasoning,
                request.ReasoningEffort,
                async chunk => await WriteSseEventAsync("token", new { content = chunk }, cancellationToken),
                async chunk => await WriteSseEventAsync("reasoning", new { content = chunk }, cancellationToken),
                runtimeSettings.ProxyRequestTimeoutSeconds,
                cancellationToken);

            var attemptResult = BuildAttemptResult(
                attemptIndex,
                route.SiteName,
                route.UpstreamModelName,
                route.SiteModelName,
                new ProxyForwardResult
                {
                    Success = streamResult.Success,
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    IsStreaming = true,
                    FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                    TotalDurationMs = streamResult.TotalDurationMs,
                    ErrorMessage = streamResult.ErrorMessage,
                    StatusCode = streamResult.StatusCode
                },
                streamResult.Success);
            attempts.Add(attemptResult);

            await _usageLogService.LogAsync(new UsageLogEntry
            {
                RequestId = requestId,
                ProtocolType = route.ProtocolType,
                RequestModel = model.ModelName,
                AttemptedModel = route.UpstreamModelName,
                TargetSiteId = route.SiteId,
                Status = streamResult.Success ? "success" : "fail",
                Source = "chat",
                RetryCount = streamResult.Success ? attemptIndex - 1 : attemptIndex,
                AttemptIndex = attemptIndex,
                IsFinalResult = streamResult.Success,
                FallbackTriggered = !streamResult.Success,
                ErrorMessage = streamResult.Success ? string.Empty : streamResult.ErrorMessage,
                InputTokens = streamResult.InputTokens,
                CachedTokens = streamResult.CachedTokens,
                OutputTokens = streamResult.OutputTokens,
                IsStreaming = true,
                FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                StreamDurationMs = 0,
                TotalDurationMs = streamResult.TotalDurationMs
            }, cancellationToken);

            if (streamResult.Success)
            {
                var finalResult = new ChatSendResult
                {
                    Success = true,
                    RequestId = requestId,
                    Content = streamResult.Content,
                    ReasoningContent = streamResult.ReasoningContent,
                    DurationMs = streamResult.TotalDurationMs,
                    ReasoningEnabled = request.EnableReasoning,
                    IsStreaming = true,
                    InputTokens = streamResult.InputTokens,
                    CachedTokens = streamResult.CachedTokens,
                    OutputTokens = streamResult.OutputTokens,
                    TotalTokens = streamResult.InputTokens + streamResult.CachedTokens + streamResult.OutputTokens,
                    FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                    TotalDurationMs = streamResult.TotalDurationMs,
                    Attempts = attempts
                };
                await WriteSseEventAsync("meta", finalResult, cancellationToken);
                await WriteSseEventAsync("done", new { requestId }, cancellationToken);
                _circuitStore.Succeed(route.RouteId);
                return;
            }

            _circuitStore.Block(route.RouteId);
            if (streamResult.HadAnyContent)
            {
                await WriteSseEventAsync("error", new { message = streamResult.ErrorMessage }, cancellationToken);
                return;
            }
        }

        await WriteSseEventAsync("error", new { message = "所有路由站点均请求失败", attempts }, cancellationToken);
    }

    // 回退逻辑：没有路由规则时直接通过站点映射发送
    private async Task<IActionResult> SendFallback(
        ChatSendRequest request,
        CachedEnabledModel model,
        CachedProxyRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var mapping = await _metadataCache.GetFallbackTargetAsync(request.ModelId, cancellationToken);

        if (mapping is null)
            return Ok(new ChatSendResult { Success = false, Error = "该模型没有可用的站点映射", ReasoningEnabled = request.EnableReasoning });

        var requestId = Guid.NewGuid();
        var requestBody = BuildChatRequestBody(mapping.ProtocolType, mapping.SiteModelName, request.Message, request.EnableReasoning, false, request.ReasoningEffort);
        var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = mapping.BaseUrl,
            TargetApiKey = mapping.ApiKey,
            ProtocolType = mapping.ProtocolType,
            TargetModelName = mapping.SiteModelName,
            RequestBody = requestBody,
            PreparedRequestBody = requestBody,
            EnableStreaming = false,
            RequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
            RetryCount = runtimeSettings.ProxyRetryCount
        }, cancellationToken);

        sw.Stop();

        await _usageLogService.LogAsync(new UsageLogEntry
        {
            RequestId = requestId,
            ProtocolType = mapping.ProtocolType,
            RequestModel = model.ModelName,
            AttemptedModel = mapping.SiteModelName,
            TargetSiteId = mapping.SiteId,
            Status = forwardResult.Success ? "success" : "fail",
            Source = "chat",
            RetryCount = 0,
            AttemptIndex = 1,
            IsFinalResult = true,
            FallbackTriggered = false,
            ErrorMessage = forwardResult.Success ? string.Empty : (forwardResult.ErrorMessage ?? string.Empty),
            InputTokens = forwardResult.InputTokens,
            CachedTokens = forwardResult.CachedTokens,
            OutputTokens = forwardResult.OutputTokens,
            IsStreaming = false,
            FirstTokenLatencyMs = forwardResult.FirstTokenLatencyMs,
            StreamDurationMs = forwardResult.StreamDurationMs,
            TotalDurationMs = forwardResult.TotalDurationMs
        }, cancellationToken);

        var attempts = new List<ChatAttemptResult>
        {
            BuildAttemptResult(1, mapping.SiteName, mapping.SiteModelName, mapping.SiteModelName, forwardResult, true)
        };

        if (!forwardResult.Success)
        {
            return Ok(new ChatSendResult
            {
                Success = false,
                RequestId = requestId,
                Error = forwardResult.ErrorMessage ?? "上游请求失败",
                DurationMs = sw.ElapsedMilliseconds,
                ReasoningEnabled = request.EnableReasoning,
                IsStreaming = false,
                Attempts = attempts
            });
        }

        var payload = ExtractChatPayload(forwardResult.ResponseBody, mapping.ProtocolType);
        return Ok(BuildSuccessResult(requestId, payload.Content, payload.ReasoningContent, request.EnableReasoning, false, forwardResult, attempts, sw.ElapsedMilliseconds));
    }

    // 流式回退逻辑：没有路由规则时直接通过站点映射流式发送。
    private async Task SendStreamFallbackAsync(
        ChatSendRequest request,
        CachedEnabledModel model,
        CachedProxyRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        var mapping = await _metadataCache.GetFallbackTargetAsync(request.ModelId, cancellationToken);

        if (mapping is null)
        {
            await WriteSseEventAsync("error", new { message = "该模型没有可用的站点映射" }, cancellationToken);
            return;
        }

        var requestId = Guid.NewGuid();
        var streamResult = await ForwardStreamAsync(
            mapping.ProtocolType,
            mapping.BaseUrl,
            mapping.ApiKey,
            mapping.SiteModelName,
            request.Message,
            request.EnableReasoning,
            request.ReasoningEffort,
            async chunk => await WriteSseEventAsync("token", new { content = chunk }, cancellationToken),
            async chunk => await WriteSseEventAsync("reasoning", new { content = chunk }, cancellationToken),
            runtimeSettings.ProxyRequestTimeoutSeconds,
            cancellationToken);

        var attemptResult = BuildAttemptResult(
            1,
            mapping.SiteName,
            mapping.SiteModelName,
            mapping.SiteModelName,
            new ProxyForwardResult
            {
                Success = streamResult.Success,
                InputTokens = streamResult.InputTokens,
                CachedTokens = streamResult.CachedTokens,
                OutputTokens = streamResult.OutputTokens,
                IsStreaming = true,
                FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
                TotalDurationMs = streamResult.TotalDurationMs,
                ErrorMessage = streamResult.ErrorMessage,
                StatusCode = streamResult.StatusCode
            },
            true);
        var attempts = new List<ChatAttemptResult> { attemptResult };

        await _usageLogService.LogAsync(new UsageLogEntry
        {
            RequestId = requestId,
            ProtocolType = mapping.ProtocolType,
            RequestModel = model.ModelName,
            AttemptedModel = mapping.SiteModelName,
            TargetSiteId = mapping.SiteId,
            Status = streamResult.Success ? "success" : "fail",
            Source = "chat",
            RetryCount = 0,
            AttemptIndex = 1,
            IsFinalResult = true,
            FallbackTriggered = false,
            ErrorMessage = streamResult.Success ? string.Empty : streamResult.ErrorMessage,
            InputTokens = streamResult.InputTokens,
            CachedTokens = streamResult.CachedTokens,
            OutputTokens = streamResult.OutputTokens,
            IsStreaming = true,
            FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
            StreamDurationMs = 0,
            TotalDurationMs = streamResult.TotalDurationMs
        }, cancellationToken);

        if (!streamResult.Success)
        {
            await WriteSseEventAsync("error", new { message = streamResult.ErrorMessage ?? "上游请求失败", attempts }, cancellationToken);
            return;
        }

        var finalResult = new ChatSendResult
        {
            Success = true,
            RequestId = requestId,
            Content = streamResult.Content,
            ReasoningContent = streamResult.ReasoningContent,
            DurationMs = streamResult.TotalDurationMs,
            ReasoningEnabled = request.EnableReasoning,
            IsStreaming = true,
            InputTokens = streamResult.InputTokens,
            CachedTokens = streamResult.CachedTokens,
            OutputTokens = streamResult.OutputTokens,
            TotalTokens = streamResult.InputTokens + streamResult.CachedTokens + streamResult.OutputTokens,
            FirstTokenLatencyMs = streamResult.FirstTokenLatencyMs,
            TotalDurationMs = streamResult.TotalDurationMs,
            Attempts = attempts
        };

        await WriteSseEventAsync("meta", finalResult, cancellationToken);
        await WriteSseEventAsync("done", new { requestId }, cancellationToken);
    }

    // 使用 HttpClient 按 SSE 方式读取上游响应，并实时转发到前端。
    private async Task<ChatStreamForwardResult> ForwardStreamAsync(
        string protocolType,
        string baseUrl,
        string apiKey,
        string targetModelName,
        string message,
        bool enableReasoning,
        string reasoningEffort,
        Func<string, Task> onContentChunk,
        Func<string, Task> onReasoningChunk,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, requestTimeoutSeconds)));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var targetUrl = protocolType == "Anthropic"
                ? $"{baseUrl.TrimEnd('/')}/v1/messages"
                : $"{baseUrl.TrimEnd('/')}/v1/chat/completions";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = new StringContent(BuildChatRequestBody(protocolType, targetModelName, message, enableReasoning, true, reasoningEffort), Encoding.UTF8, "application/json")
            };

            if (protocolType == "Anthropic")
            {
                httpRequest.Headers.Add("x-api-key", apiKey);
                httpRequest.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                return new ChatStreamForwardResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                    ErrorMessage = string.IsNullOrWhiteSpace(errorBody) ? $"上游返回 {(int)response.StatusCode}" : errorBody
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var dataLines = new List<string>();
            var currentEvent = string.Empty;
            var state = new SseBlockProcessState();
            var done = false;

            while (!done)
            {
                var line = await reader.ReadLineAsync(timeoutCts.Token);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    done = await ProcessSseBlockAsync(
                        protocolType,
                        currentEvent,
                        dataLines,
                        stopwatch,
                        onContentChunk,
                        onReasoningChunk,
                        contentBuilder,
                        reasoningBuilder,
                        state,
                        timeoutCts.Token);
                    currentEvent = string.Empty;
                    dataLines.Clear();
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    currentEvent = line[6..].Trim();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    dataLines.Add(line[5..].TrimStart());
                }
            }

            if (!done && dataLines.Count > 0)
            {
                await ProcessSseBlockAsync(
                    protocolType,
                    currentEvent,
                    dataLines,
                    stopwatch,
                    onContentChunk,
                    onReasoningChunk,
                    contentBuilder,
                    reasoningBuilder,
                    state,
                    timeoutCts.Token);
            }

            stopwatch.Stop();
            return new ChatStreamForwardResult
            {
                Success = state.HadAnyContent || contentBuilder.Length > 0 || reasoningBuilder.Length > 0,
                HadAnyContent = state.HadAnyContent,
                Content = contentBuilder.ToString(),
                ReasoningContent = reasoningBuilder.ToString(),
                InputTokens = state.InputTokens,
                CachedTokens = state.CachedTokens,
                OutputTokens = state.OutputTokens,
                FirstTokenLatencyMs = state.FirstTokenLatencyMs,
                TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                StatusCode = (int)response.StatusCode,
                ErrorMessage = state.HadAnyContent ? string.Empty : "上游未返回可用内容"
            };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ChatStreamForwardResult
            {
                Success = false,
                TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                ErrorMessage = $"Request timed out after {requestTimeoutSeconds}s: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ChatStreamForwardResult
            {
                Success = false,
                TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                ErrorMessage = ex.Message
            };
        }
    }

    private static ChatSendResult BuildSuccessResult(
        Guid requestId,
        string content,
        string reasoningContent,
        bool reasoningEnabled,
        bool isStreaming,
        ProxyForwardResult forwardResult,
        List<ChatAttemptResult> attempts,
        long durationMs)
    {
        return new ChatSendResult
        {
            Success = true,
            RequestId = requestId,
            Content = content,
            ReasoningContent = reasoningContent,
            DurationMs = durationMs,
            ReasoningEnabled = reasoningEnabled,
            IsStreaming = isStreaming,
            InputTokens = forwardResult.InputTokens,
            CachedTokens = forwardResult.CachedTokens,
            OutputTokens = forwardResult.OutputTokens,
            TotalTokens = forwardResult.InputTokens + forwardResult.CachedTokens + forwardResult.OutputTokens,
            FirstTokenLatencyMs = forwardResult.FirstTokenLatencyMs,
            TotalDurationMs = forwardResult.TotalDurationMs,
            Attempts = attempts
        };
    }

    private static ChatAttemptResult BuildAttemptResult(
        int attemptIndex,
        string siteName,
        string attemptedModel,
        string siteModelName,
        ProxyForwardResult forwardResult,
        bool isFinalResult)
    {
        return new ChatAttemptResult
        {
            AttemptIndex = attemptIndex,
            SiteName = siteName,
            AttemptedModel = attemptedModel,
            SiteModelName = siteModelName,
            Status = forwardResult.Success ? "success" : "fail",
            ErrorMessage = forwardResult.ErrorMessage ?? string.Empty,
            IsFinalResult = isFinalResult,
            IsStreaming = forwardResult.IsStreaming,
            InputTokens = forwardResult.InputTokens,
            CachedTokens = forwardResult.CachedTokens,
            OutputTokens = forwardResult.OutputTokens,
            TotalTokens = forwardResult.InputTokens + forwardResult.CachedTokens + forwardResult.OutputTokens,
            FirstTokenLatencyMs = forwardResult.FirstTokenLatencyMs,
            TotalDurationMs = forwardResult.TotalDurationMs
        };
    }

    private static string BuildChatRequestBody(string protocolType, string modelName, string message, bool enableReasoning, bool enableStreaming, string reasoningEffort = "high")
    {
        if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var anthropicPayload = new Dictionary<string, object?>
            {
                ["model"] = modelName,
                ["messages"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = message
                    }
                },
                ["stream"] = enableStreaming,
                ["max_tokens"] = 4096
            };

            if (enableReasoning)
            {
                // Anthropic 根据 thinking level 映射 budget_tokens
                var budgetTokens = reasoningEffort switch
                {
                    "low" => 1024,
                    "medium" => 4096,
                    _ => 8192
                };
                anthropicPayload["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = budgetTokens
                };
            }

            return JsonSerializer.Serialize(anthropicPayload);
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = message
                }
            },
            ["stream"] = enableStreaming,
            ["max_tokens"] = 4096
        };

        if (enableStreaming)
        {
            payload["stream_options"] = new Dictionary<string, object?>
            {
                ["include_usage"] = true
            };
        }

        if (enableReasoning)
        {
            // OpenAI 兼容协议使用 reasoning_effort 参数
            var effort = ChatSendRequest.ValidReasoningEfforts.Contains(reasoningEffort) ? reasoningEffort : "high";
            payload["reasoning_effort"] = effort;
        }

        return JsonSerializer.Serialize(payload);
    }

    private sealed class SseBlockProcessState
    {
        public int FirstTokenLatencyMs { get; set; }
        public int InputTokens { get; set; }
        public int CachedTokens { get; set; }
        public int OutputTokens { get; set; }
        public bool HadAnyContent { get; set; }
    }

    // 处理单个 SSE block，并抽取内容、思考与 usage。
    private static async Task<bool> ProcessSseBlockAsync(
        string protocolType,
        string eventName,
        List<string> dataLines,
        Stopwatch stopwatch,
        Func<string, Task> onContentChunk,
        Func<string, Task> onReasoningChunk,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder,
        SseBlockProcessState state,
        CancellationToken cancellationToken)
    {
        if (dataLines.Count == 0)
        {
            return false;
        }

        var data = string.Join("\n", dataLines);
        if (data == "[DONE]")
        {
            return true;
        }

        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (protocolType == "Anthropic")
        {
            if (eventName == "message_start" && root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var usage))
            {
                state.InputTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : state.InputTokens;
                state.OutputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : state.OutputTokens;
            }
            else if (eventName == "message_delta" && root.TryGetProperty("usage", out var deltaUsage))
            {
                state.OutputTokens = deltaUsage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : state.OutputTokens;
            }
            else if (eventName == "content_block_delta" && root.TryGetProperty("delta", out var delta))
            {
                var deltaType = delta.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                if (string.Equals(deltaType, "thinking_delta", StringComparison.OrdinalIgnoreCase))
                {
                    var reasoningChunk = ExtractElementText(delta, "thinking", "text", "content");
                    if (!string.IsNullOrWhiteSpace(reasoningChunk))
                    {
                        if (state.FirstTokenLatencyMs == 0)
                        {
                            state.FirstTokenLatencyMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
                        }
                        reasoningBuilder.Append(reasoningChunk);
                        state.HadAnyContent = true;
                        await onReasoningChunk(reasoningChunk);
                    }
                }
                else
                {
                    var contentChunk = ExtractElementText(delta, "text", "content");
                    if (!string.IsNullOrWhiteSpace(contentChunk))
                    {
                        if (state.FirstTokenLatencyMs == 0)
                        {
                            state.FirstTokenLatencyMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
                        }
                        contentBuilder.Append(contentChunk);
                        state.HadAnyContent = true;
                        await onContentChunk(contentChunk);
                    }
                }
            }

            return false;
        }

        if (root.TryGetProperty("usage", out var rootUsage))
        {
            (state.InputTokens, state.CachedTokens, state.OutputTokens) = ExtractUsageMetrics(rootUsage, state.InputTokens, state.CachedTokens, state.OutputTokens);
        }

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta))
            {
                var reasoningChunk = ExtractReasoningText(delta);
                if (!string.IsNullOrWhiteSpace(reasoningChunk))
                {
                    if (state.FirstTokenLatencyMs == 0)
                    {
                        state.FirstTokenLatencyMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
                    }
                    reasoningBuilder.Append(reasoningChunk);
                    state.HadAnyContent = true;
                    await onReasoningChunk(reasoningChunk);
                }

                var contentChunk = ExtractDeltaContent(delta);
                if (!string.IsNullOrWhiteSpace(contentChunk))
                {
                    if (state.FirstTokenLatencyMs == 0)
                    {
                        state.FirstTokenLatencyMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
                    }
                    contentBuilder.Append(contentChunk);
                    state.HadAnyContent = true;
                    await onContentChunk(contentChunk);
                }
            }
        }

        return false;
    }

    // 尽量兼容不同上游返回格式，同时提取正常回答和思考内容。
    private static (string Content, string ReasoningContent) ExtractChatPayload(string responseBody, string protocolType)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var contentParts = new List<string>();
            var reasoningParts = new List<string>();

            if (protocolType == "Anthropic")
            {
                if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in contentArr.EnumerateArray())
                    {
                        var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                        if (string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
                        {
                            AppendIfNotEmpty(reasoningParts, ExtractElementText(item, "thinking", "text", "content"));
                            continue;
                        }

                        AppendIfNotEmpty(contentParts, ExtractElementText(item, "text", "content"));
                    }
                }
            }
            else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message))
                {
                    AppendIfNotEmpty(reasoningParts, ExtractReasoningText(message));

                    if (message.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind == JsonValueKind.String)
                        {
                            AppendIfNotEmpty(contentParts, content.GetString());
                        }
                        else if (content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in content.EnumerateArray())
                            {
                                var itemType = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                                if (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(itemType, "thinking", StringComparison.OrdinalIgnoreCase))
                                {
                                    AppendIfNotEmpty(reasoningParts, ExtractReasoningText(item));
                                    continue;
                                }

                                AppendIfNotEmpty(contentParts, ExtractElementText(item, "text", "content"));
                            }
                        }
                    }
                }
            }

            var contentText = string.Join("\n", contentParts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            var reasoningText = string.Join("\n", reasoningParts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            return (string.IsNullOrWhiteSpace(contentText) ? responseBody : contentText, reasoningText);
        }
        catch
        {
        }

        return (responseBody, string.Empty);
    }

    // SSE 序列化使用 camelCase 命名策略，与前端 JS 属性名保持一致
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task WriteSseEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, SseJsonOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static (int InputTokens, int CachedTokens, int OutputTokens) ExtractUsageMetrics(JsonElement usage, int currentInput, int currentCached, int currentOutput)
    {
        var inputTokens = usage.TryGetProperty("prompt_tokens", out var promptTokens) ? promptTokens.GetInt32() : currentInput;
        var outputTokens = usage.TryGetProperty("completion_tokens", out var completionTokens) ? completionTokens.GetInt32() : currentOutput;
        var cachedTokens = currentCached;

        if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails) &&
            promptDetails.ValueKind == JsonValueKind.Object &&
            promptDetails.TryGetProperty("cached_tokens", out var cached))
        {
            cachedTokens = cached.GetInt32();
        }

        return (inputTokens, cachedTokens, outputTokens);
    }

    private static string ExtractDeltaContent(JsonElement delta)
    {
        if (delta.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    AppendIfNotEmpty(parts, ExtractElementText(item, "text", "content"));
                }
                return string.Join(string.Empty, parts);
            }
        }

        return string.Empty;
    }

    // 兼容不同 OpenAI 风格上游的思考字段，优先提取可直接展示的文本。
    private static string ExtractReasoningText(JsonElement element)
    {
        var directText = ExtractElementText(element, "reasoning_content", "reasoning", "thinking", "summary_text", "output_text", "text");
        if (!string.IsNullOrWhiteSpace(directText))
        {
            return directText;
        }

        if (element.TryGetProperty("reasoning_details", out var reasoningDetails))
        {
            var detailText = ExtractElementText(reasoningDetails, "text", "content", "value", "summary_text", "output_text");
            if (!string.IsNullOrWhiteSpace(detailText))
            {
                return detailText;
            }
        }

        if (element.TryGetProperty("summary", out var summary))
        {
            var summaryText = ExtractElementText(summary, "text", "content", "value", "summary_text", "output_text");
            if (!string.IsNullOrWhiteSpace(summaryText))
            {
                return summaryText;
            }
        }

        return string.Empty;
    }

    private static string ExtractElementText(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    AppendIfNotEmpty(parts, ExtractElementText(item, "text", "content", "value"));
                }
                if (parts.Count > 0)
                {
                    return string.Join("\n", parts);
                }
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = ExtractElementText(value, "text", "content", "value");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return string.Empty;
    }

    private static void AppendIfNotEmpty(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value.Trim());
        }
    }
}
