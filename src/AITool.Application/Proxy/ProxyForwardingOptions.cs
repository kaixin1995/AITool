namespace AITool.Application.Proxy;

// 代理转发配置，控制单路由请求超时和失败重试次数
public sealed class ProxyForwardingOptions
{
    public const string SectionName = "ProxyForwarding";

    // 单次请求超时时间（秒）
    public int RequestTimeoutSeconds { get; set; } = 60;

    // 单路由内部失败重试次数
    public int RetryCount { get; set; } = 0;
}
