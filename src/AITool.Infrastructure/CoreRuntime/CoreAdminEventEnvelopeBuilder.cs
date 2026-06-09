using System.Text.Json;
using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件信封构造器。
/// 当前阶段先支持 UsageLog 事件，后续再扩展 Conversations、Developer traces 等其他事件。
/// </summary>
public static class CoreAdminEventEnvelopeBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// 构造一条 UsageLog 事件信封。
    /// </summary>
    public static CoreAdminEventEnvelope CreateUsageLogEnvelope(long sequenceId, CoreUsageLogEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "usage-log",
            OccurredAt = payload.RequestedAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }
}
