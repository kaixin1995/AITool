using System.Text.Json;
using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧配置变更应用事件消费器。
/// <para>
/// 从 <see cref="Admin.Services.CoreEventPullService"/> 分发过来的事件批次中筛选 <c>config-applied</c> 类型，
/// 反序列化为 <see cref="CoreConfigAppliedEvent"/>，写入
/// <see cref="AdminConfigAppliedStore"/> 内存存储。
/// </para>
/// <para>
/// 配置变更事件属于运维审计数据，不需要持久化，24 小时过期自动清理。
/// Admin 侧可通过内存存储查询近期的配置变更历史，了解配置何时被 Core 接受并生效。
/// </para>
/// </summary>
public sealed class AdminConfigAppliedEventIngestor
{
    private readonly AdminConfigAppliedStore _store;
    private readonly ILogger<AdminConfigAppliedEventIngestor> _logger;

    /// <summary>
    /// JSON 序列化选项，使用 Web 默认配置（camelCase、宽松枚举）。
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 初始化配置变更应用事件消费器。
    /// </summary>
    public AdminConfigAppliedEventIngestor(
        AdminConfigAppliedStore store,
        ILogger<AdminConfigAppliedEventIngestor> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 消费一批 Core 事件，筛选 config-applied 类型并写入内存存储。
    /// 返回本批次中 config-applied 事件的最大序号；如果没有匹配事件则返回 0。
    /// </summary>
    public Task<long> IngestConfigAppliedEventsAsync(
        IReadOnlyList<CoreAdminEventEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
        {
            return Task.FromResult(0L);
        }

        // 筛选 config-applied 事件并尝试反序列化
        var parsed = envelopes
            .Where(x => string.Equals(x.EventType, "config-applied", StringComparison.Ordinal))
            .Select(x => (Envelope: x, Payload: DeserializeConfigApplied(x.PayloadJson)))
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
            "已消费 {IngestedCount} 条配置变更应用事件",
            parsed.Count);

        return Task.FromResult(parsed.Max(x => x.Envelope.SequenceId));
    }

    /// <summary>
    /// 反序列化配置变更应用事件负载；失败时返回 null，由上层跳过。
    /// </summary>
    private static CoreConfigAppliedEvent? DeserializeConfigApplied(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CoreConfigAppliedEvent>(payloadJson, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
