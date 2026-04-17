using AITool.Application.Common;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Retention;

// 日志保留策略服务实现，清理超过 7 天的检测日志和使用日志
public sealed class LogRetentionService : ILogRetentionService
{
    private readonly AppDbContext _dbContext;

    public LogRetentionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 删除超过 7 天的检测日志和使用日志
    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);

        // 清理过期的检测日志
        var oldDetectionLogs = await _dbContext.DetectionLogs
            .Where(l => l.CheckedAt < cutoff)
            .ToListAsync(cancellationToken);
        _dbContext.DetectionLogs.RemoveRange(oldDetectionLogs);

        // 清理过期的使用日志
        var oldUsageLogs = await _dbContext.ProxyUsageLogs
            .Where(l => l.RequestedAt < cutoff)
            .ToListAsync(cancellationToken);
        _dbContext.ProxyUsageLogs.RemoveRange(oldUsageLogs);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}