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
    /// 删除超过保留天数的使用日志和对话记录，并回写本次清理结果。
    /// <para>
    /// 优化策略：先查出满足条件记录的 Id 列表，再按 Id 定位并删除，
    /// 避免将整行数据（含 UserInputText、AssistantOutputMarkdown 等大文本字段）全部加载到内存。
    /// <para>
    /// 注意：SQLite EF Core 不支持 DateTimeOffset 表达式翻译，因此 ExecuteDeleteAsync 在此不可用
    /// （其 Where 子句同样无法翻译 RequestedAt/CreatedAt 比较），保留基于 Id 的 RemoveRange 路径。
    /// </para>
    /// </para>
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
        var conversationCutoff = now.AddDays(-ConversationLogStoragePolicy.RetentionDays);

        // 先查出过期对话记录的 Id，再按 Id 加载实体后删除。
        // SQLite 不支持 DateTimeOffset 翻译，时间过滤在客户端完成（先全表加载 Id 再内存过滤开销可控）。
        var oldConversationIds = (await _dbContext.ConversationTurnLogs
                .Select(l => new { l.Id, l.CreatedAt })
                .ToListAsync(cancellationToken))
            .Where(l => l.CreatedAt < conversationCutoff)
            .Select(l => l.Id)
            .ToList();

        if (oldConversationIds.Count > 0)
        {
            var oldConversationLogs = await _dbContext.ConversationTurnLogs
                .Where(l => oldConversationIds.Contains(l.Id))
                .ToListAsync(cancellationToken);
            _dbContext.ConversationTurnLogs.RemoveRange(oldConversationLogs);
        }

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

        // 同理，先查 Id 再按 Id 定位删除，避免加载整张 ProxyUsageLogs 表的大文本字段。
        var oldUsageIds = (await _dbContext.ProxyUsageLogs
                .Select(l => new { l.Id, l.RequestedAt })
                .ToListAsync(cancellationToken))
            .Where(l => l.RequestedAt < usageCutoff)
            .Select(l => l.Id)
            .ToList();

        var deletedUsageCount = 0;
        if (oldUsageIds.Count > 0)
        {
            var oldUsageLogs = await _dbContext.ProxyUsageLogs
                .Where(l => oldUsageIds.Contains(l.Id))
                .ToListAsync(cancellationToken);
            deletedUsageCount = oldUsageLogs.Count;
            _dbContext.ProxyUsageLogs.RemoveRange(oldUsageLogs);
        }

        settings.LastUsageLogPrunedAt = now;
        settings.LastUsageLogPrunedCount = deletedUsageCount;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LogPruneResult
        {
            UsageLogPrunedCount = deletedUsageCount
        };
    }
}
