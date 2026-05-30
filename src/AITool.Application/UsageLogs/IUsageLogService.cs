namespace AITool.Application.UsageLogs;

/// <summary>
/// 使用日志服务接口，负责写入代理调用过程中的统计与状态信息。
/// </summary>
public interface IUsageLogService
{
    /// <summary>
    /// 记录一条完整的使用日志条目，供后续查询、统计和排障使用。
    /// </summary>
    Task LogAsync(UsageLogEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// 使用日志条目，用于描述一次请求链路中某次实际调用的关键数据。
/// </summary>
public sealed class UsageLogEntry
{
    /// <summary>
    /// 标识同一条代理请求链路，便于将多次重试记录串联起来。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 记录发起请求所使用的平台访问密钥。
    /// </summary>
    public Guid AccessKeyId { get; set; }

    /// <summary>
    /// 标识本次调用所走的协议类型，例如 OpenAI 或 Anthropic。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 标识当前调用是直接透传还是兼容中转。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>
    /// 记录客户端原始请求中的模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 记录本次实际尝试转发到上游的模型名称。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 标识当前尝试命中的目标站点。
    /// </summary>
    public Guid TargetSiteId { get; set; }

    /// <summary>
    /// 保存本次处理结果状态，例如成功、失败或中断。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 标记日志来源，便于区分代理转发、聊天调试等不同入口。
    /// </summary>
    public string Source { get; set; } = "proxy";

    /// <summary>
    /// 记录请求链路中累计尝试过多少条路由，便于分析重试情况。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 标记当前是该请求链路中的第几次实际尝试。
    /// </summary>
    public int AttemptIndex { get; set; }

    /// <summary>
    /// 指示当前这条日志是否代表整条请求链路的最终结果。
    /// </summary>
    public bool IsFinalResult { get; set; }

    /// <summary>
    /// 标记本次失败后是否继续触发了后续 fallback 逻辑。
    /// </summary>
    public bool FallbackTriggered { get; set; }

    /// <summary>
    /// 保存本次尝试产生的错误信息；成功时通常为空。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 记录输入消耗的 Token 数。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 记录命中缓存所对应的 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 记录输出生成的 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 标记本次请求是否采用流式返回。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 标记流式过程中是否发生了异常中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>
    /// 记录从发起请求到收到首个输出片段的耗时，单位为毫秒。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>
    /// 记录首个输出片段之后到流结束的持续时间，单位为毫秒。
    /// </summary>
    public int StreamDurationMs { get; set; }

    /// <summary>
    /// 记录本次请求从开始到结束的整体耗时，单位为毫秒。
    /// </summary>
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// 记录请求使用的思考强度配置，便于后续做成本与效果分析。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 记录该条使用日志对应的请求开始时间。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
