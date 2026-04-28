namespace AITool.Domain.Operations;

// 系统运行时设置，集中存储代理和日志保留相关配置
public sealed class SystemRuntimeSettings
{
    // 固定单例主键，数据库中始终只保留一条记录
    public int Id { get; set; } = 1;

    // 代理请求超时时间（秒）
    public int ProxyRequestTimeoutSeconds { get; set; } = 60;

    // 代理失败重试次数
    public int ProxyRetryCount { get; set; } = 1;

    // 使用日志保留天数
    public int UsageLogRetentionDays { get; set; } = 7;

    // 检测日志保留天数
    public int DetectionLogRetentionDays { get; set; } = 7;

    // 最近一次使用日志清理时间
    public DateTimeOffset? LastUsageLogPrunedAt { get; set; }

    // 最近一次使用日志清理数量
    public int LastUsageLogPrunedCount { get; set; }

    // 最近一次检测日志清理时间
    public DateTimeOffset? LastDetectionLogPrunedAt { get; set; }

    // 最近一次检测日志清理数量
    public int LastDetectionLogPrunedCount { get; set; }
}
