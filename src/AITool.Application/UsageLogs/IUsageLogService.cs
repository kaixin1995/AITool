namespace AITool.Application.UsageLogs;

// 使用日志服务接口，记录代理请求的使用情况
public interface IUsageLogService
{
    // 记录一次代理请求的使用日志
    Task LogAsync(UsageLogEntry entry, CancellationToken cancellationToken = default);
}

// 使用日志条目
public sealed class UsageLogEntry
{
    // 同一条代理请求链路的唯一标识
    public Guid RequestId { get; set; }

    // 平台访问密钥标识
    public Guid AccessKeyId { get; set; }

    // 协议类型
    public string ProtocolType { get; set; } = string.Empty;

    // 请求模型名称
    public string RequestModel { get; set; } = string.Empty;

    // 当前尝试的上游模型名称
    public string AttemptedModel { get; set; } = string.Empty;

    // 目标站点标识
    public Guid TargetSiteId { get; set; }

    // 处理状态
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

    // 输出 Token 数
    public int OutputTokens { get; set; }
}
