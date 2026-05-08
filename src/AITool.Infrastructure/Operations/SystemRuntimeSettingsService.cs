using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Operations;

// 系统运行时设置服务实现，负责默认值初始化、配置更新与日志清理
public sealed class SystemRuntimeSettingsService : ISystemRuntimeSettingsService
{
    private readonly AppDbContext _dbContext;

    public SystemRuntimeSettingsService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken);

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

        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

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

    // 系统设置页与测试仍可能在缺表场景下调用该服务，因此保留兼容逻辑。
    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
        {
            return;
        }

        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SystemRuntimeSettings'";
            var exists = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            if (exists)
            {
                return;
            }

            command.CommandText = "CREATE TABLE SystemRuntimeSettings (Id INTEGER NOT NULL CONSTRAINT PK_SystemRuntimeSettings PRIMARY KEY, ProxyRequestTimeoutSeconds INTEGER NOT NULL DEFAULT 60, ProxyRetryCount INTEGER NOT NULL DEFAULT 1, DetectionRequestTimeoutSeconds INTEGER NOT NULL DEFAULT 60, DetectionRetryCount INTEGER NOT NULL DEFAULT 0, DetectionConcurrency INTEGER NOT NULL DEFAULT 1, CircuitBreakerFailureThreshold INTEGER NOT NULL DEFAULT 5, CircuitBreakerRecoveryMinutes INTEGER NOT NULL DEFAULT 2, UsageLogRetentionDays INTEGER NOT NULL DEFAULT 7, UsageLogAutoCleanupEnabled INTEGER NOT NULL DEFAULT 1, LastUsageLogPrunedAt TEXT NULL, LastUsageLogPrunedCount INTEGER NOT NULL DEFAULT 0)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
