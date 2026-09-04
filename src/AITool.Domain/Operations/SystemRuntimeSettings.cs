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
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public int Id { get; set; } = 1;

    /// <summary>
    /// 代理请求超时时间，单位为秒，用于限制单次代理转发请求的最长等待时长。
    /// </summary>
    public int ProxyRequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 流式转发空闲超时（秒）：相邻两次读到上游数据的最大间隔，超过即判定上游挂起并终止本次转发。
    /// 0 表示不启用（默认）——推理模型首 token 前可能长时间静默，误配会中断合法慢流。
    /// </summary>
    public int ProxyStreamIdleTimeoutSeconds { get; set; }

    /// <summary>
    /// 代理请求失败后的最大重试次数，用于控制路由重试或重新转发的上限。
    /// </summary>
    public int ProxyRetryCount { get; set; } = 1;

    /// <summary>
    /// 上游返回 429（速率限制）时的连续重试次数，默认 0（一次 429 即失败并回退下一路由）。
    /// 设为 N&gt;0 时：同一上游连续 N 次 429 才判定该路由失败；期间任一次成功即算成功。
    /// </summary>
    public int RateLimitRetryCount { get; set; }

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
    /// 调用追踪开关（开发者总闸开启时生效）。关闭后代理请求不再采集追踪数据（省去每请求的
    /// 报文复制分配），调用追踪相关 API 返回 404。
    /// </summary>
    public bool DeveloperTraceEnabled { get; set; } = true;

    /// <summary>
    /// 诊断抓包开关（开发者总闸开启时生效）。关闭后失败请求不再自动落盘 dump，
    /// 对比采样与抓包管理 API 返回 404。
    /// </summary>
    public bool DeveloperFailureDumpEnabled { get; set; } = true;

    /// <summary>
    /// 请求模拟器页开关（纯按需功能，无后台开销；关闭仅隐藏入口并禁用其 API）。
    /// </summary>
    public bool DeveloperSimulatorEnabled { get; set; } = true;

    /// <summary>
    /// 协议诊断与 AI 自愈页开关（按需功能；关闭隐藏入口并禁用相关 API）。
    /// </summary>
    public bool DeveloperProtocolDiagnosticsEnabled { get; set; } = true;

    /// <summary>
    /// SQL 迁移页开关（按需功能；关闭隐藏入口并禁用脚本执行 API）。
    /// </summary>
    public bool DeveloperSqlMigrationsEnabled { get; set; } = true;

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
    /// OAuth 账号功能总开关（含 OAuth 登录账号、凭证导入和额度巡检）。
    /// 关闭后隐藏 OAuth 页面入口，并把所有托管账号站点置为禁用（路由/模型/对话测试不再命中）。
    /// </summary>
    // 保留旧数据库列名，避免 CodeFirst 只增不改时丢失现有配置。
    [SugarColumn(ColumnName = "CodexFeaturesEnabled")]
    public bool OAuthFeaturesEnabled { get; set; }

    /// <summary>
    /// OAuth 账号额度巡检自动执行开关。仅在 OAuthFeaturesEnabled 开启时生效。
    /// </summary>
    [SugarColumn(ColumnName = "CodexInspectionEnabled")]
    public bool OAuthInspectionEnabled { get; set; }

    /// <summary>
    /// OAuth 账号额度巡检周期（秒），下限 30。每隔该周期执行一轮账号额度巡检。
    /// </summary>
    [SugarColumn(ColumnName = "CodexInspectionIntervalSeconds")]
    public int OAuthInspectionIntervalSeconds { get; set; } = 1800;

    /// <summary>
    /// OAuth 账号额度缓存最大小时数。超过该时长未真实刷新的账号，巡检时强制真实刷新。
    /// </summary>
    [SugarColumn(ColumnName = "CodexQuotaMaxCacheHours")]
    public int OAuthQuotaMaxCacheHours { get; set; } = 6;

    /// <summary>
    /// OAuth 账号自动禁用阈值（百分比，1-100）。
    /// 当任一关键额度窗口的已使用百分比达到该阈值时，账号自动禁用。
    /// 这是全局配置，对所有 OAuth 账号统一生效。
    /// </summary>
    [SugarColumn(ColumnName = "CodexAutoDisableThresholdPercent")]
    public int OAuthAutoDisableThresholdPercent { get; set; } = 95;

    /// <summary>
    /// OAuth 账号额度巡检缓存复用开关。仅在 OAuthFeaturesEnabled 开启时生效。
    /// 关闭（默认）：每轮巡检都对所有账号真实刷新额度，无论是否被调用。
    /// 开启：未被使用且窗口未过期且未超过 OAuthQuotaMaxCacheHours 的账号沿用上次额度快照（减少上游请求）。
    /// </summary>
    [SugarColumn(ColumnName = "CodexInspectionCacheEnabled")]
    public bool OAuthInspectionCacheEnabled { get; set; }
}
