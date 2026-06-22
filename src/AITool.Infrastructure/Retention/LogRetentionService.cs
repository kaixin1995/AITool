using AITool.Application.Common;
using AITool.Application.Conversations;
using AITool.Domain.Operations;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Retention;

/// <summary>
/// 日志保留策略服务实现，按运行时配置清理使用日志并回写结果
/// </summary>
public sealed class LogRetentionService : ILogRetentionService
{
    /// <summary>
    /// 数据库上下文，用于查询和删除过期使用日志
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 对话记录本地存储，用于清理本地保留窗口之外的历史数据。
    /// </summary>
    private readonly IConversationLogStore _conversationLogStore;
    /// <summary>
    /// 当前 UTC 时间提供器，测试时可替换为固定时间
    /// </summary>
    private readonly Func<DateTimeOffset> _utcNowProvider;

    /// <summary>
    /// 注入数据库上下文，使用系统当前 UTC 时间
    /// </summary>
    public LogRetentionService(AppDbContext dbContext, IConversationLogStore conversationLogStore)
        : this(dbContext, conversationLogStore, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// 为测试提供固定时间入口，避免边界场景受当前时间漂移影响
    /// </summary>
    public LogRetentionService(AppDbContext dbContext, IConversationLogStore conversationLogStore, Func<DateTimeOffset> utcNowProvider)
    {
        _dbContext = dbContext;
        _conversationLogStore = conversationLogStore;
        _utcNowProvider = utcNowProvider;
    }

    /// <summary>
    /// 删除超过保留天数的使用日志，并回写本次清理结果
    /// </summary>
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

        // 对话记录已迁移到本地 JSONL 文件，DB 表不再写入，无需清理。
        // 本地文件的过期清理由下一行 PruneExpiredAsync 负责。
        await _conversationLogStore.PruneExpiredAsync(cancellationToken);

        if (!settings.UsageLogAutoCleanupEnabled)
        {
            settings.LastUsageLogPrunedAt = now;
            settings.LastUsageLogPrunedCount = 0;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new LogPruneResult
            {
                UsageLogPrunedCount = 0
            };
        }

        var usageCutoff = now.AddDays(-settings.UsageLogRetentionDays);

        // 先加载到内存再按时间过滤，避免 SQLite 无法翻译 DateTimeOffset 比较
        var allUsageLogs = await _dbContext.ProxyUsageLogs.ToListAsync(cancellationToken);
        var oldUsageLogs = allUsageLogs
            .Where(l => l.RequestedAt < usageCutoff)
            .ToList();
        _dbContext.ProxyUsageLogs.RemoveRange(oldUsageLogs);

        var prunedAt = now;
        settings.LastUsageLogPrunedAt = prunedAt;
        settings.LastUsageLogPrunedCount = oldUsageLogs.Count;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LogPruneResult
        {
            UsageLogPrunedCount = oldUsageLogs.Count
        };
    }
}
