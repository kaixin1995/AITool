using System.Text.Json;
using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧熔断状态变更事件消费器。
/// <para>
/// 从 <see cref="Admin.Services.CoreEventPullService"/> 分发过来的事件批次中筛选 <c>circuit-breaker</c> 类型，
/// 反序列化为 <see cref="CoreCircuitBreakerEvent"/>，写入
/// <see cref="AdminCircuitBreakerStore"/> 内存存储。
/// </para>
/// <para>
/// 熔断事件属于实时运维监控数据，不需要持久化，6 小时过期自动清理。
/// Admin 侧可通过内存存储查询近期的熔断触发历史，了解哪些路由因连续失败被临时屏蔽。
/// </para>
/// </summary>
public sealed class AdminCircuitBreakerEventIngestor
{
    private readonly AdminCircuitBreakerStore _store;
    private readonly ILogger<AdminCircuitBreakerEventIngestor> _logger;

    /// <summary>
    /// JSON 序列化选项，使用 Web 默认配置（camelCase、宽松枚举）。
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 初始化熔断状态变更事件消费器。
    /// </summary>
    public AdminCircuitBreakerEventIngestor(
        AdminCircuitBreakerStore store,
        ILogger<AdminCircuitBreakerEventIngestor> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 消费一批 Core 事件，筛选 circuit-breaker 类型并写入内存存储。
    /// 返回本批次中 circuit-breaker 事件的最大序号；如果没有匹配事件则返回 0。
    /// </summary>
    public Task<long> IngestCircuitBreakerEventsAsync(
        IReadOnlyList<CoreAdminEventEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
        {
            return Task.FromResult(0L);
        }

        // 筛选 circuit-breaker 事件并尝试反序列化
        var parsed = envelopes
            .Where(x => string.Equals(x.EventType, "circuit-breaker", StringComparison.Ordinal))
            .Select(x => (Envelope: x, Payload: DeserializeCircuitBreaker(x.PayloadJson)))
            .Where(x => x.Payload is not null)
            .ToList();

        if (parsed.Count == 0)
        {
            return Task.FromResult(0L);
        }

        // 批量写入内存存储
        foreach (var item in parsed)
        {
            _store.Add(item.Payload!);
        }

        _logger.LogDebug(
            "已消费 {IngestedCount} 条熔断状态变更事件",
            parsed.Count);

        return Task.FromResult(parsed.Max(x => x.Envelope.SequenceId));
    }

    /// <summary>
    /// 反序列化熔断状态变更事件负载；失败时返回 null，由上层跳过。
    /// </summary>
    private static CoreCircuitBreakerEvent? DeserializeCircuitBreaker(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CoreCircuitBreakerEvent>(payloadJson, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
