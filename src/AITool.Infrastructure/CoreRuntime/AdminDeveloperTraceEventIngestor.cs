using System.Text.Json;
using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧开发者追踪事件消费器。
/// <para>
/// 从 CoreEventPullService 分发过来的事件批次中筛选 <c>developer-trace</c> 类型，
/// 反序列化为 <see cref="CoreDeveloperTraceEvent"/>，去重后写入
/// <see cref="AdminDeveloperTraceStore"/> 内存存储。
/// </para>
/// <para>
/// 与 <see cref="AdminUsageLogEventIngestor"/>（写数据库）和
/// <see cref="AITool.Infrastructure.Conversations.AdminConversationTurnEventIngestor"/>（写 JSONL）
/// 不同，开发者追踪数据是临时运行时数据，不需要持久化，6 小时过期自动清理。
/// </para>
/// </summary>
public sealed class AdminDeveloperTraceEventIngestor
{
    private readonly AdminDeveloperTraceStore _store;
    private readonly ILogger<AdminDeveloperTraceEventIngestor> _logger;

    /// <summary>
    /// JSON 序列化选项，使用 Web 默认配置（camelCase、宽松枚举）。
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 初始化开发者追踪事件消费器。
    /// </summary>
    public AdminDeveloperTraceEventIngestor(
        AdminDeveloperTraceStore store,
        ILogger<AdminDeveloperTraceEventIngestor> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 消费一批 Core 事件，筛选 developer-trace 类型并写入内存存储。
    /// 返回本批次中 developer-trace 事件的最大序号；如果没有匹配事件则返回 0。
    /// </summary>
    public Task<long> IngestDeveloperTraceEventsAsync(
        IReadOnlyList<CoreAdminEventEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
        {
            return Task.FromResult(0L);
        }

        // 筛选 developer-trace 事件并尝试反序列化
        var parsed = envelopes
            .Where(x => string.Equals(x.EventType, "developer-trace", StringComparison.Ordinal))
            .Select(x => (Envelope: x, Payload: DeserializeTrace(x.PayloadJson)))
            .Where(x => x.Payload is not null)
            .ToList();

        if (parsed.Count == 0)
        {
            return Task.FromResult(0L);
        }

        // 按 TraceId 去重，保留最大序号的事件（事件可能因重放而产生重复）
        var deduplicated = parsed
            .GroupBy(x => x.Payload!.TraceId)
            .Select(g => g.OrderByDescending(x => x.Envelope.SequenceId).First())
            .ToList();

        // 批量写入内存存储
        _store.UpsertRange(deduplicated.Select(x => x.Payload!));

        _logger.LogDebug(
            "已消费 {IngestedCount} 条开发者追踪事件（去重前 {RawCount} 条）",
            deduplicated.Count,
            parsed.Count);

        return Task.FromResult(parsed.Max(x => x.Envelope.SequenceId));
    }

    /// <summary>
    /// 反序列化开发者追踪事件负载；失败时返回 null，由上层跳过。
    /// </summary>
    private static CoreDeveloperTraceEvent? DeserializeTrace(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CoreDeveloperTraceEvent>(payloadJson, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
