namespace AITool.Domain.Proxy;

// 代理使用日志，记录每次代理请求的详细信息
public sealed class ProxyUsageLog
{
    // 日志主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 平台访问密钥标识
    public Guid AccessKeyId { get; set; }

    // 协议类型，例如 OpenAI 或 Anthropic
    public string ProtocolType { get; set; } = string.Empty;

    // 请求的模型名称
    public string RequestModel { get; set; } = string.Empty;

    // 命中的目标站点标识
    public Guid TargetSiteId { get; set; }

    // 请求处理状态
    public string Status { get; set; } = string.Empty;

    // 请求来源，例如 "proxy" 或 "chat"
    public string Source { get; set; } = "proxy";

    // 尝试的路由数量（重试次数）
    public int RetryCount { get; set; }

    // 输入 Token 数
    public int InputTokens { get; set; }

    // 输出 Token 数
    public int OutputTokens { get; set; }

    // 总 Token 数
    public int TotalTokens { get; set; }

    // 请求时间
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
