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
}
