using AITool.Application.CoreRuntime;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧最小 UsageLog 事件消费器。
/// 当前阶段先只接 usage-log 事件，并把它写回 Admin 当前数据库中的 ProxyUsageLogs 表。
/// 这样可以先验证一条真实事件链路是否已经具备“消费入库”的完整能力。
/// </summary>
public sealed class AdminUsageLogEventIngestor
{
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化 UsageLog 事件消费器。
    /// </summary>
    public AdminUsageLogEventIngestor(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 消费一批 Core 事件并把 UsageLog 事件写回 Admin 数据库。
    /// 返回值表示本次成功写入的最大连续序号，供上层提交 ack 使用。
    /// 如果当前批次中没有 usage-log 事件，则返回 0。
    /// </summary>
    public async Task<long> IngestUsageLogEventsAsync(
        IReadOnlyList<CoreAdminEventEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
        {
            return 0;
        }

        var parsedUsageLogs = envelopes
            .Where(x => string.Equals(x.EventType, "usage-log", StringComparison.Ordinal))
            .Select(x => (Envelope: x, Payload: DeserializeUsageLog(x.PayloadJson)))
            .Where(x => x.Payload is not null)
            .ToList();
        if (parsedUsageLogs.Count == 0)
        {
            return 0;
        }

        var usageLogs = parsedUsageLogs
            .GroupBy(x => new
            {
                x.Payload!.RequestId,
                x.Payload.AttemptIndex,
                x.Payload.RequestedAt,
                x.Payload.AttemptedModel,
                x.Payload.Status
            })
            .Select(x => x.OrderBy(item => item.Envelope.SequenceId).First())
            .ToList();

        // 这里按 RequestId + AttemptIndex + RequestedAt 做最小幂等判断，避免 replay 或重复提交时写出完全相同的使用日志。
        var requestIds = usageLogs.Select(x => x.Payload!.RequestId).Distinct().ToList();
        var existingLogs = await _dbContext.ProxyUsageLogs
            .Where(x => requestIds.Contains(x.RequestId))
            .ToListAsync(cancellationToken);

        var newLogs = usageLogs
            .Where(x => !existingLogs.Any(existing =>
                existing.RequestId == x.Payload!.RequestId
                && existing.AttemptIndex == x.Payload.AttemptIndex
                && existing.RequestedAt == x.Payload.RequestedAt
                && string.Equals(existing.AttemptedModel, x.Payload.AttemptedModel, StringComparison.Ordinal)
                && string.Equals(existing.Status, x.Payload.Status, StringComparison.Ordinal)))
            .Select(x => new ProxyUsageLog
            {
                RequestId = x.Payload!.RequestId,
                AccessKeyId = x.Payload.AccessKeyId,
                ProtocolType = x.Payload.ProtocolType,
                ForwardingMode = x.Payload.ForwardingMode,
                RequestModel = x.Payload.RequestModel,
                AttemptedModel = x.Payload.AttemptedModel,
                TargetSiteId = x.Payload.TargetSiteId,
                Status = x.Payload.Status,
                Source = x.Payload.Source,
                RetryCount = x.Payload.RetryCount,
                AttemptIndex = x.Payload.AttemptIndex,
                IsFinalResult = x.Payload.IsFinalResult,
                FallbackTriggered = x.Payload.FallbackTriggered,
                ErrorMessage = x.Payload.ErrorMessage,
                InputTokens = x.Payload.InputTokens,
                CachedTokens = x.Payload.CachedTokens,
                OutputTokens = x.Payload.OutputTokens,
                TotalTokens = x.Payload.InputTokens + x.Payload.CachedTokens + x.Payload.OutputTokens,
                IsStreaming = x.Payload.IsStreaming,
                IsStreamInterrupted = x.Payload.IsStreamInterrupted,
                FirstTokenLatencyMs = x.Payload.FirstTokenLatencyMs,
                StreamDurationMs = x.Payload.StreamDurationMs,
                TotalDurationMs = x.Payload.TotalDurationMs,
                ReasoningEffort = x.Payload.ReasoningEffort,
                RequestedAt = x.Payload.RequestedAt
            })
            .ToList();

        if (newLogs.Count > 0)
        {
            _dbContext.ProxyUsageLogs.AddRange(newLogs);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return parsedUsageLogs.Max(x => x.Envelope.SequenceId);
    }

    /// <summary>
    /// 解析 UsageLog 事件负载；解析失败时返回 null，由上层直接跳过该事件。
    /// </summary>
    private static CoreUsageLogEvent? DeserializeUsageLog(string payloadJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CoreUsageLogEvent>(payloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }
}
