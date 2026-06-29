using AITool.Application.Common;
using AITool.Application.Conversations;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.Persistence;

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
    /// 删除超过保留天数的使用日志，并回写本次清理结果。
    /// SqlSugar 能将 DateTimeOffset 比较下推到 SQLite，无需像 EF 那样全表加载到内存。
    /// </summary>
    public async Task<LogPruneResult> PruneAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
        {
            settings = new SystemRuntimeSettings { Id = 1 };
            await _dbContext.InsertAsync(settings, cancellationToken);
        }

        var now = _utcNowProvider();

        // 对话记录已迁移到本地 JSONL 文件，DB 表不再写入，无需清理。
        // 本地文件的过期清理由下一行 PruneExpiredAsync 负责。
        await _conversationLogStore.PruneExpiredAsync(cancellationToken);

        if (!settings.UsageLogAutoCleanupEnabled)
        {
            settings.LastUsageLogPrunedAt = now;
            settings.LastUsageLogPrunedCount = 0;
            await _dbContext.UpdateAsync(settings, cancellationToken);
            return new LogPruneResult { UsageLogPrunedCount = 0 };
        }

        var usageCutoff = now.AddDays(-settings.UsageLogRetentionDays);

        // SqlSugar 直接在数据库层删除过期日志（DateTimeOffset 比较可下推），避免全表加载到内存。
        var prunedCount = await _dbContext.DeleteAsync<ProxyUsageLog>(
            l => l.RequestedAt < usageCutoff, cancellationToken);

        settings.LastUsageLogPrunedAt = now;
        settings.LastUsageLogPrunedCount = prunedCount;
        await _dbContext.UpdateAsync(settings, cancellationToken);

        return new LogPruneResult { UsageLogPrunedCount = prunedCount };
    }
}
