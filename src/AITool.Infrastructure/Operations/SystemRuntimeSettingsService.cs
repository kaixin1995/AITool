using System.Linq.Expressions;
using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Operations;

/// <summary>
/// 系统运行时设置服务实现，负责默认值初始化、配置更新与日志清理
/// </summary>
public sealed class SystemRuntimeSettingsService : ISystemRuntimeSettingsService
{
    /// <summary>
    /// 数据库上下文，用于读写系统运行时配置
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 注入数据库上下文
    /// </summary>
    public SystemRuntimeSettingsService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取系统运行时配置，不存在时自动创建默认值并持久化
    /// </summary>
    public async Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new SystemRuntimeSettings();
        await _dbContext.InsertAsync(settings, cancellationToken);
        return settings;
    }

    /// <summary>
    /// 更新系统运行时配置，对各字段做边界保护后持久化
    /// </summary>
    public async Task<SystemRuntimeSettings> UpdateAsync(UpdateSystemRuntimeSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateAsync(cancellationToken);

        // 对运行时设置做最小边界保护，避免写入无效值
        settings.ProxyRequestTimeoutSeconds = Math.Max(1, request.ProxyRequestTimeoutSeconds);
        settings.ProxyRetryCount = Math.Max(0, request.ProxyRetryCount);
        settings.DetectionRequestTimeoutSeconds = Math.Max(1, request.DetectionRequestTimeoutSeconds);
        settings.DetectionRetryCount = Math.Max(0, request.DetectionRetryCount);
        settings.DetectionConcurrency = Math.Max(1, request.DetectionConcurrency);
        settings.CircuitBreakerFailureThreshold = Math.Max(1, request.CircuitBreakerFailureThreshold);
        settings.CircuitBreakerRecoveryMinutes = Math.Max(1, request.CircuitBreakerRecoveryMinutes);
        settings.UsageLogRetentionDays = Math.Max(1, request.UsageLogRetentionDays);
        settings.UsageLogAutoCleanupEnabled = request.UsageLogAutoCleanupEnabled;
        settings.DeveloperFeaturesEnabled = request.DeveloperFeaturesEnabled;
        settings.ConversationLogEnabled = request.ConversationLogEnabled;
        settings.ConcurrencyMode = Math.Max(0, Math.Min(1, request.ConcurrencyMode));
        settings.ConcurrencyQueueTimeoutSeconds = Math.Max(1, request.ConcurrencyQueueTimeoutSeconds);

        await _dbContext.UpdateAsync(settings, cancellationToken);
        return settings;
    }

    /// <summary>
    /// 按来源和时间范围清理使用日志，并回写本次清理结果。
    /// SqlSugar 能将 DateTimeOffset 区间比较下推到 SQLite，无需像 EF 那样全表加载到内存。
    /// </summary>
    public async Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateAsync(cancellationToken);

        // 用条件查询先统计待删除数量，再按同样条件删除。
        var query = _dbContext.ProxyUsageLogs
            .WhereIF(!string.IsNullOrWhiteSpace(request.Source), x => x.Source == request.Source);
        if (request.StartTime.HasValue)
        {
            query = query.Where(x => x.RequestedAt >= request.StartTime.Value);
        }
        if (request.EndTime.HasValue)
        {
            query = query.Where(x => x.RequestedAt < request.EndTime.Value);
        }

        var deletedCount = await query.CountAsync(cancellationToken);

        // SqlSugar 的 Deleteable.Where(复杂表达式) 在 SQLite 下可能静默不生成 DELETE，
        // 改为先查出待删除的 Id，再用 In 删除，确保删除真正执行。
        var idsToDelete = await query.Select(x => x.Id).ToListAsync(cancellationToken);
        if (idsToDelete.Count > 0)
        {
            await _dbContext.Client.Deleteable<ProxyUsageLog>()
                .In(idsToDelete)
                .ExecuteCommandAsync(cancellationToken);
        }

        settings.LastUsageLogPrunedAt = DateTimeOffset.UtcNow;
        settings.LastUsageLogPrunedCount = deletedCount;
        await _dbContext.UpdateAsync(settings, cancellationToken);
        return deletedCount;
    }
}
