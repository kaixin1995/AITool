namespace AITool.Domain.Operations;

// 系统运行时设置，集中存储代理、检测与日志相关配置
public sealed class SystemRuntimeSettings
{
    // 固定单例主键，数据库中始终只保留一条记录
    public int Id { get; set; } = 1;

    // 代理请求超时时间（秒）
    public int ProxyRequestTimeoutSeconds { get; set; } = 60;

    // 代理失败重试次数
    public int ProxyRetryCount { get; set; } = 1;

    // 检测请求超时时间（秒）
    public int DetectionRequestTimeoutSeconds { get; set; } = 60;

    // 检测失败重试次数
    public int DetectionRetryCount { get; set; } = 0;

    // 检测并发数
    public int DetectionConcurrency { get; set; } = 1;

    // 熔断连续失败阈值
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    // 熔断恢复时间（分钟）
    public int CircuitBreakerRecoveryMinutes { get; set; } = 2;

    // 使用日志保留天数
    public int UsageLogRetentionDays { get; set; } = 7;

    // 是否启用使用日志自动清理
    public bool UsageLogAutoCleanupEnabled { get; set; } = true;

    // 是否启用开发者功能
    public bool DeveloperFeaturesEnabled { get; set; }

    // 最近一次使用日志清理时间
    public DateTimeOffset? LastUsageLogPrunedAt { get; set; }

    // 最近一次使用日志清理数量
    public int LastUsageLogPrunedCount { get; set; }
}
