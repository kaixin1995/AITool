using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧统一代理事件消费器，替代 <see cref="AdminUsageLogEventIngestor"/> 和
/// <see cref="AdminDeveloperTraceEventIngestor"/>。
/// <para>
/// 从 Core 事件流中筛选 <c>proxy-request</c> 类型事件，反序列化为
/// <see cref="CoreUnifiedProxyEvent"/>，按 TraceId 去重后同时写入两个 Sink：
/// <list type="bullet">
///   <item>DB Sink：按尝试明细展开为 <see cref="ProxyUsageLog"/> 行写入数据库；</item>
///   <item>Memory Sink：将完整事件存入 <see cref="AdminDeveloperTraceStore"/> 供开发者调试页面查询。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AdminUnifiedProxyEventIngestor
{
    private readonly AppDbContext _dbContext;
    private readonly AdminDeveloperTraceStore _traceStore;
    private readonly ILogger<AdminUnifiedProxyEventIngestor> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 初始化统一代理事件消费器。
    /// </summary>
    public AdminUnifiedProxyEventIngestor(
        AppDbContext dbContext,
        AdminDeveloperTraceStore traceStore,
        ILogger<AdminUnifiedProxyEventIngestor> logger)
    {
        _dbContext = dbContext;
        _traceStore = traceStore;
        _logger = logger;
    }

    /// <summary>
    /// 消费一批 Core 事件，筛选 proxy-request 类型并写入三个 Sink。
    /// 返回本批次中 proxy-request 事件的最大序号；如果没有匹配事件则返回 0。
    /// </summary>
    public async Task<long> IngestUnifiedProxyEventsAsync(
        IReadOnlyList<CoreAdminEventEnvelope> envelopes,
        CancellationToken ct = default)
    {
        if (envelopes.Count == 0)
        {
            return 0;
        }

        // 筛选 proxy-request 事件并反序列化为 CoreUnifiedProxyEvent
        var parsed = envelopes
            .Where(x => string.Equals(x.EventType, "proxy-request", StringComparison.Ordinal))
            .Select(x => (Envelope: x, Payload: DeserializeProxyEvent(x.PayloadJson)))
            .Where(x => x.Payload is not null)
            .ToList();

        if (parsed.Count == 0)
        {
            return 0;
        }

        // 按 TraceId 去重，保留最大 SequenceId（事件可能因重放而产生重复）
        var deduplicated = parsed
            .GroupBy(x => x.Payload!.TraceId)
            .Select(g => g.OrderByDescending(x => x.Envelope.SequenceId).First())
            .ToList();

        // ──────────── DB SINK：按尝试明细展开为 ProxyUsageLog 行 ────────────
        await WriteDbSinkAsync(deduplicated, ct);

        // ──────────── MEMORY SINK：写入 AdminDeveloperTraceStore ────────────
        WriteMemorySink(deduplicated);

        _logger.LogDebug(
            "已消费 {IngestedCount} 条统一代理事件（去重前 {RawCount} 条）",
            deduplicated.Count,
            parsed.Count);

        return parsed.Max(x => x.Envelope.SequenceId);
    }

    /// <summary>
    /// DB Sink：遍历每个去重后事件的 Attempts 列表，为每次尝试创建一条 ProxyUsageLog 行，
    /// 并在写入前按 (RequestId, AttemptIndex, RequestedAt, AttemptedModel, Status) 做幂等判断。
    /// </summary>
    private async Task WriteDbSinkAsync(
        List<(CoreAdminEventEnvelope Envelope, CoreUnifiedProxyEvent Payload)> deduplicated,
        CancellationToken ct)
    {
        // 收集所有去重事件的 RequestId，一次性查询已有行用于幂等判断
        var requestIds = deduplicated.Select(x => x.Payload.RequestId).Distinct().ToList();
        var existingLogs = await _dbContext.ProxyUsageLogs
            .Where(x => requestIds.Contains(x.RequestId))
            .ToListAsync(ct);

        var newLogs = new List<ProxyUsageLog>();

        foreach (var (_, evt) in deduplicated)
        {
            var attempts = evt.Attempts
                .OrderBy(a => a.AttemptIndex)
                .ToList();

            if (attempts.Count == 0)
            {
                continue;
            }

            // 找到最后一个成功尝试的索引；若全部失败，最后一个尝试视为最终结果
            int? lastSuccessIndex = null;
            for (var i = attempts.Count - 1; i >= 0; i--)
            {
                if (string.Equals(attempts[i].Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    lastSuccessIndex = i;
                    break;
                }
            }

            var hasFailedAttempt = false;

            foreach (var attempt in attempts)
            {
                // IsFinalResult：最后一个成功的尝试，或全部失败时的最后一个尝试
                bool isFinalResult;
                if (lastSuccessIndex.HasValue)
                {
                    isFinalResult = attempt.AttemptIndex == lastSuccessIndex.Value;
                }
                else
                {
                    isFinalResult = attempt.AttemptIndex == attempts[^1].AttemptIndex;
                }

                // FallbackTriggered：是否有更早的尝试失败
                var fallbackTriggered = hasFailedAttempt;

                // DB 幂等判断
                var alreadyExists = existingLogs.Any(existing =>
                    existing.RequestId == evt.RequestId
                    && existing.AttemptIndex == attempt.AttemptIndex
                    && existing.RequestedAt == attempt.StartedAt
                    && string.Equals(existing.AttemptedModel, attempt.AttemptedModel, StringComparison.Ordinal)
                    && string.Equals(existing.Status, attempt.Status, StringComparison.Ordinal));

                if (!alreadyExists)
                {
                    var inputTokens = attempt.InputTokens;
                    var cachedTokens = attempt.CachedTokens;
                    var outputTokens = attempt.OutputTokens;

                    newLogs.Add(new ProxyUsageLog
                    {
                        RequestId = evt.RequestId,
                        AccessKeyId = evt.AccessKeyId,
                        ProtocolType = evt.ProtocolType,
                        ForwardingMode = attempt.ForwardingMode,
                        RequestModel = evt.RequestModel,
                        AttemptedModel = attempt.AttemptedModel,
                        TargetSiteId = attempt.TargetSiteId,
                        Status = attempt.Status,
                        Source = evt.Source,
                        RetryCount = attempt.AttemptIndex,
                        AttemptIndex = attempt.AttemptIndex,
                        IsFinalResult = isFinalResult,
                        FallbackTriggered = fallbackTriggered,
                        ErrorMessage = attempt.ErrorMessage,
                        InputTokens = inputTokens,
                        CachedTokens = cachedTokens,
                        OutputTokens = outputTokens,
                        TotalTokens = inputTokens + cachedTokens + outputTokens,
                        IsStreaming = attempt.IsStreaming,
                        IsStreamInterrupted = attempt.IsStreamInterrupted,
                        FirstTokenLatencyMs = attempt.FirstTokenLatencyMs,
                        StreamDurationMs = attempt.StreamDurationMs,
                        TotalDurationMs = attempt.TotalDurationMs,
                        ReasoningEffort = evt.ReasoningEffort,
                        RequestedAt = attempt.StartedAt
                    });
                }

                // 记录本次尝试是否失败，供后续尝试的 FallbackTriggered 判断
                if (!string.Equals(attempt.Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    hasFailedAttempt = true;
                }
            }
        }

        if (newLogs.Count > 0)
        {
            await _dbContext.InsertRangeAsync(newLogs, ct);
        }
    }

    /// <summary>
    /// Memory Sink：将 CoreUnifiedProxyEvent 直接写入内存存储。
    /// </summary>
    private void WriteMemorySink(
        List<(CoreAdminEventEnvelope Envelope, CoreUnifiedProxyEvent Payload)> deduplicated)
    {
        foreach (var (_, evt) in deduplicated)
        {
            _traceStore.Upsert(evt);
        }
    }

    private static CoreUnifiedProxyEvent? DeserializeProxyEvent(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CoreUnifiedProxyEvent>(payloadJson, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
