namespace AITool.Domain.Proxy;

/// <summary>
/// 表示一条代理使用日志，用于记录一次请求链路中的调用目标、重试过程、耗时表现和结果状态。
/// </summary>
public sealed class ProxyUsageLog
{
    /// <summary>
    /// 日志唯一标识，用于区分每一条独立的调用记录。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 请求链路唯一标识，用于将同一次代理请求产生的多条尝试记录串联起来。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 访问密钥标识，用于追踪本次请求是由哪个平台密钥发起的。
    /// </summary>
    public Guid AccessKeyId { get; set; }

    /// <summary>
    /// 协议类型，例如 OpenAI 或 Anthropic，用于区分请求采用的协议格式。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 客户端请求的模型名称，用于保留调用方原始指定的目标模型。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 当前这次尝试实际命中的上游模型名称，用于反映路由或降级后的真实请求目标。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 命中的目标站点标识，用于记录本次请求被转发到哪个站点处理。
    /// </summary>
    public Guid TargetSiteId { get; set; }

    /// <summary>
    /// 当前请求处理状态，用于描述成功、失败或其他阶段性结果。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 请求来源，例如 proxy 或 chat，用于区分日志来自哪个业务入口。
    /// </summary>
    public string Source { get; set; } = "proxy";

    /// <summary>
    /// 本次请求链路中累计尝试的路由数量，可用于反映重试或切换次数。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 当前记录对应第几次尝试，用于还原重试顺序和排查链路过程。
    /// </summary>
    public int AttemptIndex { get; set; }

    /// <summary>
    /// 标记当前日志是否代表整条请求链路的最终结果，便于快速筛选最终状态。
    /// </summary>
    public bool IsFinalResult { get; set; }

    /// <summary>
    /// 标记本次失败后是否触发了 fallback，用于判断是否发生了备用路由切换。
    /// </summary>
    public bool FallbackTriggered { get; set; }

    /// <summary>
    /// 当前尝试的错误信息，用于记录失败原因或异常摘要。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 输入 Token 数，用于统计提示词或请求正文消耗的输入量。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 命中缓存的 Token 数，用于记录可复用缓存带来的输入节省情况。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 输出 Token 数，用于统计模型响应内容的生成量。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 总 Token 数，用于汇总本次调用在计费或统计层面的整体消耗。
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// 标记本次响应是否采用流式返回，用于区分不同的响应模式。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 标记流式响应过程中是否出现异常中断，用于排查流式输出不完整的问题。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>
    /// 首字耗时，单位为毫秒，用于衡量请求发出后首次收到响应内容的等待时间。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>
    /// 首字之后的流式耗时，单位为毫秒，用于衡量流式输出阶段的持续时间。
    /// </summary>
    public int StreamDurationMs { get; set; }

    /// <summary>
    /// 请求总耗时，单位为毫秒，用于记录从发起到结束的完整处理时间。
    /// </summary>
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// 思考等级，用于记录模型调用时附带的 reasoning effort 等推理强度参数。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 请求时间，用于记录该条日志对应请求开始进入系统的时间点。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
