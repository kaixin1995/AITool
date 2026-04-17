namespace AITool.Application.Common;

// 日志保留策略服务接口，清理过期的检测日志和使用日志
public interface ILogRetentionService
{
    // 清理超过保留天数的日志记录
    Task PruneAsync(CancellationToken cancellationToken = default);
}