namespace AITool.Application.Common;

/// <summary>
/// 日志保留策略服务接口，用于按系统设定定期清理过期的使用日志。
/// </summary>
public interface ILogRetentionService
{
    /// <summary>
    /// 执行一次日志清理，并返回本次实际删除的数据量。
    /// </summary>
    Task<LogPruneResult> PruneAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 日志清理结果，用于汇总一次清理操作的执行效果。
/// </summary>
public sealed class LogPruneResult
{
    /// <summary>
    /// 记录本次被成功清理掉的使用日志条数。
    /// </summary>
    public int UsageLogPrunedCount { get; set; }
}
