using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 代理调用统一记录服务实现，从一份 <see cref="ProxyCallContext"/> 派发数据到
/// <see cref="DeveloperInvocationTraceStore"/> 和 <see cref="IUsageLogService"/> 两个存储。
/// <para>
/// 所有方法内部均捕获异常并记录日志，确保记录失败不影响代理主链路。
/// </para>
/// </summary>
public sealed class ProxyCallRecorder : IProxyCallRecorder
{
    private readonly DeveloperInvocationTraceStore _traceStore;
    private readonly IUsageLogService _usageLogService;
    private readonly ILogger<ProxyCallRecorder> _logger;

    public ProxyCallRecorder(
        DeveloperInvocationTraceStore traceStore,
        IUsageLogService usageLogService,
        ILogger<ProxyCallRecorder> logger)
    {
        _traceStore = traceStore;
        _usageLogService = usageLogService;
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
                RequestHeaders = context.RequestHeaders,
                AccessKeyId = context.AccessKeyId,
                ReasoningEffort = context.ReasoningEffort
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
                TotalDurationMs = context.TotalDurationMs,
                FirstTokenLatencyMs = context.FirstTokenLatencyMs,
                StreamDurationMs = context.StreamDurationMs,
                IsStreamInterrupted = context.IsStreamInterrupted,
                RetryCount = context.RetryCount,
                IsFinalResult = context.IsFinalResult,
                FallbackTriggered = context.FallbackTriggered
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
    public void CancelTrace(Guid? traceId, string reason)
    {
        if (!traceId.HasValue)
        {
            return;
        }

        try
        {
            _traceStore.CancelPending(traceId.Value, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "取消开发者调用追踪失败，忽略。TraceId={TraceId}, Reason={Reason}",
                traceId,
                reason);
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
}
