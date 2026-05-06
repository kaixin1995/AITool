namespace AITool.Domain.Proxy;

// 代理使用日志，记录每次代理请求的详细信息
public sealed class ProxyUsageLog
{
    // 日志主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 同一条代理请求链路的唯一标识
    public Guid RequestId { get; set; }

    // 平台访问密钥标识
    public Guid AccessKeyId { get; set; }

    // 协议类型，例如 OpenAI 或 Anthropic
    public string ProtocolType { get; set; } = string.Empty;

    // 请求的模型名称
    public string RequestModel { get; set; } = string.Empty;

    // 当前尝试的上游模型名称
    public string AttemptedModel { get; set; } = string.Empty;

    // 命中的目标站点标识
    public Guid TargetSiteId { get; set; }

    // 请求处理状态
    public string Status { get; set; } = string.Empty;

    // 请求来源，例如 "proxy" 或 "chat"
    public string Source { get; set; } = "proxy";

    // 尝试的路由数量（重试次数）
    public int RetryCount { get; set; }

    // 当前是链路中的第几次尝试
    public int AttemptIndex { get; set; }

    // 当前日志是否为最终结果
    public bool IsFinalResult { get; set; }

    // 当前失败后是否触发了 fallback
    public bool FallbackTriggered { get; set; }

    // 当前尝试的错误信息
    public string ErrorMessage { get; set; } = string.Empty;

    // 输入 Token 数
    public int InputTokens { get; set; }

    // 缓存 Token 数
    public int CachedTokens { get; set; }

    // 输出 Token 数
    public int OutputTokens { get; set; }

    // 总 Token 数
    public int TotalTokens { get; set; }

    // 是否为流式响应
    public bool IsStreaming { get; set; }

    // 是否发生流式异常中断
    public bool IsStreamInterrupted { get; set; }

    // 首字耗时（毫秒）
    public int FirstTokenLatencyMs { get; set; }

    // 首字后的后续耗时（毫秒）
    public int StreamDurationMs { get; set; }

    // 请求总耗时（毫秒）
    public int TotalDurationMs { get; set; }

    // 请求时间
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
