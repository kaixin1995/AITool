namespace AITool.Application.Common;

// 日志保留策略服务接口，清理过期的检测日志和使用日志
public interface ILogRetentionService
{
    // 清理超过保留天数的日志记录，并返回本次清理结果
    Task<LogPruneResult> PruneAsync(CancellationToken cancellationToken = default);
}

// 日志清理结果
public sealed class LogPruneResult
{
    // 本次清理的使用日志数量
    public int UsageLogPrunedCount { get; set; }

    // 本次清理的检测日志数量
    public int DetectionLogPrunedCount { get; set; }
}
