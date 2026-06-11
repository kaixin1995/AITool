using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;

namespace AITool.Core.Services;

/// <summary>
/// 开发者调用追踪 → Core 事件发布器。
/// 在代理请求完成后，将调用摘要发布为 developer-trace 事件，
/// Admin 侧消费后在开发者调试页面展示近期的调用追踪。
/// <para>
/// 与 UsageLog/ConversationTurn 不同，此发布器不从独立的业务 Entry 转换，
/// 而是直接从 <see cref="DeveloperInvocationTraceEntry"/> 提取摘要字段。
/// 请求体和响应体通过预览方式截断传输，避免大负载事件占用过多带宽。
/// </para>
/// </summary>
public sealed class CoreDeveloperTraceEventPublisher
{
    /// <summary>
    /// 请求体/响应体预览的最大长度。
    /// 超过此长度的内容将被截断，防止事件负载过大。
    /// </summary>
    private const int PreviewMaxLength = 512;

    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly CoreAdminEventBus _eventBus;

    /// <summary>
    /// 初始化开发者追踪事件发布器。
    /// </summary>
    public CoreDeveloperTraceEventPublisher(
        CoreEventSequenceProvider sequenceProvider,
        CoreAdminEventBus eventBus)
    {
        _sequenceProvider = sequenceProvider;
        _eventBus = eventBus;
    }

    /// <summary>
    /// 把一条调用追踪记录投影成 Core 事件并发布到总线。
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

        var payload = new CoreDeveloperTraceEvent
        {
            TraceId = entry.TraceId,
            RequestId = entry.RequestId,
            ProtocolType = entry.ProtocolType,
            RequestModel = entry.RequestModel,
            AttemptedModel = entry.AttemptedModel,
            TargetSiteId = entry.TargetSiteId,
            TargetSiteName = entry.TargetSiteName,
            ForwardingMode = entry.Attempts.FirstOrDefault()?.ForwardingMode ?? string.Empty,
            Status = entry.Status,
            StartedAt = entry.CreatedAt,
            FinishedAt = entry.UpdatedAt,
            ErrorMessage = entry.ErrorMessage ?? string.Empty,
            RequestPreview = TruncatePreview(entry.RequestBody),
            ResponsePreview = TruncatePreview(entry.ResponseBody),
            Source = entry.Source,
            IsStreaming = entry.IsStreaming,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            TotalDurationMs = entry.TotalDurationMs
        };

        var envelope = CoreAdminEventEnvelopeBuilder.CreateDeveloperTraceEnvelope(_sequenceProvider.Next(), payload);
        await _eventBus.PublishAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// 截断预览文本，超过最大长度时添加省略号后缀。
    /// </summary>
    private static string TruncatePreview(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= PreviewMaxLength
            ? text
            : string.Concat(text.AsSpan(0, PreviewMaxLength), "...");
    }
}
