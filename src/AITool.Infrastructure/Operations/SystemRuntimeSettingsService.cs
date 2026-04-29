using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Operations;

// 系统运行时设置服务实现，负责默认值初始化与配置更新
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
        settings.UsageLogRetentionDays = Math.Max(1, request.UsageLogRetentionDays);
        settings.DetectionLogRetentionDays = Math.Max(1, request.DetectionLogRetentionDays);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    // 兼容旧 SQLite 库缺少 SystemRuntimeSettings 表的情况
    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
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

            command.CommandText = "CREATE TABLE SystemRuntimeSettings (Id INTEGER NOT NULL CONSTRAINT PK_SystemRuntimeSettings PRIMARY KEY, ProxyRequestTimeoutSeconds INTEGER NOT NULL DEFAULT 60, ProxyRetryCount INTEGER NOT NULL DEFAULT 1, UsageLogRetentionDays INTEGER NOT NULL DEFAULT 7, DetectionLogRetentionDays INTEGER NOT NULL DEFAULT 7, LastUsageLogPrunedAt TEXT NULL, LastUsageLogPrunedCount INTEGER NOT NULL DEFAULT 0, LastDetectionLogPrunedAt TEXT NULL, LastDetectionLogPrunedCount INTEGER NOT NULL DEFAULT 0)";
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
