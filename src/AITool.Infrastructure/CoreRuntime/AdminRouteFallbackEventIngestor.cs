using System.Text.Json;
using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧路由回退事件消费器。
/// <para>
/// 从 <see cref="CoreEventPullService"/> 分发过来的事件批次中筛选 <c>route-fallback</c> 类型，
/// 反序列化为 <see cref="CoreRouteFallbackEvent"/>，写入
/// <see cref="AdminRouteFallbackStore"/> 内存存储。
/// </para>
/// <para>
/// 回退事件属于运行时诊断数据，不需要持久化，6 小时过期自动清理。
/// Admin 侧可通过内存存储查询最近的回退记录，用于路由健康监控和分析。
/// </para>
/// </summary>
public sealed class AdminRouteFallbackEventIngestor
{
    private readonly AdminRouteFallbackStore _store;
    private readonly ILogger<AdminRouteFallbackEventIngestor> _logger;

    /// <summary>
    /// JSON 序列化选项，使用 Web 默认配置（camelCase、宽松枚举）。
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 初始化路由回退事件消费器。
    /// </summary>
    public AdminRouteFallbackEventIngestor(
        AdminRouteFallbackStore store,
        ILogger<AdminRouteFallbackEventIngestor> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 消费一批 Core 事件，筛选 route-fallback 类型并写入内存存储。
    /// 返回本批次中 route-fallback 事件的最大序号；如果没有匹配事件则返回 0。
    /// </summary>
    public Task<long> IngestRouteFallbackEventsAsync(
        IReadOnlyList<CoreAdminEventEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
        {
            return Task.FromResult(0L);
        }

        // 筛选 route-fallback 事件并尝试反序列化
        var parsed = envelopes
            .Where(x => string.Equals(x.EventType, "route-fallback", StringComparison.Ordinal))
            .Select(x => (Envelope: x, Payload: DeserializeFallback(x.PayloadJson)))
            .Where(x => x.Payload is not null)
            .ToList();

        if (parsed.Count == 0)
        {
            return Task.FromResult(0L);
        }

        // 批量写入内存存储
        _store.AddRange(parsed.Select(x => x.Payload!));

        _logger.LogDebug(
            "已消费 {IngestedCount} 条路由回退事件",
            parsed.Count);

        return Task.FromResult(parsed.Max(x => x.Envelope.SequenceId));
    }

    /// <summary>
    /// 反序列化路由回退事件负载；失败时返回 null，由上层跳过。
    /// </summary>
    private static CoreRouteFallbackEvent? DeserializeFallback(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CoreRouteFallbackEvent>(payloadJson, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
