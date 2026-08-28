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

/// <summary>
/// 配置变更应用事件对应的 Core 事件负载。
/// <para>
/// 当 Admin 通过 full-sync 或 patch-sync 向 Core 下发配置并成功应用后，
/// Core 发布此事件作为确认通知。Admin 侧消费后可用于：
/// <list type="bullet">
///   <item>实时感知配置变更已生效</item>
///   <item>记录配置变更历史审计日志</item>
///   <item>触发 Admin 侧的缓存刷新或状态同步</item>
/// </list>
/// </para>
/// </summary>
public sealed class CoreConfigAppliedEvent
{
    /// <summary>
    /// 应用后的配置版本号。
    /// </summary>
    public long ConfigVersion { get; set; }

    /// <summary>
    /// 应用后的配置哈希值。
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// 同步模式：full（全量同步）或 patch（增量同步）。
    /// </summary>
    public string SyncMode { get; set; } = string.Empty;

    /// <summary>
    /// 增量同步时变更的实体类别列表（如 Sites、Models 等）。
    /// 全量同步时为空列表。
    /// </summary>
    public List<string> ChangedCategories { get; set; } = [];

    /// <summary>
    /// 配置应用前（旧）的版本号。
    /// 首次同步时为 0。
    /// </summary>
    public long PreviousConfigVersion { get; set; }

    /// <summary>
    /// 配置应用前（旧）的哈希值。
    /// 首次同步时为空字符串。
    /// </summary>
    public string PreviousConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// 事件发生时间（即配置被 Core 应用的时间）。
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 熔断状态变更事件对应的 Core 事件负载。
/// <para>
/// 当某条路由因连续失败达到阈值触发熔断时，Core 发布此事件，
/// Admin 侧消费后用于实时监控路由健康状态和熔断模式分析。
/// </para>
/// <para>
/// 事件在熔断首次触发时产生（即连续失败计数首次达到阈值的瞬间），
/// 不包含熔断恢复事件——恢复由时间窗口自动过期，无需额外通知。
/// </para>
/// </summary>
public sealed class CoreCircuitBreakerEvent
{
    /// <summary>
    /// 被熔断的路由标识。
    /// </summary>
    public Guid RouteId { get; set; }

    /// <summary>
    /// 触发熔断时的连续失败次数。
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// 熔断阈值（触发熔断所需的最小连续失败次数）。
    /// </summary>
    public int FailThreshold { get; set; }

    /// <summary>
    /// 熔断持续时长（路由被屏蔽的时间窗口）。
    /// </summary>
    public TimeSpan BlockDuration { get; set; }

    /// <summary>
    /// 熔断预计解除时间（UTC）。
    /// </summary>
    public DateTimeOffset RecoveryTime { get; set; }

    /// <summary>
    /// 事件发生时间（即熔断被触发的瞬间）。
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 统一代理请求事件负载。
/// 合并了 UsageLog 和 DeveloperTrace 的全部字段，并包含完整的请求/响应体和所有尝试明细。
/// </summary>
public sealed class CoreUnifiedProxyEvent
{
    // ──────────── 来自 CoreUsageLogEvent ────────────

    /// <summary>请求链路标识。</summary>
    public Guid RequestId { get; set; }

    /// <summary>访问密钥标识。</summary>
    public Guid AccessKeyId { get; set; }

    /// <summary>协议类型。</summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>转发模式。</summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>请求模型名。</summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>实际尝试模型名。</summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>目标站点标识。</summary>
    public Guid? TargetSiteId { get; set; }

    /// <summary>状态。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>来源。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>重试次数。</summary>
    public int RetryCount { get; set; }

    /// <summary>尝试序号。</summary>
    public int AttemptIndex { get; set; }

    /// <summary>是否最终结果。</summary>
    public bool IsFinalResult { get; set; }

    /// <summary>是否触发回退。</summary>
    public bool FallbackTriggered { get; set; }

    /// <summary>错误信息。</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>输入 Token。</summary>
    public int InputTokens { get; set; }

    /// <summary>缓存 Token。</summary>
    public int CachedTokens { get; set; }

    /// <summary>输出 Token。</summary>
    public int OutputTokens { get; set; }

    /// <summary>是否流式。</summary>
    public bool IsStreaming { get; set; }

    /// <summary>是否流式中断。</summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>首 Token 延迟。</summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>流式耗时。</summary>
    public int StreamDurationMs { get; set; }

    /// <summary>总耗时。</summary>
    public int TotalDurationMs { get; set; }

    /// <summary>思考强度。</summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>请求开始时间。</summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    // ──────────── 来自 CoreDeveloperTraceEvent ────────────

    /// <summary>跟踪标识。</summary>
    public Guid TraceId { get; set; }

    /// <summary>目标站点名称。</summary>
    public string TargetSiteName { get; set; } = string.Empty;

    /// <summary>调用开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>调用结束时间。</summary>
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;

    // ──────────── 完整请求/响应数据（非截断版） ────────────

    /// <summary>完整请求体。</summary>
    public string RequestBody { get; set; } = string.Empty;

    /// <summary>完整响应体。</summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>请求头字典。</summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = [];

    /// <summary>客户端 IP。</summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>User-Agent。</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>请求路径。</summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; set; }

    /// <summary>响应 Content-Type。</summary>
    public string ResponseContentType { get; set; } = string.Empty;

    /// <summary>所有尝试明细列表。</summary>
    public List<CoreUnifiedAttemptDetail> Attempts { get; set; } = [];
}

/// <summary>
/// 统一代理请求中单次尝试的明细。
/// </summary>
public sealed class CoreUnifiedAttemptDetail
{
    /// <summary>尝试标识。</summary>
    public Guid AttemptId { get; set; }

    /// <summary>尝试序号（从 0 开始）。</summary>
    public int AttemptIndex { get; set; }

    /// <summary>本次尝试实际调用的模型名。</summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>上游协议类型。</summary>
    public string UpstreamProtocolType { get; set; } = string.Empty;

    /// <summary>转发模式。</summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>目标站点标识。</summary>
    public Guid TargetSiteId { get; set; }

    /// <summary>目标站点名称。</summary>
    public string TargetSiteName { get; set; } = string.Empty;

    /// <summary>状态（success / error 等）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; set; }

    /// <summary>错误信息。</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>本次尝试的完整响应体。</summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>响应 Content-Type。</summary>
    public string ResponseContentType { get; set; } = string.Empty;

    /// <summary>是否流式调用。</summary>
    public bool IsStreaming { get; set; }

    /// <summary>是否流式中断。</summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>输入 Token。</summary>
    public int InputTokens { get; set; }

    /// <summary>缓存 Token。</summary>
    public int CachedTokens { get; set; }

    /// <summary>输出 Token。</summary>
    public int OutputTokens { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public int TotalDurationMs { get; set; }

    /// <summary>首 Token 延迟（毫秒）。</summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>流式耗时（毫秒）。</summary>
    public int StreamDurationMs { get; set; }

    /// <summary>本次尝试开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>本次尝试结束时间。</summary>
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Core 侧托管凭证刷新事件（401 即刷链路）。
/// Core 无数据库：刷新成功后立即回写本地运行时快照并发布本事件，
/// Admin 侧摄取后持久化到对应账号表与隐藏站点 ApiKey，并触发配置同步。
/// </summary>
public sealed class CoreCredentialRefreshedEvent
{
    /// <summary>提供商标识：Codex | Google | kimi_oauth。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>账号标识。</summary>
    public Guid AccountId { get; set; }

    /// <summary>关联隐藏站点标识。</summary>
    public Guid LinkedSiteId { get; set; }

    /// <summary>刷新后的访问令牌（写回 Site.ApiKey）。</summary>
    public string NewAccessToken { get; set; } = string.Empty;

    /// <summary>刷新后的刷新令牌（Google 会轮换；空表示上游未轮换）。</summary>
    public string NewRefreshToken { get; set; } = string.Empty;

    /// <summary>刷新时间。</summary>
    public DateTimeOffset RefreshedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Core 侧托管凭证禁用事件（上游 403 等不可恢复错误）。
/// Admin 侧摄取后禁用对应账号与隐藏站点，并触发配置同步。
/// </summary>
public sealed class CoreCredentialDisabledEvent
{
    /// <summary>提供商标识：Codex | Google | kimi_oauth。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>账号标识。</summary>
    public Guid AccountId { get; set; }

    /// <summary>关联隐藏站点标识。</summary>
    public Guid LinkedSiteId { get; set; }

    /// <summary>禁用原因（如 proxy-403）。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>禁用时间。</summary>
    public DateTimeOffset DisabledAt { get; set; } = DateTimeOffset.UtcNow;
}
