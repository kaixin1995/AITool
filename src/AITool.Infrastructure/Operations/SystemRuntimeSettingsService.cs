using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Domain.Codex;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Operations;

/// <summary>
/// 系统运行时设置服务实现，负责默认值初始化、配置更新与日志清理。
/// 当前阶段额外提供 Core 配置快照构建能力，作为后续 Admin → Core 同步骨架。
/// </summary>
public sealed class SystemRuntimeSettingsService : ISystemRuntimeSettingsService
{
    /// <summary>
    /// 数据库上下文，用于读写系统运行时配置。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 注入数据库上下文。
    /// </summary>
    public SystemRuntimeSettingsService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取系统运行时配置，不存在时自动创建默认值并持久化。
    /// </summary>
    public async Task<Domain.Operations.SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new Domain.Operations.SystemRuntimeSettings();
        await _dbContext.InsertAsync(settings, cancellationToken);
        return settings;
    }

    /// <summary>
    /// 更新系统运行时配置，对各字段做边界保护后持久化。
    /// </summary>
    public async Task<Domain.Operations.SystemRuntimeSettings> UpdateAsync(UpdateSystemRuntimeSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateAsync(cancellationToken);

        // 对运行时设置做最小边界保护，避免写入无效值。
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

        // Codex 设置：边界保护 + 总开关联动禁用
        var wasCodexEnabled = settings.CodexFeaturesEnabled;
        settings.CodexFeaturesEnabled = request.CodexFeaturesEnabled;
        settings.CodexInspectionEnabled = request.CodexInspectionEnabled;
        settings.CodexInspectionIntervalMinutes = Math.Max(1, request.CodexInspectionIntervalMinutes);
        settings.CodexQuotaMaxCacheHours = Math.Max(1, request.CodexQuotaMaxCacheHours);
        settings.CodexAutoDisableThresholdPercent = Math.Max(1, Math.Min(100, request.CodexAutoDisableThresholdPercent));

        await _dbContext.UpdateAsync(settings, cancellationToken);

        // 总开关 true→false：禁用所有 Codex 托管 Site + 标记账号为「被总开关禁用」
        // 总开关 false→true：仅恢复「被总开关禁用」的账号（避免误启用冷却中/手动禁用的账号）
        if (wasCodexEnabled && !settings.CodexFeaturesEnabled)
        {
            await ApplyCodexFeatureToggleOffAsync(cancellationToken);
        }
        else if (!wasCodexEnabled && settings.CodexFeaturesEnabled)
        {
            await ApplyCodexFeatureToggleOnAsync(cancellationToken);
        }

        return settings;
    }

    /// <summary>
    /// 从当前数据库主数据构建一份完整的 Core 运行时配置快照。
    /// 这份快照后续将作为 Admin → Core 全量同步的载体。
    /// </summary>
    public async Task<CoreRuntimeConfigSnapshot> BuildCoreRuntimeConfigSnapshotAsync(long configVersion, CancellationToken cancellationToken = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var sites = await _dbContext.Sites.ToListAsync(cancellationToken);
        var models = await _dbContext.ModelLibraryItems.ToListAsync(cancellationToken);
        var siteModelMappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
        var routeEntries = await _dbContext.ProxyRouteEntries.ToListAsync(cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var accessKeys = await _dbContext.ProxyAccessKeys.ToListAsync(cancellationToken);
        var compatibilityProfiles = await _dbContext.CompatibilityProfiles.ToListAsync(cancellationToken);
        var runtimeSettings = await GetOrCreateAsync(cancellationToken);

        return CoreRuntimeConfigSnapshotBuilder.Build(
            sites,
            models,
            siteModelMappings,
            routeEntries,
            routeRules,
            accessKeys,
            runtimeSettings,
            configVersion,
            generatedAt,
            compatibilityProfiles);
    }

    /// <summary>
    /// 按来源和时间范围清理使用日志，并回写本次清理结果。
    /// SqlSugar 能将 DateTimeOffset 区间比较下推到 SQLite，因此无需全表加载到内存。
    /// </summary>
    public async Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateAsync(cancellationToken);

        // 用条件查询先统计待删除数量：WhereIF 仅在条件成立时追加谓词，
        // 避免闭包变量（如 source==null）被 SqlSugar 翻译成错误的 IS 子句。
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

        // 坑#3：SqlSugar 的 Deleteable.Where(复杂表达式) 在 SQLite 下会生成错误 SQL，
        // 改为先查出待删除的 Id，再用 In 删除，确保删除真正执行。
        var idsToDelete = await query.Select(x => x.Id).ToListAsync(cancellationToken);
        if (idsToDelete.Count > 0)
        {
            await _dbContext.Client.Deleteable<Domain.Proxy.ProxyUsageLog>()
                .In(idsToDelete)
                .ExecuteCommandAsync(cancellationToken);
        }

        settings.LastUsageLogPrunedAt = DateTimeOffset.UtcNow;
        settings.LastUsageLogPrunedCount = deletedCount;
        await _dbContext.UpdateAsync(settings, cancellationToken);
        return deletedCount;
    }

    /// <summary>
    /// 总开关关闭：把所有 Codex 托管 Site 置为禁用，并把对应 CodexAccount 标记为「被总开关禁用」
    /// （记录原启用状态，便于重新开启时仅恢复这些账号）。
    /// </summary>
    private async Task ApplyCodexFeatureToggleOffAsync(CancellationToken cancellationToken)
    {
        var codexSites = await _dbContext.Sites
            .Where(s => s.ManagedSource == "Codex")
            .ToListAsync(cancellationToken);
        if (codexSites.Count == 0) return;

        var siteIds = codexSites.Select(s => s.Id).ToList();
        var accounts = await _dbContext.CodexAccounts
            .Where(a => siteIds.Contains(a.LinkedSiteId))
            .ToListAsync(cancellationToken);

        foreach (var site in codexSites)
        {
            if (site.IsEnabled)
            {
                site.IsEnabled = false;
                await _dbContext.UpdateAsync(site, cancellationToken);
            }
        }

        foreach (var account in accounts)
        {
            // 记录原启用状态后禁用，便于重新开启时精准恢复
            account.DisabledByFeatureToggle = account.IsEnabled;
            if (account.IsEnabled)
            {
                account.IsEnabled = false;
                await _dbContext.UpdateAsync(account, cancellationToken);
            }
            else
            {
                // 原本就禁用的账号，DisabledByFeatureToggle 仍记录为 false，重开时不恢复
                await _dbContext.UpdateAsync(account, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 总开关重新开启：仅恢复因总开关被禁用的账号（DisabledByFeatureToggle==true），避免误启用冷却中/手动禁用的账号。
    /// </summary>
    private async Task ApplyCodexFeatureToggleOnAsync(CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.CodexAccounts
            .Where(a => a.DisabledByFeatureToggle)
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0) return;

        var siteIds = accounts.Select(a => a.LinkedSiteId).ToList();
        var sites = await _dbContext.Sites
            .Where(s => siteIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            account.IsEnabled = true;
            account.DisabledByFeatureToggle = false;
            await _dbContext.UpdateAsync(account, cancellationToken);
        }

        foreach (var site in sites)
        {
            if (!site.IsEnabled)
            {
                site.IsEnabled = true;
                await _dbContext.UpdateAsync(site, cancellationToken);
            }
        }
    }
}
