using SqlSugar;

namespace AITool.Domain.Operations;

/// <summary>
/// 表示系统运行期使用的一组集中配置，用于统一控制代理请求、检测任务、熔断策略和日志清理行为。
/// </summary>
[SugarTable("SystemRuntimeSettings")]
public sealed class SystemRuntimeSettings
{
    /// <summary>
    /// 固定主键值，表示这是一张单例配置表，数据库中预期始终只保留一条记录。
    /// IsIdentity=false 替代原 EF 的 ValueGeneratedNever()，确保插入时使用固定的 Id=1。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
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
    /// 标记是否启用对话记录功能，用于控制对话记录页面显示以及对话记录写入。
    /// </summary>
    public bool ConversationLogEnabled { get; set; } = true;

    /// <summary>
    /// 并发打满时的处理策略。
    /// 0 = SkipOnFull：跳到下一顺位模型；
    /// 1 = WaitForSlot：排队等待直到释放或超时。
    /// </summary>
    public int ConcurrencyMode { get; set; }

    /// <summary>
    /// 并发排队等待的最大时间，单位为秒。
    /// 仅在 WaitForSlot 模式下生效；超时后顺延到下一顺位模型。
    /// 默认 120 秒。
    /// </summary>
    public int ConcurrencyQueueTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 最近一次执行使用日志清理的时间，用于展示或判断自动清理的运行情况。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastUsageLogPrunedAt { get; set; }

    /// <summary>
    /// 最近一次使用日志清理删除的记录数量，用于保留清理结果的统计信息。
    /// </summary>
    public int LastUsageLogPrunedCount { get; set; }

    /// <summary>
    /// Codex 功能总开关（含 OAuth 账号、凭证导入、巡检）。
    /// 关闭后隐藏 Codex 页面入口，并把所有 Codex 托管站点置为禁用（路由/模型/对话测试不再命中）。
    /// </summary>
    public bool CodexFeaturesEnabled { get; set; }

    /// <summary>
    /// Codex 巡检自动执行开关。仅在 CodexFeaturesEnabled 开启时生效。
    /// </summary>
    public bool CodexInspectionEnabled { get; set; }

    /// <summary>
    /// Codex 巡检周期（秒），下限 30。每隔该周期执行一轮账号额度巡检。
    /// </summary>
    public int CodexInspectionIntervalSeconds { get; set; } = 1800;

    /// <summary>
    /// Codex 额度缓存最大小时数。超过该时长未真实刷新的账号，巡检时强制真实刷新（codex-patrol 缺失的兜底）。
    /// </summary>
    public int CodexQuotaMaxCacheHours { get; set; } = 6;

    /// <summary>
    /// Codex 自动禁用阈值（百分比，1-100）。
    /// 当任一关键额度窗口的已使用百分比达到该阈值时，账号自动禁用。
    /// 这是全局配置，对所有 Codex 账号统一生效。
    /// </summary>
    public int CodexAutoDisableThresholdPercent { get; set; } = 95;
}
