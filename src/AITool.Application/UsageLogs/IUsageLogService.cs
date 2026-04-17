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
    // 平台访问密钥标识
    public Guid AccessKeyId { get; set; }

    // 协议类型
    public string ProtocolType { get; set; } = string.Empty;

    // 请求模型名称
    public string RequestModel { get; set; } = string.Empty;

    // 目标站点标识
    public Guid TargetSiteId { get; set; }

    // 处理状态
    public string Status { get; set; } = string.Empty;

    // 输入 Token 数
    public int InputTokens { get; set; }

    // 输出 Token 数
    public int OutputTokens { get; set; }
}
