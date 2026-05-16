using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
            .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new SystemRuntimeSettings();
        _dbContext.SystemRuntimeSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    /// <summary>
    /// 按来源和时间范围清理使用日志，并回写本次清理结果
    /// </summary>
    public async Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateAsync(cancellationToken);

        // 先加载到内存再按条件过滤，避免 SQLite 无法稳定翻译 DateTimeOffset 区间比较
        var logs = await _dbContext.ProxyUsageLogs.ToListAsync(cancellationToken);
        var logsToDelete = logs
            .Where(x => string.IsNullOrWhiteSpace(request.Source) || string.Equals(x.Source, request.Source, StringComparison.OrdinalIgnoreCase))
            .Where(x => !request.StartTime.HasValue || x.RequestedAt >= request.StartTime.Value)
            .Where(x => !request.EndTime.HasValue || x.RequestedAt < request.EndTime.Value)
            .ToList();

        if (logsToDelete.Count == 0)
        {
            settings.LastUsageLogPrunedAt = DateTimeOffset.UtcNow;
            settings.LastUsageLogPrunedCount = 0;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }

        _dbContext.ProxyUsageLogs.RemoveRange(logsToDelete);
        settings.LastUsageLogPrunedAt = DateTimeOffset.UtcNow;
        settings.LastUsageLogPrunedCount = logsToDelete.Count;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return logsToDelete.Count;
    }
}
