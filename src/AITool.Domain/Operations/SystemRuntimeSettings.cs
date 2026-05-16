namespace AITool.Domain.Operations;

/// <summary>
/// 表示系统运行期使用的一组集中配置，用于统一控制代理请求、检测任务、熔断策略和日志清理行为。
/// </summary>
public sealed class SystemRuntimeSettings
{
    /// <summary>
    /// 固定主键值，表示这是一张单例配置表，数据库中预期始终只保留一条记录。
    /// </summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// 代理请求超时时间，单位为秒，用于限制单次代理转发请求的最长等待时长。
    /// </summary>
    public int ProxyRequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 代理请求失败后的最大重试次数，用于控制路由重试或重新转发的上限。
    /// </summary>
    public int ProxyRetryCount { get; set; } = 1;

    /// <summary>
    /// 检测请求超时时间，单位为秒，用于限制健康检测或探测请求的最长执行时长。
    /// </summary>
    public int DetectionRequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 检测请求失败后的最大重试次数，用于控制单次检测任务内部的重试策略。
    /// </summary>
    public int DetectionRetryCount { get; set; } = 0;

    /// <summary>
    /// 检测并发数，用于限制同一时刻并行执行的检测任务数量，避免占用过多资源。
    /// </summary>
    public int DetectionConcurrency { get; set; } = 1;

    /// <summary>
    /// 熔断连续失败阈值，当同一路径累计失败达到该值时可进入熔断状态。
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// 熔断恢复时间，单位为分钟，用于控制进入熔断后多久允许再次尝试恢复调用。
    /// </summary>
    public int CircuitBreakerRecoveryMinutes { get; set; } = 2;

    /// <summary>
    /// 使用日志保留天数，超过该时间范围的历史日志可被清理。
    /// </summary>
    public int UsageLogRetentionDays { get; set; } = 7;

    /// <summary>
    /// 标记是否启用使用日志自动清理，用于控制系统是否定期删除过期日志。
    /// </summary>
    public bool UsageLogAutoCleanupEnabled { get; set; } = true;

    /// <summary>
    /// 标记是否启用开发者功能，用于集中控制面向调试或高级配置的功能入口。
    /// </summary>
    public bool DeveloperFeaturesEnabled { get; set; }

    /// <summary>
    /// 最近一次执行使用日志清理的时间，用于展示或判断自动清理的运行情况。
    /// </summary>
    public DateTimeOffset? LastUsageLogPrunedAt { get; set; }

    /// <summary>
    /// 最近一次使用日志清理删除的记录数量，用于保留清理结果的统计信息。
    /// </summary>
    public int LastUsageLogPrunedCount { get; set; }
}
