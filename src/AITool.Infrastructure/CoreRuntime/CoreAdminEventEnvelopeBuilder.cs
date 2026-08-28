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

    /// <summary>
    /// 构造一条开发者追踪事件信封。
    /// </summary>
    public static CoreAdminEventEnvelope CreateDeveloperTraceEnvelope(long sequenceId, CoreDeveloperTraceEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "developer-trace",
            OccurredAt = payload.FinishedAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }

    /// <summary>
    /// 构造一条路由回退事件信封。
    /// </summary>
    public static CoreAdminEventEnvelope CreateRouteFallbackEnvelope(long sequenceId, CoreRouteFallbackEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "route-fallback",
            OccurredAt = payload.OccurredAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }

    /// <summary>
    /// 构造一条配置变更应用事件信封。
    /// </summary>
    public static CoreAdminEventEnvelope CreateConfigAppliedEnvelope(long sequenceId, CoreConfigAppliedEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "config-applied",
            OccurredAt = payload.OccurredAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }

    /// <summary>
    /// 构造一条熔断状态变更事件信封。
    /// </summary>
    public static CoreAdminEventEnvelope CreateCircuitBreakerEnvelope(long sequenceId, CoreCircuitBreakerEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "circuit-breaker",
            OccurredAt = payload.OccurredAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }

    /// <summary>
    /// 构造一条统一代理请求事件信封。
    /// </summary>
    public static CoreAdminEventEnvelope CreateUnifiedProxyEnvelope(long sequenceId, CoreUnifiedProxyEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "proxy-request",
            OccurredAt = payload.FinishedAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }

    /// <summary>
    /// 构造一条托管凭证刷新事件信封（Core 401 即刷 → Admin 持久化）。
    /// </summary>
    public static CoreAdminEventEnvelope CreateCredentialRefreshedEnvelope(long sequenceId, CoreCredentialRefreshedEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "credential-refreshed",
            OccurredAt = payload.RefreshedAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }

    /// <summary>
    /// 构造一条托管凭证禁用事件信封（Core 403 → Admin 禁用账号与站点）。
    /// </summary>
    public static CoreAdminEventEnvelope CreateCredentialDisabledEnvelope(long sequenceId, CoreCredentialDisabledEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CoreAdminEventEnvelope
        {
            SequenceId = sequenceId,
            EventType = "credential-disabled",
            OccurredAt = payload.DisabledAt,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions)
        };
    }
}
