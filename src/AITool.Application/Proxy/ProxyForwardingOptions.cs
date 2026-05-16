namespace AITool.Application.Proxy;

/// <summary>
/// 代理转发配置，用于集中承载请求超时和重试等运行参数。
/// </summary>
public sealed class ProxyForwardingOptions
{
    /// <summary>
    /// 配置节名称，供选项绑定时定位到对应的配置节点。
    /// </summary>
    public const string SectionName = "ProxyForwarding";

    /// <summary>
    /// 单次上游请求允许的最长处理时间，单位为秒。
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 同一路由在内部重试的次数，不包含首次请求。
    /// </summary>
    public int RetryCount { get; set; } = 0;
}
