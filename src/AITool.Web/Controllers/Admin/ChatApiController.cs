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

/// <summary>
/// 调试对话页的模型选择项，包含模型标识、显示名称和可用站点数量。
/// </summary>
public sealed class ChatModelItem
{
    /// <summary>
    /// 模型标识。
    /// </summary>
    public Guid ModelId { get; set; }
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 可用站点数量。
    /// </summary>
    public int AvailableSiteCount { get; set; }
}

/// <summary>
/// 单次路由尝试的结果，记录该次尝试调用的站点、模型、状态和 Token 用量。
/// </summary>
public sealed class ChatAttemptResult
{
    /// <summary>
    /// 尝试序号。
    /// </summary>
    public int AttemptIndex { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 尝试调用的模型名称。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 站点侧模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 请求状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
    /// <summary>
    /// 是否为最终结果。
    /// </summary>
    public bool IsFinalResult { get; set; }
    /// <summary>
    /// 是否流式返回。
    /// </summary>
    public bool IsStreaming { get; set; }
    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }
    /// <summary>
    /// Token 总数。
    /// </summary>
    public int TotalTokens { get; set; }
    /// <summary>
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
    /// <summary>
    /// 请求体内容。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;
    /// <summary>
    /// 响应体内容。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;
}

/// <summary>
/// 发送调试对话消息的请求参数，指定目标模型、消息内容和输出模式。
/// </summary>
public sealed class ChatSendRequest
{
    /// <summary>
    /// 支持的思考强度选项。
    /// </summary>
    public static readonly string[] ValidReasoningEfforts = ["low", "medium", "high", "xhigh","max"];

    /// <summary>
    /// 模型标识。
    /// </summary>
    public Guid ModelId { get; set; }
    /// <summary>
    /// 用户消息内容。
    /// </summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>
    /// 是否开启思考。
    /// </summary>
    public bool EnableReasoning { get; set; }
    /// <summary>
    /// 是否开启流式输出。
    /// </summary>
    public bool EnableStreaming { get; set; }
    /// <summary>
    /// 思考强度。
    /// </summary>
    public string ReasoningEffort { get; set; } = "high";
}

/// <summary>
/// 调试对话请求的最终响应，包含返回内容、思考内容、Token 用量和所有尝试详情。
/// </summary>
public sealed class ChatSendResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    public bool Success { get; set; }
    /// <summary>
    /// 请求标识。
    /// </summary>
    public Guid? RequestId { get; set; }
    /// <summary>
    /// 返回内容。
    /// </summary>
    public string Content { get; set; } = string.Empty;
    /// <summary>
    /// 思考内容。
    /// </summary>
    public string ReasoningContent { get; set; } = string.Empty;
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string? Error { get; set; }
    /// <summary>
    /// 耗时（毫秒）。
    /// </summary>
    public long DurationMs { get; set; }
    /// <summary>
    /// 是否启用思考。
    /// </summary>
    public bool ReasoningEnabled { get; set; }
    /// <summary>
    /// 是否流式返回。
    /// </summary>
    public bool IsStreaming { get; set; }
    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }
    /// <summary>
    /// Token 总数。
    /// </summary>
    public int TotalTokens { get; set; }
    /// <summary>
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
    /// <summary>
    /// 尝试详情。
    /// </summary>
    public List<ChatAttemptResult> Attempts { get; set; } = [];
}

/// <summary>
/// 流式对话转发的内部结果，记录流式响应的拼接内容、Token 用量和原始请求/响应体。
/// </summary>
internal sealed class ChatStreamForwardResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    public bool Success { get; set; }
    /// <summary>
    /// 是否收到过有效内容。
    /// </summary>
    public bool HadAnyContent { get; set; }
    /// <summary>
    /// 返回内容。
    /// </summary>
    public string Content { get; set; } = string.Empty;
    /// <summary>
    /// 思考内容。
    /// </summary>
    public string ReasoningContent { get; set; } = string.Empty;
    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }
    /// <summary>
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
    /// <summary>
    /// 状态码。
    /// </summary>
    public int StatusCode { get; set; }
    /// <summary>
    /// 请求体内容。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;
    /// <summary>
    /// 响应体内容。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;
}

/// <summary>
/// 调试对话控制器，提供模型选择和非流式/流式调试请求功能。
/// </summary>
[ApiController]
[Route("api/admin/chat")]
public sealed class ChatApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理转发服务。
    /// </summary>
    private readonly IProxyForwardService _forwardService;
    /// <summary>
    /// 路由熔断状态存储。
    /// </summary>
    private readonly RouteCircuitStateStore _circuitStore;
    /// <summary>
    /// 用量日志服务。
    /// </summary>
    private readonly IUsageLogService _usageLogService;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>
    /// HttpClient 工厂。
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// 创建调试对话控制器。
    /// </summary>
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

    /// <summary>
    /// 获取可调试的模型列表。
    /// </summary>
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        var models = await _metadataCache.GetChatModelsAsync(cancellationToken);
        return Ok(models);
    }

    /// <summary>
    /// 发送非流式调试请求。
    /// </summary>
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

                attempts.Add(BuildAttemptResult(attemptIndex, route.SiteName, route.UpstreamModelName, route.SiteModelName, forwardResult, forwardResult.Success, requestBody, forwardResult.ResponseBody ?? ""));

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
                    TotalDurationMs = forwardResult.TotalDurationMs,
                    ReasoningEffort = request.EnableReasoning ? request.ReasoningEffort : string.Empty
                }, cancellationToken);

                if (forwardResult.Success)
                {
                    sw.Stop();
                    _circuitStore.Succeed(route.RouteId);
                    var payload = ExtractChatPayload(forwardResult.ResponseBody ?? string.Empty, route.ProtocolType);
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

    /// <summary>
    /// 发送流式调试请求。
    /// </summary>
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
                streamResult.Success,
                streamResult.RequestBody,
                streamResult.ResponseBody);
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
                TotalDurationMs = streamResult.TotalDurationMs,
                ReasoningEffort = request.EnableReasoning ? request.ReasoningEffort : string.Empty
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

    /// <summary>
    /// 使用兜底映射发送非流式请求。
    /// </summary>
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
            TotalDurationMs = forwardResult.TotalDurationMs,
            ReasoningEffort = request.EnableReasoning ? request.ReasoningEffort : string.Empty
        }, cancellationToken);

        var attempts = new List<ChatAttemptResult>
        {
            BuildAttemptResult(1, mapping.SiteName, mapping.SiteModelName, mapping.SiteModelName, forwardResult, true, requestBody, forwardResult.ResponseBody ?? "")
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

        var payload = ExtractChatPayload(forwardResult.ResponseBody ?? string.Empty, mapping.ProtocolType);
        return Ok(BuildSuccessResult(requestId, payload.Content, payload.ReasoningContent, request.EnableReasoning, false, forwardResult, attempts, sw.ElapsedMilliseconds));
    }

    /// <summary>
    /// 使用兜底映射发送流式请求。
    /// </summary>
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
            true,
            streamResult.RequestBody,
            streamResult.ResponseBody);
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
            TotalDurationMs = streamResult.TotalDurationMs,
            ReasoningEffort = request.EnableReasoning ? request.ReasoningEffort : string.Empty
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

    /// <summary>
    /// 转发流式对话请求。
    /// </summary>
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
        // 提前构建请求体，供调用方记录到尝试详情
        var requestBody = BuildChatRequestBody(protocolType, targetModelName, message, enableReasoning, true, reasoningEffort);

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
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
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
                    ErrorMessage = string.IsNullOrWhiteSpace(errorBody) ? $"上游返回 {(int)response.StatusCode}" : errorBody,
                    RequestBody = requestBody,
                    ResponseBody = errorBody
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
            // 流式响应的原始 body 已逐块消费，无法完整保留，用内容摘要替代
            var responseBodySummary = $"[SSE streaming] content={contentBuilder.Length} chars, reasoning={reasoningBuilder.Length} chars, input={state.InputTokens}, output={state.OutputTokens}";
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
                ErrorMessage = state.HadAnyContent ? string.Empty : "上游未返回可用内容",
                RequestBody = requestBody,
                ResponseBody = responseBodySummary
            };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ChatStreamForwardResult
            {
                Success = false,
                TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                ErrorMessage = $"Request timed out after {requestTimeoutSeconds}s: {ex.Message}",
                RequestBody = requestBody
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ChatStreamForwardResult
            {
                Success = false,
                TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                ErrorMessage = ex.Message,
                RequestBody = requestBody
            };
        }
    }

    /// <summary>
    /// 组装成功响应结果。
    /// </summary>
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

    /// <summary>
    /// 组装单次尝试结果。
    /// </summary>
    private static ChatAttemptResult BuildAttemptResult(
        int attemptIndex,
        string siteName,
        string attemptedModel,
        string siteModelName,
        ProxyForwardResult forwardResult,
        bool isFinalResult,
        string requestBody = "",
        string responseBody = "")
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
            TotalDurationMs = forwardResult.TotalDurationMs,
            RequestBody = requestBody,
            ResponseBody = responseBody
        };
    }

    /// <summary>
    /// 构建对话请求体。
    /// </summary>
    private static string BuildChatRequestBody(string protocolType, string modelName, string message, bool enableReasoning, bool enableStreaming, string reasoningEffort = "high")
    {
        // 规范化思考等级
        var effort = ChatSendRequest.ValidReasoningEfforts.Contains(reasoningEffort) ? reasoningEffort : "high";

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
                // Anthropic 使用 thinking 配置，budget_tokens 按等级映射
                var budgetTokens = effort switch
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
            payload["reasoning_effort"] = effort;
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// SSE 数据块处理过程中的累积状态，用于跟踪首 Token 延迟和 Token 用量。
    /// </summary>
    private sealed class SseBlockProcessState
    {
        /// <summary>
        /// 首 Token 延迟（毫秒）。
        /// </summary>
        public int FirstTokenLatencyMs { get; set; }
        /// <summary>
        /// 输入 Token 数。
        /// </summary>
        public int InputTokens { get; set; }
        /// <summary>
        /// 缓存 Token 数。
        /// </summary>
        public int CachedTokens { get; set; }
        /// <summary>
        /// 输出 Token 数。
        /// </summary>
        public int OutputTokens { get; set; }
        /// <summary>
        /// 是否收到过有效内容。
        /// </summary>
        public bool HadAnyContent { get; set; }
    }

    /// <summary>
    /// 处理单个 SSE 数据块。
    /// </summary>
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

    /// <summary>
    /// 提取非流式对话响应内容。
    /// </summary>
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

    /// <summary>
    /// SSE 序列化选项。
    /// </summary>
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 写入 SSE 事件。
    /// </summary>
    private async Task WriteSseEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, SseJsonOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 提取非流式对话响应内容。
    /// </summary>
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

    /// <summary>
    /// 提取增量内容文本。
    /// </summary>
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

    /// <summary>
    /// 提取思考文本。
    /// </summary>
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

    /// <summary>
    /// 提取节点文本内容。
    /// </summary>
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

    /// <summary>
    /// 在非空时追加文本。
    /// </summary>
    private static void AppendIfNotEmpty(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value.Trim());
        }
    }
}
