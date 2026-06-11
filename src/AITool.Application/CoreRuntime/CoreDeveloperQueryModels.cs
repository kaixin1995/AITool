namespace AITool.Application.CoreRuntime;

/// <summary>
/// 开发者调用记录列表查询响应。
/// 由 Core 的 /api/core/developer/invocations/list 端点返回。
/// </summary>
public sealed class CoreDeveloperInvocationListResponse
{
    /// <summary>
    /// 调用记录总数。
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// 失败记录数。
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// 等待记录数。
    /// </summary>
    public int PendingCount { get; set; }

    /// <summary>
    /// 页码。
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// 每页记录数。
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// 总页数。
    /// </summary>
    public int TotalPages { get; set; }

    /// <summary>
    /// 记录列表。
    /// </summary>
    public List<CoreDeveloperInvocationSummary> Entries { get; set; } = [];
}

/// <summary>
/// 开发者调用摘要。
/// </summary>
public sealed class CoreDeveloperInvocationSummary
{
    /// <summary>
    /// 跟踪标识。
    /// </summary>
    public Guid TraceId { get; set; }

    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 格式化后的创建时间。
    /// </summary>
    public string CreatedAtText { get; set; } = string.Empty;

    /// <summary>
    /// 来源。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// 请求模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 摘要中的站点名称。
    /// </summary>
    public string SummarySite { get; set; } = string.Empty;

    /// <summary>
    /// 摘要中的模型名称。
    /// </summary>
    public string SummaryAttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 状态显示文本。
    /// </summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// 状态样式类名。
    /// </summary>
    public string StatusClass { get; set; } = string.Empty;

    /// <summary>
    /// 状态码。
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// 失败尝试次数。
    /// </summary>
    public int FailedAttemptCount { get; set; }

    /// <summary>
    /// 等待中的尝试次数。
    /// </summary>
    public int PendingAttemptCount { get; set; }

    /// <summary>
    /// 成功尝试次数。
    /// </summary>
    public int SuccessAttemptCount { get; set; }
}

/// <summary>
/// 开发者调用详情。
/// 由 Core 的 /api/core/developer/invocations/detail 端点返回。
/// </summary>
public sealed class CoreDeveloperInvocationDetail
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
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 格式化后的创建时间。
    /// </summary>
    public string CreatedAtText { get; set; } = string.Empty;

    /// <summary>
    /// 更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 格式化后的更新时间。
    /// </summary>
    public string UpdatedAtText { get; set; } = string.Empty;

    /// <summary>
    /// 来源。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 用户代理。
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// 客户端 IP。
    /// </summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 上游协议类型。
    /// </summary>
    public string UpstreamProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// 请求模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 尝试调用的模型。
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
    /// 摘要中的站点名称。
    /// </summary>
    public string SummarySite { get; set; } = string.Empty;

    /// <summary>
    /// 摘要中的模型名称。
    /// </summary>
    public string SummaryAttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 请求体。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;

    /// <summary>
    /// 请求头。
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = [];

    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 状态显示文本。
    /// </summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// 状态样式类名。
    /// </summary>
    public string StatusClass { get; set; } = string.Empty;

    /// <summary>
    /// 状态码。
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 响应体。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>
    /// 响应内容类型。
    /// </summary>
    public string ResponseContentType { get; set; } = string.Empty;

    /// <summary>
    /// 是否为流式响应。
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

    /// <summary>
    /// 失败尝试次数。
    /// </summary>
    public int FailedAttemptCount { get; set; }

    /// <summary>
    /// 等待中的尝试次数。
    /// </summary>
    public int PendingAttemptCount { get; set; }

    /// <summary>
    /// 成功尝试次数。
    /// </summary>
    public int SuccessAttemptCount { get; set; }

    /// <summary>
    /// 尝试记录列表。
    /// </summary>
    public List<CoreDeveloperInvocationAttempt> Attempts { get; set; } = [];
}

/// <summary>
/// 开发者调用尝试详情。
/// </summary>
public sealed class CoreDeveloperInvocationAttempt
{
    /// <summary>
    /// 尝试记录标识。
    /// </summary>
    public Guid AttemptId { get; set; }

    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 格式化后的创建时间。
    /// </summary>
    public string CreatedAtText { get; set; } = string.Empty;

    /// <summary>
    /// 更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 格式化后的更新时间。
    /// </summary>
    public string UpdatedAtText { get; set; } = string.Empty;

    /// <summary>
    /// 尝试调用的模型。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 上游协议类型。
    /// </summary>
    public string UpstreamProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 转发模式。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>
    /// 目标站点标识。
    /// </summary>
    public Guid? TargetSiteId { get; set; }

    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string TargetSiteName { get; set; } = string.Empty;

    /// <summary>
    /// 摘要中的站点名称。
    /// </summary>
    public string SummarySite { get; set; } = string.Empty;

    /// <summary>
    /// 摘要中的模型名称。
    /// </summary>
    public string SummaryAttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 状态显示文本。
    /// </summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// 状态样式类名。
    /// </summary>
    public string StatusClass { get; set; } = string.Empty;

    /// <summary>
    /// 状态码。
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 响应体。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>
    /// 响应内容类型。
    /// </summary>
    public string ResponseContentType { get; set; } = string.Empty;

    /// <summary>
    /// 是否为流式响应。
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
/// 开发者并发查询响应。
/// 由 Core 的 /api/core/developer/concurrency 端点返回。
/// </summary>
public sealed class CoreDeveloperConcurrencyResponse
{
    /// <summary>
    /// 最近刷新时间。
    /// </summary>
    public DateTimeOffset RefreshedAt { get; set; }

    /// <summary>
    /// 当前活跃并发项。
    /// </summary>
    public List<CoreDeveloperConcurrencyItem> Items { get; set; } = [];
}

/// <summary>
/// 当前模型并发检测项。
/// </summary>
public sealed class CoreDeveloperConcurrencyItem
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// 当前并发数。
    /// </summary>
    public int ActiveCount { get; set; }

    /// <summary>
    /// 配置的最大并发数，null 表示未设置限制。
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>
    /// 当前排队等待的请求数。
    /// </summary>
    public int QueueCount { get; set; }
}

/// <summary>
/// 开发者元数据查询响应。
/// 由 Core 的 /api/core/developer/metadata 端点返回。
/// 用于客户端模拟器的默认参数和模型列表。
/// </summary>
public sealed class CoreDeveloperMetadataResponse
{
    /// <summary>
    /// 默认访问密钥。
    /// </summary>
    public string DefaultAccessKey { get; set; } = string.Empty;

    /// <summary>
    /// 默认 OpenAI 模型。
    /// </summary>
    public string DefaultOpenAiModel { get; set; } = string.Empty;

    /// <summary>
    /// 默认 Anthropic 模型。
    /// </summary>
    public string DefaultAnthropicModel { get; set; } = string.Empty;

    /// <summary>
    /// 可用的调试模型列表。
    /// </summary>
    public List<CoreDeveloperModelItem> Models { get; set; } = [];
}

/// <summary>
/// 开发者调试模型项。
/// </summary>
public sealed class CoreDeveloperModelItem
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 路由数量。
    /// </summary>
    public int RouteCount { get; set; }

    /// <summary>
    /// 是否支持 OpenAI 原生协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; }

    /// <summary>
    /// 是否支持 Anthropic 原生协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 是否可通过 OpenAI 兼容方式使用。
    /// </summary>
    public bool CanUseOpenAi { get; set; }

    /// <summary>
    /// 是否可通过 Anthropic 兼容方式使用。
    /// </summary>
    public bool CanUseAnthropic { get; set; }
}
