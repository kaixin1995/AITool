using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;

namespace AITool.Core.Services;

/// <summary>
/// 统一代理请求事件发布器。
/// 在代理请求完成后，将完整的调用详情（含未截断的请求/响应体及所有尝试记录）
/// 发布为 proxy-request 事件，Admin 侧消费后在开发者调试页面展示完整的调用链路。
/// </summary>
public sealed class CoreUnifiedProxyEventPublisher
{
    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly CoreAdminEventBus _eventBus;

    /// <summary>
    /// 初始化统一代理请求事件发布器。
    /// </summary>
    public CoreUnifiedProxyEventPublisher(
        CoreEventSequenceProvider sequenceProvider,
        CoreAdminEventBus eventBus)
    {
        _sequenceProvider = sequenceProvider;
        _eventBus = eventBus;
    }

    /// <summary>
    /// 把一条调用追踪记录投影成 <see cref="CoreUnifiedProxyEvent"/> 并发布到总线。
    /// 只发布最终完成的追踪记录（Status 不为 pending），未完成的请求不会产生事件。
    /// </summary>
    public async Task PublishAsync(DeveloperInvocationTraceEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // 只有完成的请求才发布事件
        if (string.Equals(entry.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = new CoreUnifiedProxyEvent
        {
            // ── 来自 CoreUsageLogEvent ──
            RequestId = entry.RequestId,
            AccessKeyId = entry.AccessKeyId,
            ProtocolType = entry.ProtocolType,
            ForwardingMode = entry.Attempts.FirstOrDefault()?.ForwardingMode ?? string.Empty,
            RequestModel = entry.RequestModel,
            AttemptedModel = entry.AttemptedModel,
            TargetSiteId = entry.TargetSiteId,
            Status = entry.Status,
            Source = entry.Source,
            RetryCount = entry.RetryCount,
            AttemptIndex = entry.Attempts.Count > 0 ? entry.Attempts.Count - 1 : 0,
            IsFinalResult = entry.IsFinalResult,
            FallbackTriggered = entry.FallbackTriggered,
            ErrorMessage = entry.ErrorMessage ?? string.Empty,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            IsStreaming = entry.IsStreaming,
            IsStreamInterrupted = entry.IsStreamInterrupted,
            FirstTokenLatencyMs = entry.FirstTokenLatencyMs,
            StreamDurationMs = entry.StreamDurationMs,
            TotalDurationMs = entry.TotalDurationMs,
            ReasoningEffort = entry.ReasoningEffort ?? string.Empty,
            RequestedAt = entry.CreatedAt,

            // ── 来自 CoreDeveloperTraceEvent ──
            TraceId = entry.TraceId,
            TargetSiteName = entry.TargetSiteName ?? string.Empty,
            StartedAt = entry.CreatedAt,
            FinishedAt = entry.UpdatedAt,

            // ── 完整请求/响应数据（不截断） ──
            RequestBody = entry.RequestBody ?? string.Empty,
            ResponseBody = entry.ResponseBody ?? string.Empty,
            RequestHeaders = entry.RequestHeaders ?? [],
            ClientIp = entry.ClientIp ?? string.Empty,
            UserAgent = entry.UserAgent ?? string.Empty,
            RequestPath = entry.RequestPath ?? string.Empty,
            StatusCode = entry.StatusCode,
            ResponseContentType = entry.ResponseContentType ?? string.Empty,

            // ── 所有尝试明细 ──
            Attempts = entry.Attempts
                .Select((attempt, index) => new CoreUnifiedAttemptDetail
                {
                    AttemptId = attempt.AttemptId,
                    AttemptIndex = index,
                    AttemptedModel = attempt.AttemptedModel ?? string.Empty,
                    UpstreamProtocolType = attempt.UpstreamProtocolType ?? string.Empty,
                    ForwardingMode = attempt.ForwardingMode ?? string.Empty,
                    TargetSiteId = attempt.TargetSiteId ?? Guid.Empty,
                    TargetSiteName = attempt.TargetSiteName ?? string.Empty,
                    Status = attempt.Status ?? string.Empty,
                    StatusCode = attempt.StatusCode,
                    ErrorMessage = attempt.ErrorMessage ?? string.Empty,
                    ResponseBody = attempt.ResponseBody ?? string.Empty,
                    ResponseContentType = attempt.ResponseContentType ?? string.Empty,
                    IsStreaming = attempt.IsStreaming,
                    IsStreamInterrupted = attempt.IsStreamInterrupted,
                    InputTokens = attempt.InputTokens,
                    CachedTokens = attempt.CachedTokens,
                    OutputTokens = attempt.OutputTokens,
                    TotalDurationMs = attempt.TotalDurationMs,
                    FirstTokenLatencyMs = attempt.FirstTokenLatencyMs,
                    StreamDurationMs = attempt.StreamDurationMs,
                    StartedAt = attempt.CreatedAt,
                    FinishedAt = attempt.UpdatedAt
                })
                .ToList()
        };

        var envelope = CoreAdminEventEnvelopeBuilder.CreateUnifiedProxyEnvelope(_sequenceProvider.Next(), payload);
        await _eventBus.PublishAsync(envelope, cancellationToken);
    }
}
