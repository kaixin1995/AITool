using AITool.Domain.Operations;

namespace AITool.Application.Operations;

/// <summary>
/// 系统运行时设置服务接口，负责统一读取、更新和维护系统级持久化配置。
/// </summary>
public interface ISystemRuntimeSettingsService
{
    /// <summary>
    /// 获取当前系统设置；当数据库中还没有记录时，按默认值自动创建一份。
    /// </summary>
    Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按请求内容更新系统运行时设置，并返回保存后的最新结果。
    /// </summary>
    Task<SystemRuntimeSettings> UpdateAsync(UpdateSystemRuntimeSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按给定条件清空使用日志，并返回本次实际删除的记录数量。
    /// </summary>
    Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 系统运行时设置更新请求，用于承载后台配置页提交的各项参数。
/// </summary>
public sealed class UpdateSystemRuntimeSettingsRequest
{
    /// <summary>
    /// 控制代理转发请求的超时时间，单位为秒。
    /// </summary>
    public int ProxyRequestTimeoutSeconds { get; set; }

    /// <summary>
    /// 控制代理转发在失败时的重试次数。
    /// </summary>
    public int ProxyRetryCount { get; set; }

    /// <summary>
    /// 控制模型检测请求的超时时间，单位为秒。
    /// </summary>
    public int DetectionRequestTimeoutSeconds { get; set; }

    /// <summary>
    /// 控制模型检测失败后的重试次数。
    /// </summary>
    public int DetectionRetryCount { get; set; }

    /// <summary>
    /// 控制批量检测时允许的最大并发数。
    /// </summary>
    public int DetectionConcurrency { get; set; }

    /// <summary>
    /// 配置熔断器触发所需的连续失败次数阈值。
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; }

    /// <summary>
    /// 配置熔断后等待多久再尝试恢复，单位为分钟。
    /// </summary>
    public int CircuitBreakerRecoveryMinutes { get; set; }

    /// <summary>
    /// 指定使用日志的保留天数，超过该范围的数据可被清理。
    /// </summary>
    public int UsageLogRetentionDays { get; set; }

    /// <summary>
    /// 控制是否开启使用日志的自动清理任务。
    /// </summary>
    public bool UsageLogAutoCleanupEnabled { get; set; }

    /// <summary>
    /// 控制系统中面向开发调试的功能开关是否启用。
    /// </summary>
    public bool DeveloperFeaturesEnabled { get; set; }

    /// <summary>
    /// 并发打满时的处理策略：0 = 跳到下一顺位，1 = 排队等待。
    /// </summary>
    public int ConcurrencyMode { get; set; }

    /// <summary>
    /// 并发排队等待的最大时间（秒），仅在 WaitForSlot 模式下生效。
    /// </summary>
    public int ConcurrencyQueueTimeoutSeconds { get; set; }

    /// <summary>
    /// Codex 功能总开关（含 OAuth 账号、凭证导入、巡检）。关闭后隐藏 Codex 页面并禁用所有 Codex 托管站点。
    /// </summary>
    public bool CodexFeaturesEnabled { get; set; }

    /// <summary>
    /// Codex 巡检自动执行开关。
    /// </summary>
    public bool CodexInspectionEnabled { get; set; }

    /// <summary>
    /// Codex 巡检周期（秒），下限 30。
    /// </summary>
    public int CodexInspectionIntervalSeconds { get; set; }

    /// <summary>
    /// Codex 额度缓存最大小时数，超过则强制真实刷新。
    /// </summary>
    public int CodexQuotaMaxCacheHours { get; set; }

    /// <summary>
    /// Codex 自动禁用阈值（百分比，1-100）。
    /// 当任一关键额度窗口的已使用百分比达到该阈值时，账号自动禁用。
    /// </summary>
    public int CodexAutoDisableThresholdPercent { get; set; }

    /// <summary>
    /// Codex 巡检缓存复用开关。关闭时每轮巡检都真实刷新额度；开启时未被使用的账号沿用缓存快照。
    /// </summary>
    public bool CodexInspectionCacheEnabled { get; set; }
}

/// <summary>
/// 使用日志清空请求，用于描述本次删除操作的筛选范围。
/// </summary>
public sealed class ClearUsageLogsRequest
{
    /// <summary>
    /// 指定来源时，仅清空该来源下的日志数据。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 指定开始时间后，仅清空该时间点及之后的日志。
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// 指定结束时间后，仅清空该时间点及之前的日志。
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }
}
