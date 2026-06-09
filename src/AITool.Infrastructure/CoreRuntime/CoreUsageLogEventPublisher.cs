using AITool.Application.CoreRuntime;
using AITool.Application.UsageLogs;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// UsageLog → Core 事件发布器。
/// 当前阶段先把 UsageLog 这条真实链路接到事件总线里，验证事件骨架与序号分配是否可用。
/// </summary>
public sealed class CoreUsageLogEventPublisher
{
    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly CoreAdminEventBus _eventBus;

    /// <summary>
    /// 初始化 UsageLog 事件发布器。
    /// </summary>
    public CoreUsageLogEventPublisher(
        CoreEventSequenceProvider sequenceProvider,
        CoreAdminEventBus eventBus)
    {
        _sequenceProvider = sequenceProvider;
        _eventBus = eventBus;
    }

    /// <summary>
    /// 把一条 UsageLogEntry 投影成 Core 事件并发布到总线。
    /// </summary>
    public async Task PublishAsync(UsageLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var payload = new CoreUsageLogEvent
        {
            RequestId = entry.RequestId,
            AccessKeyId = entry.AccessKeyId,
            ProtocolType = entry.ProtocolType,
            ForwardingMode = entry.ForwardingMode,
            RequestModel = entry.RequestModel,
            AttemptedModel = entry.AttemptedModel,
            TargetSiteId = entry.TargetSiteId,
            Status = entry.Status,
            Source = entry.Source,
            RetryCount = entry.RetryCount,
            AttemptIndex = entry.AttemptIndex,
            IsFinalResult = entry.IsFinalResult,
            FallbackTriggered = entry.FallbackTriggered,
            ErrorMessage = entry.ErrorMessage,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            IsStreaming = entry.IsStreaming,
            IsStreamInterrupted = entry.IsStreamInterrupted,
            FirstTokenLatencyMs = entry.FirstTokenLatencyMs,
            StreamDurationMs = entry.StreamDurationMs,
            TotalDurationMs = entry.TotalDurationMs,
            ReasoningEffort = entry.ReasoningEffort,
            RequestedAt = entry.RequestedAt
        };

        var envelope = CoreAdminEventEnvelopeBuilder.CreateUsageLogEnvelope(_sequenceProvider.Next(), payload);
        await _eventBus.PublishAsync(envelope, cancellationToken);
    }
}
