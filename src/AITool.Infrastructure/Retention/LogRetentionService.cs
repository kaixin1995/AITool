using AITool.Application.Common;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Retention;

// 日志保留策略服务实现，按运行时配置清理检测日志和使用日志
public sealed class LogRetentionService : ILogRetentionService
{
    private readonly AppDbContext _dbContext;
    private readonly Func<DateTimeOffset> _utcNowProvider;

    public LogRetentionService(AppDbContext dbContext)
        : this(dbContext, () => DateTimeOffset.UtcNow)
    {
    }

    // 为测试提供固定时间入口，避免边界场景受当前时间漂移影响
    public LogRetentionService(AppDbContext dbContext, Func<DateTimeOffset> utcNowProvider)
    {
        _dbContext = dbContext;
        _utcNowProvider = utcNowProvider;
    }

    // 删除超过保留天数的检测日志和使用日志，并回写本次清理结果
    public async Task<LogPruneResult> PruneAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.SystemRuntimeSettings
            .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
        {
            settings = new SystemRuntimeSettings
            {
                Id = 1
            };
            _dbContext.SystemRuntimeSettings.Add(settings);
        }

        var now = _utcNowProvider();
        var usageCutoff = now.AddDays(-settings.UsageLogRetentionDays);
        var detectionCutoff = now.AddDays(-settings.DetectionLogRetentionDays);

        // 按使用日志保留天数清理过期的代理使用日志
        var oldUsageLogs = await _dbContext.ProxyUsageLogs
            .Where(l => l.RequestedAt < usageCutoff)
            .ToListAsync(cancellationToken);
        _dbContext.ProxyUsageLogs.RemoveRange(oldUsageLogs);

        // 按检测日志保留天数清理过期的检测日志
        var oldDetectionLogs = await _dbContext.DetectionLogs
            .Where(l => l.CheckedAt < detectionCutoff)
            .ToListAsync(cancellationToken);
        _dbContext.DetectionLogs.RemoveRange(oldDetectionLogs);

        var prunedAt = now;
        settings.LastUsageLogPrunedAt = prunedAt;
        settings.LastUsageLogPrunedCount = oldUsageLogs.Count;
        settings.LastDetectionLogPrunedAt = prunedAt;
        settings.LastDetectionLogPrunedCount = oldDetectionLogs.Count;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LogPruneResult
        {
            UsageLogPrunedCount = oldUsageLogs.Count,
            DetectionLogPrunedCount = oldDetectionLogs.Count
        };
    }
}
