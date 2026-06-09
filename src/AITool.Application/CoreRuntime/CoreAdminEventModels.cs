namespace AITool.Application.CoreRuntime;

/// <summary>
/// Core 发往 Admin 的事件信封。
/// 当前阶段先统一基本元数据，后续再逐步扩展为可回放、可补传的完整事件协议。
/// </summary>
public sealed class CoreAdminEventEnvelope
{
    /// <summary>
    /// 全局递增事件序号。
    /// </summary>
    public long SequenceId { get; set; }

    /// <summary>
    /// 事件类型。
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// 事件发生时间。
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// JSON 负载。
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;
}

/// <summary>
/// UsageLog 对应的 Core 事件负载。
/// 为了降低第一阶段接入成本，字段尽量与现有 UsageLogEntry 保持一致。
/// </summary>
public sealed class CoreUsageLogEvent
{
    /// <summary>
    /// 请求链路标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 访问密钥标识。
    /// </summary>
    public Guid AccessKeyId { get; set; }

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 转发模式。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>
    /// 请求模型名。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 实际尝试模型名。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 目标站点标识。
    /// </summary>
    public Guid TargetSiteId { get; set; }

    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 来源。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 重试次数。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 尝试序号。
    /// </summary>
    public int AttemptIndex { get; set; }

    /// <summary>
    /// 是否最终结果。
    /// </summary>
    public bool IsFinalResult { get; set; }

    /// <summary>
    /// 是否触发回退。
    /// </summary>
    public bool FallbackTriggered { get; set; }

    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 输入 Token。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 缓存 Token。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 输出 Token。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 是否流式。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 是否流式中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>
    /// 首 Token 延迟。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>
    /// 流式耗时。
    /// </summary>
    public int StreamDurationMs { get; set; }

    /// <summary>
    /// 总耗时。
    /// </summary>
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// 思考强度。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 请求开始时间。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
