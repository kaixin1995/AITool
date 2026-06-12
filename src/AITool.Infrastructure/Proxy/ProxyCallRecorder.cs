using AITool.Application.Conversations;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Conversations;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 代理调用统一记录服务实现，从一份 <see cref="ProxyCallContext"/> 派发数据到
/// <see cref="DeveloperInvocationTraceStore"/>、<see cref="IUsageLogService"/>、
/// <see cref="IConversationLogService"/> 三个存储。
/// <para>
/// 所有方法内部均捕获异常并记录日志，确保记录失败不影响代理主链路。
/// </para>
/// </summary>
public sealed class ProxyCallRecorder : IProxyCallRecorder
{
    private readonly DeveloperInvocationTraceStore _traceStore;
    private readonly IUsageLogService _usageLogService;
    private readonly IConversationLogService _conversationLogService;
    private readonly ConversationExtractionService _conversationExtractionService;
    private readonly ILogger<ProxyCallRecorder> _logger;

    public ProxyCallRecorder(
        DeveloperInvocationTraceStore traceStore,
        IUsageLogService usageLogService,
        IConversationLogService conversationLogService,
        ConversationExtractionService conversationExtractionService,
        ILogger<ProxyCallRecorder> logger)
    {
        _traceStore = traceStore;
        _usageLogService = usageLogService;
        _conversationLogService = conversationLogService;
        _conversationExtractionService = conversationExtractionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Guid? BeginTrace(ProxyCallContext context)
    {
        try
        {
            return _traceStore.AddRequest(new DeveloperInvocationTraceRequest
            {
                RequestId = context.RequestId,
                Source = context.Source,
                UserAgent = context.UserAgent,
                ClientIp = context.ClientIp,
                ProtocolType = context.ProtocolType,
                RequestPath = context.RequestPath,
                RequestModel = context.RequestModel,
                RequestBody = DeveloperInvocationTraceStore.FormatBody(context.RequestBody),
                RequestHeaders = context.RequestHeaders
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "创建开发者调用追踪失败，但请求继续转发。Protocol={Protocol}, RequestModel={RequestModel}",
                context.ProtocolType,
                context.RequestModel);
            return null;
        }
    }

    /// <inheritdoc />
    public Guid BeginTraceAttempt(Guid? traceId, ProxyCallContext context)
    {
        if (!traceId.HasValue)
        {
            return Guid.Empty;
        }

        try
        {
            return _traceStore.AddAttempt(traceId.Value, new DeveloperInvocationAttempt
            {
                AttemptedModel = context.AttemptedModel,
                UpstreamProtocolType = context.UpstreamProtocolType,
                ForwardingMode = context.ForwardingMode,
                TargetSiteId = context.TargetSiteId,
                TargetSiteName = context.TargetSiteName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "创建开发者调用追踪尝试失败，但请求继续转发。RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                context.RequestModel,
                context.AttemptedModel);
            return Guid.Empty;
        }
    }

    /// <inheritdoc />
    public void CompleteTraceAttempt(Guid? traceId, Guid traceAttemptId, ProxyCallContext context)
    {
        if (!traceId.HasValue || traceAttemptId == Guid.Empty)
        {
            return;
        }

        try
        {
            // 开发者追踪使用的响应体：优先使用适配后的响应体，无则使用原始响应体
            var traceResponseBody = !string.IsNullOrEmpty(context.AdaptedResponseBody)
                ? context.AdaptedResponseBody
                : context.ResponseBody;

            _traceStore.CompleteAttempt(traceId.Value, traceAttemptId, new DeveloperInvocationResult
            {
                Status = context.Success ? "success" : "fail",
                StatusCode = context.StatusCode,
                ErrorMessage = context.ErrorMessage,
                ResponseBody = DeveloperInvocationTraceStore.FormatBody(traceResponseBody),
                ResponseContentType = context.ResponseContentType,
                IsStreaming = context.IsStreaming,
                InputTokens = context.InputTokens,
                CachedTokens = context.CachedTokens,
                OutputTokens = context.OutputTokens,
                TotalDurationMs = context.TotalDurationMs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "完成开发者调用追踪失败，但请求继续返回。TraceId={TraceId}, AttemptId={AttemptId}",
                traceId,
                traceAttemptId);
        }
    }

    /// <inheritdoc />
    public async Task RecordUsageAsync(ProxyCallContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _usageLogService.LogAsync(new UsageLogEntry
            {
                RequestId = context.RequestId,
                AccessKeyId = context.AccessKeyId,
                ProtocolType = context.ProtocolType,
                ForwardingMode = context.ForwardingMode,
                RequestModel = context.RequestModel,
                AttemptedModel = context.AttemptedModel,
                TargetSiteId = context.TargetSiteId,
                Status = context.Success ? "success" : "fail",
                Source = context.Source,
                RetryCount = context.RetryCount,
                AttemptIndex = context.AttemptIndex,
                IsFinalResult = context.IsFinalResult,
                FallbackTriggered = context.FallbackTriggered,
                ErrorMessage = context.Success ? string.Empty : context.ErrorMessage,
                InputTokens = context.InputTokens,
                CachedTokens = context.CachedTokens,
                OutputTokens = context.OutputTokens,
                IsStreaming = context.IsStreaming,
                IsStreamInterrupted = context.IsStreamInterrupted,
                FirstTokenLatencyMs = context.FirstTokenLatencyMs,
                StreamDurationMs = context.StreamDurationMs,
                TotalDurationMs = context.TotalDurationMs,
                ReasoningEffort = context.ReasoningEffort,
                RequestedAt = context.RequestedAt
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "记录使用日志失败，但请求继续返回。Protocol={Protocol}, RequestModel={RequestModel}, AttemptedModel={AttemptedModel}",
                context.ProtocolType,
                context.RequestModel,
                context.AttemptedModel);
        }
    }

    /// <inheritdoc />
    public async Task RecordConversationAsync(ProxyCallContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // 仅记录成功的请求
            if (!context.Success)
            {
                return;
            }

            // 从请求头中解析工具来源和会话标识
            var sourceTool = _conversationExtractionService.ResolveSourceTool(
                context.RequestHeaders.TryGetValue("X-AITool-Source", out var explicitSource) ? explicitSource : string.Empty,
                context.UserAgent);

            var sessionId = _conversationExtractionService.ExtractSessionId(context.RequestHeaders);

            // 提取用户输入和助手输出
            // 优先使用调用方预提取的值（如 Chat 调试页已有原始文本），否则从请求/响应体中解析
            var userInput = !string.IsNullOrWhiteSpace(context.PreExtractedUserInputText)
                ? context.PreExtractedUserInputText
                : _conversationExtractionService.ExtractUserInputText(
                    context.RequestBody, context.ProtocolType, context.RequestPath);
            var assistantOutput = !string.IsNullOrWhiteSpace(context.PreExtractedAssistantOutput)
                ? context.PreExtractedAssistantOutput
                : _conversationExtractionService.ExtractAssistantOutput(
                    context.ResponseBody, context.ProtocolType, context.RequestPath);
            // 仅当未预提取助手输出时，才尝试从请求体中提取工具结果
            var toolResultOutput = string.IsNullOrWhiteSpace(context.PreExtractedAssistantOutput)
                ? _conversationExtractionService.ExtractToolResultOutput(
                    context.RequestBody, context.ProtocolType, context.RequestPath)
                : string.Empty;
            var assistantOutputMarkdown = JoinConversationMarkdown(toolResultOutput, assistantOutput);

            // 两者都为空时跳过记录
            if (string.IsNullOrWhiteSpace(userInput) && string.IsNullOrWhiteSpace(assistantOutputMarkdown))
            {
                return;
            }

            // 有 sessionId 的按 sourceTool:sessionId 分组，无则合并到 sourceTool 这一组
            var groupKey = !string.IsNullOrWhiteSpace(sessionId)
                ? $"{sourceTool}:{sessionId}"
                : sourceTool;

            await _conversationLogService.LogAsync(new ConversationTurnEntry
            {
                RequestId = context.RequestId,
                CreatedAt = DateTimeOffset.UtcNow,
                UserCreatedAt = context.RequestedAt,
                SourceTool = sourceTool,
                SessionId = sessionId,
                ConversationGroupKey = groupKey,
                AccessKeyId = context.AccessKeyId,
                RequestModel = context.RequestModel,
                ProtocolType = context.ProtocolType,
                RequestPath = context.RequestPath,
                Source = context.Source,
                UserInputText = userInput,
                AssistantOutputMarkdown = assistantOutputMarkdown,
                InputTokens = context.InputTokens,
                CachedTokens = context.CachedTokens,
                OutputTokens = context.OutputTokens,
                IsStreaming = context.IsStreaming,
                Status = "success",
                MetadataJson = _conversationExtractionService.BuildMetadataJson(
                    context.UserAgent,
                    context.RequestHeaders.TryGetValue("x-app", out var xApp) ? xApp : string.Empty,
                    sessionId)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "记录结构化对话失败，但请求继续返回。Protocol={Protocol}, RequestModel={RequestModel}",
                context.ProtocolType,
                context.RequestModel);
        }
    }

    /// <summary>
    /// 合并工具结果和模型回复，避免展示时内容粘连。
    /// </summary>
    private static string JoinConversationMarkdown(params string[] values)
    {
        return string.Join("\n\n", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
    }
}
