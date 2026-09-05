namespace AITool.Application.Proxy;

using System.Diagnostics.CodeAnalysis;
using Sites;

/// <summary>
/// 代理转发请求参数，用于描述一次上游调用所需的完整上下文。
/// </summary>
public sealed class ProxyForwardRequest
{
    /// <summary>
    /// 目标站点根地址，转发层会基于该地址拼接具体接口路径。
    /// </summary>
    public string TargetBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 目标站点接口路径模式，决定默认接口是否需要补 /v1 前缀。
    /// </summary>
    public string TargetEndpointPathMode { get; set; } = SiteEndpointPathResolver.StandardRoot;

    /// <summary>
    /// 目标站点 API 密钥，用于上游鉴权。
    /// </summary>
    public string TargetApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型，例如 OpenAI 或 Anthropic，用于决定请求格式和目标接口。
    /// </summary>
    public string ProtocolType { get; set; } = "OpenAI";

    /// <summary>
    /// 上游站点实际接收的模型名称，通常已完成路由映射。
    /// </summary>
    public string TargetModelName { get; set; } = string.Empty;

    /// <summary>
    /// 原始请求体 JSON 字符串，保留调用方提交的完整内容。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;

    /// <summary>
    /// 预先替换目标模型后的请求体，供转发层直接复用，避免重复解析和改写原始 JSON。
    /// </summary>
    public string? PreparedRequestBody { get; set; }

    /// <summary>
    /// 标记本次请求是否需要按流式方式读取并转发响应。
    /// </summary>
    public bool EnableStreaming { get; set; }

    /// <summary>
    /// 单次上游请求的超时时间，单位为秒。
    /// </summary>
    public int RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// 流式转发的空闲超时（秒）：相邻两次读到上游数据的最大间隔，超时判定上游挂起并终止。
    /// 0 表示不启用（默认）。请求建立阶段的超时仍由 <see cref="RequestTimeoutSeconds"/> 控制。
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; }

    /// <summary>
    /// 同一路由在内部允许的失败重试次数，不包含首次请求。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 上游返回 429（速率限制）时的连续重试次数，默认 0（一次 429 即失败）。
    /// 设为 N&gt;0 时：连续 N 次 429 才判定该路由失败；期间任一次成功即算成功。
    /// </summary>
    public int RateLimitRetryCount { get; set; }

    /// <summary>
    /// 自定义目标路径；未指定时通常按协议使用默认聊天接口。
    /// </summary>
    public string? TargetPath { get; set; }

    /// <summary>
    /// 需要额外透传给上游的请求头，使用不区分大小写的键比较方式。
    /// </summary>
    public Dictionary<string, string> ForwardHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 站点专用出口网络代理地址（如 http://127.0.0.1:7890 或 socks5://127.0.0.1:10808），为空则走服务器默认网络。
    /// </summary>
    public string? EgressProxyUrl { get; set; }

    /// <summary>
    /// 上游返回 401 时刷新特定凭证并返回新 API Key；普通站点不设置此回调。
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? RefreshTargetApiKeyAsync { get; set; }

    /// <summary>
    /// 上游返回 403 时禁用特定托管凭证；普通站点不设置此回调。
    /// </summary>
    public Func<CancellationToken, Task>? DisableTargetCredentialAsync { get; set; }

    /// <summary>
    /// 发起上游请求前准备托管凭证或项目配置；参数为当前请求使用的 API Key，普通站点不设置此回调。
    /// </summary>
    public Func<string, CancellationToken, Task>? PrepareTargetCredentialAsync { get; set; }
}

/// <summary>
/// 代理转发结果，用于汇总一次上游调用的状态、耗时和统计数据。
/// </summary>
public sealed class ProxyForwardResult
{
    /// <summary>
    /// 标记本次转发是否成功完成。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 记录上游返回的 HTTP 状态码。
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// 保存上游响应体内容；流式场景下通常用于补充最终结果或错误信息。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>
    /// 记录本次请求消耗的输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 记录本次请求命中的缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 记录本次请求生成的输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 标记本次调用是否按流式方式处理。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 标记是否已经收到上游首个流式数据块，便于区分首包前失败和中途失败。
    /// </summary>
    public bool HasStartedStreaming { get; set; }

    /// <summary>
    /// 标记流式响应过程中是否出现异常中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>
    /// 本次转发实际执行的 429 速率限制重试次数（连续 429 后退避重发的次数）。
    /// 供 usage 日志链路展示，验证 RateLimitRetryCount 配置是否真的生效。
    /// </summary>
    public int RateLimitRetryCount { get; set; }

    /// <summary>
    /// 标记本次调用是否因客户端主动取消而结束；取消不应触发后续路由回退。
    /// </summary>
    public bool IsCanceled { get; set; }

    /// <summary>
    /// 记录从发起请求到收到首个输出片段的耗时，单位为毫秒。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>
    /// 记录首个输出片段之后到流结束的持续时间，单位为毫秒。
    /// </summary>
    public int StreamDurationMs { get; set; }

    /// <summary>
    /// 记录本次请求整体耗时，单位为毫秒。
    /// </summary>
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// 保存失败或异常时的错误信息，成功时通常为空。
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 代理转发服务接口，负责将标准化请求发送到目标站点并返回统一结果。
/// </summary>
public interface IProxyForwardService
{
    /// <summary>
    /// 按指定协议执行一次普通转发请求，并返回完整的调用结果。
    /// </summary>
    Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行流式转发，并在收到每段 SSE 数据时回调给上层做实时透传或转换。
    /// </summary>
    Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default);
}
