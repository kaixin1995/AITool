using AITool.Domain.Operations;

namespace AITool.Application.Operations;

// 系统运行时设置服务接口，统一读取和更新持久化配置
public interface ISystemRuntimeSettingsService
{
    // 获取当前系统设置，不存在时按默认值创建
    Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default);

    // 更新系统运行时设置并持久化到数据库
    Task<SystemRuntimeSettings> UpdateAsync(UpdateSystemRuntimeSettingsRequest request, CancellationToken cancellationToken = default);

    // 按条件清空使用日志并返回删除数量
    Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request, CancellationToken cancellationToken = default);
}

// 系统运行时设置更新请求
public sealed class UpdateSystemRuntimeSettingsRequest
{
    // 代理请求超时时间（秒）
    public int ProxyRequestTimeoutSeconds { get; set; }

    // 代理失败重试次数
    public int ProxyRetryCount { get; set; }

    // 检测请求超时时间（秒）
    public int DetectionRequestTimeoutSeconds { get; set; }

    // 检测失败重试次数
    public int DetectionRetryCount { get; set; }

    // 检测并发数
    public int DetectionConcurrency { get; set; }

    // 熔断连续失败阈值
    public int CircuitBreakerFailureThreshold { get; set; }

    // 熔断恢复时间（分钟）
    public int CircuitBreakerRecoveryMinutes { get; set; }

    // 使用日志保留天数
    public int UsageLogRetentionDays { get; set; }

    // 是否启用使用日志自动清理
    public bool UsageLogAutoCleanupEnabled { get; set; }
}

// 使用日志清空请求
public sealed class ClearUsageLogsRequest
{
    // 指定来源时只清空该来源的数据
    public string Source { get; set; } = string.Empty;

    // 指定开始时间时只清空区间内数据
    public DateTimeOffset? StartTime { get; set; }

    // 指定结束时间时只清空区间内数据
    public DateTimeOffset? EndTime { get; set; }
}
