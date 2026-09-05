namespace AITool.Application.Proxy;

/// <summary>
/// 代理调用统一上下文，在代理管道中一次性采集所有信息，
/// 再由 <see cref="IProxyCallRecorder"/> 派发到 UsageLog、DeveloperInvocationTrace、ConversationLog 三个存储。
/// </summary>
public sealed class ProxyCallContext
{
    // 请求级标识（整个调用链共享）

    /// <summary>
    /// 本次代理调用链的唯一标识，同一请求的多路由尝试共享此值。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 平台访问密钥标识。
    /// </summary>
    public Guid AccessKeyId { get; set; }

    /// <summary>
    /// 客户端协议类型（Anthropic / OpenAI）。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 请求来源入口（proxy / chat / developer-simulator 等）。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 客户端请求的模型名称（外部路由名）。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 思考等级（low / medium / high），由请求体解析而来。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 是否为流式请求。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 原始请求体 JSON。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// 请求发起时刻。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; }

    // 开发者追踪 — 请求级上下文

    /// <summary>
    /// 客户端 User-Agent 头。
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// 客户端 IP 地址。
    /// </summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>
    /// 原始请求头（开发者追踪需要完整头信息）。
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // 本次路由尝试级字段（每次尝试覆盖）

    /// <summary>
    /// 当前尝试索引（从 1 开始）。
    /// </summary>
    public int AttemptIndex { get; set; }

    /// <summary>
    /// 当前尝试使用的上游模型名称。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 上游协议类型（可能与客户端协议不同，如 Anthropic 请求走 OpenAI 上游）。
    /// </summary>
    public string UpstreamProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 转发模式（direct 直连 / bridge 兼容转发）。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>
    /// 目标站点标识。
    /// </summary>
    public Guid TargetSiteId { get; set; }

    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string TargetSiteName { get; set; } = string.Empty;

    /// <summary>
    /// 路由标识（用于熔断器）。
    /// </summary>
    public Guid RouteId { get; set; }

    /// <summary>
    /// 预处理后的请求体（已替换模型名称等字段）。
    /// </summary>
    public string PreparedRequestBody { get; set; } = string.Empty;

    // 转发结果字段

    /// <summary>
    /// 本次尝试是否成功。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 上游返回的 HTTP 状态码。
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// 失败时的错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 上游响应体。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>
    /// 适配后的响应体（协议桥接后返回给客户端的内容，开发者追踪需要）。
    /// </summary>
    public string AdaptedResponseBody { get; set; } = string.Empty;

    /// <summary>
    /// 响应内容类型。
    /// </summary>
    public string ResponseContentType { get; set; } = string.Empty;

    // Token 统计

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

    // 耗时统计

    /// <summary>
    /// 首个 Token 延迟，单位毫秒。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>
    /// 流式持续时间，单位毫秒。
    /// </summary>
    public int StreamDurationMs { get; set; }

    /// <summary>
    /// 总耗时，单位毫秒。
    /// </summary>
    public int TotalDurationMs { get; set; }

    // 流式特殊字段

    /// <summary>
    /// 是否已经收到上游首个流式数据块。
    /// </summary>
    public bool HasStartedStreaming { get; set; }

    /// <summary>
    /// 流式响应过程中是否出现异常中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }

    // UsageLog 专用字段

    /// <summary>
    /// 失败重试次数（成功时为当前 attemptIndex - 1，失败时为 attemptIndex）。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 429 速率限制重试次数（本路由候选内按退避间隔连续重发的次数）。
    /// </summary>
    public int RateLimitRetries { get; set; }

    /// <summary>
    /// 是否为最终结果（成功时 true）。
    /// </summary>
    public bool IsFinalResult { get; set; }

    /// <summary>
    /// 是否触发了路由回退。
    /// </summary>
    public bool FallbackTriggered { get; set; }

    // ConversationLog 预提取字段

    /// <summary>
    /// 预提取的用户输入文本。
    /// 当非空时，<see cref="IProxyCallRecorder.RecordConversationAsync"/> 将直接使用此值，
    /// 而不从 <see cref="RequestBody"/> 中提取。
    /// 适用于 Chat 调试页等已经持有原始文本的调用方。
    /// </summary>
    public string PreExtractedUserInputText { get; set; } = string.Empty;

    /// <summary>
    /// 预提取的助手输出 Markdown。
    /// 当非空时，<see cref="IProxyCallRecorder.RecordConversationAsync"/> 将直接使用此值，
    /// 而不从 <see cref="ResponseBody"/> 中提取。
    /// 适用于流式场景下原始响应体已被逐块消费、无法回放完整 JSON 的情况。
    /// </summary>
    public string PreExtractedAssistantOutput { get; set; } = string.Empty;
}
