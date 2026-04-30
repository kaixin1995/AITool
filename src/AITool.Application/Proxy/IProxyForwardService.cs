namespace AITool.Application.Proxy;

// 代理转发请求参数
public sealed class ProxyForwardRequest
{
    // 目标站点根地址
    public string TargetBaseUrl { get; set; } = string.Empty;

    // 目标站点 API 密钥
    public string TargetApiKey { get; set; } = string.Empty;

    // 协议类型，例如 OpenAI 或 Anthropic
    public string ProtocolType { get; set; } = "OpenAI";

    // 目标站点上的模型名称
    public string TargetModelName { get; set; } = string.Empty;

    // 原始请求体（JSON 字符串）
    public string RequestBody { get; set; } = string.Empty;

    // 是否启用流式
    public bool EnableStreaming { get; set; }

    // 单次请求超时时间（秒）
    public int RequestTimeoutSeconds { get; set; }

    // 单路由内部失败重试次数
    public int RetryCount { get; set; }
}

// 代理转发结果
public sealed class ProxyForwardResult
{
    // 是否成功
    public bool Success { get; set; }

    // 响应状态码
    public int StatusCode { get; set; }

    // 响应体内容
    public string ResponseBody { get; set; } = string.Empty;

    // 输入 Token 数
    public int InputTokens { get; set; }

    // 缓存 Token 数
    public int CachedTokens { get; set; }

    // 输出 Token 数
    public int OutputTokens { get; set; }

    // 是否为流式请求
    public bool IsStreaming { get; set; }

    // 首字耗时（毫秒）
    public int FirstTokenLatencyMs { get; set; }

    // 首字后的后续耗时（毫秒）
    public int StreamDurationMs { get; set; }

    // 请求总耗时（毫秒）
    public int TotalDurationMs { get; set; }

    // 错误信息
    public string? ErrorMessage { get; set; }
}

// 代理转发服务接口，将请求转发到目标站点并返回结果
public interface IProxyForwardService
{
    // 根据协议类型将请求转发到目标站点
    Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default);
}
