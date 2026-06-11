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

/// <summary>
/// 对话记录对应的 Core 事件负载。
/// 为了降低第一阶段接入成本，字段尽量与现有 ConversationTurnEntry 保持一致。
/// </summary>
public sealed class CoreConversationTurnEvent
{
    /// <summary>
    /// 请求链路标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 助手侧时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 用户发起时间。
    /// </summary>
    public DateTimeOffset? UserCreatedAt { get; set; }

    /// <summary>
    /// 工具来源。
    /// </summary>
    public string SourceTool { get; set; } = string.Empty;

    /// <summary>
    /// 会话标识。
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 会话分组键。
    /// </summary>
    public string ConversationGroupKey { get; set; } = string.Empty;

    /// <summary>
    /// 访问密钥标识。
    /// </summary>
    public Guid AccessKeyId { get; set; }

    /// <summary>
    /// 请求模型名。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// 来源入口。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 用户输入文本。
    /// </summary>
    public string UserInputText { get; set; } = string.Empty;

    /// <summary>
    /// AI 输出 Markdown。
    /// </summary>
    public string AssistantOutputMarkdown { get; set; } = string.Empty;

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
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 附加元数据 JSON。
    /// </summary>
    public string MetadataJson { get; set; } = string.Empty;

    /// <summary>
    /// 自定义会话标题。
    /// </summary>
    public string ConversationTitle { get; set; } = string.Empty;
}

/// <summary>
/// 开发者调用追踪对应的 Core 事件负载。
/// 当代理请求完成（成功或失败）时，Core 将调用摘要发布为事件，
/// Admin 侧消费后在开发者调试页面展示近期的调用追踪。
/// <para>
/// 与 DeveloperInvocationTraceEntry 的区别：
/// TraceEntry 是代理运行时的完整内部记录（含请求体、响应体、请求头等大量字段），
/// 而 CoreDeveloperTraceEvent 只携带摘要信息，适合跨宿主传输和长期存储。
/// </para>
/// </summary>
public sealed class CoreDeveloperTraceEvent
{
    /// <summary>
    /// 跟踪标识。
    /// </summary>
    public Guid TraceId { get; set; }

    /// <summary>
    /// 请求标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 协议类型（如 OpenAI、Anthropic）。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 请求模型名（路由前的原始模型名）。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 实际尝试调用的模型名（路由后的上游模型名）。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 目标站点标识。
    /// </summary>
    public Guid? TargetSiteId { get; set; }

    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string TargetSiteName { get; set; } = string.Empty;

    /// <summary>
    /// 转发模式（如 direct、upstream-protocol）。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>
    /// 调用状态（success / error / pending）。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 调用开始时间（即请求创建时间）。
    /// </summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 调用结束时间（即结果完成时间）。
    /// </summary>
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 错误信息。成功时为空字符串。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 请求体预览（截断后的前 N 个字符）。
    /// </summary>
    public string RequestPreview { get; set; } = string.Empty;

    /// <summary>
    /// 响应体预览（截断后的前 N 个字符）。
    /// </summary>
    public string ResponsePreview { get; set; } = string.Empty;

    /// <summary>
    /// 来源标识。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 是否为流式调用。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
}

/// <summary>
/// 路由回退事件对应的 Core 事件负载。
/// 当代理请求在某条路由上失败后回退到下一条路由时，Core 发布此事件，
/// Admin 侧消费后用于实时监控路由健康状态和分析回退模式。
/// <para>
/// 每次回退切换产生一条事件，记录从哪条路由（源）切换到哪条路由（目标），
/// 以及触发回退的具体原因（如上游超时、HTTP 错误等）。
/// </para>
/// </summary>
public sealed class CoreRouteFallbackEvent
{
    /// <summary>
    /// 关联的代理请求标识，可用于关联同一请求的 UsageLog 和 ConversationTurn 事件。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 请求模型名（路由前的原始模型名）。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 回退源路由标识。
    /// </summary>
    public Guid FromRouteId { get; set; }

    /// <summary>
    /// 回退源站点标识。
    /// </summary>
    public Guid FromSiteId { get; set; }

    /// <summary>
    /// 回退源站点上的模型名。
    /// </summary>
    public string FromSiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 回退目标路由标识。
    /// </summary>
    public Guid ToRouteId { get; set; }

    /// <summary>
    /// 回退目标站点标识。
    /// </summary>
    public Guid ToSiteId { get; set; }

    /// <summary>
    /// 回退目标站点上的模型名。
    /// </summary>
    public string ToSiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 触发回退的原因（如 upstream timeout、HTTP 500、connection refused 等）。
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// 回退发生时间。
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
