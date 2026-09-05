using System.Linq.Expressions;
using AITool.Application.Accounts;
using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Operations;

/// <summary>
/// 系统运行时设置服务实现，负责默认值初始化、配置更新与日志清理。
/// split 双宿主：额外提供 Core 配置快照构建能力（Admin 是配置唯一权威源）。
/// </summary>
public sealed class SystemRuntimeSettingsService : ISystemRuntimeSettingsService
{
    private const int UsageLogDeleteBatchSize = 500;

    /// <summary>
    /// 数据库上下文，用于读写系统运行时配置
    /// </summary>
    private readonly AppDbContext _dbContext;
    private readonly IReadOnlyList<IAccountQuotaProvider> _quotaProviders;

    /// <summary>
    /// 注入数据库上下文
    /// </summary>
    /// <summary>请求头模板目录（可选）：构建 Core 快照时下发启用的 HeaderProfile 模板。</summary>
    private readonly AITool.Application.Proxy.IHeaderProfileCatalogService? _headerProfileCatalog;

    public SystemRuntimeSettingsService(
        AppDbContext dbContext,
        IEnumerable<IAccountQuotaProvider>? quotaProviders = null,
        AITool.Application.Proxy.IHeaderProfileCatalogService? headerProfileCatalog = null)
    {
        _dbContext = dbContext;
        _quotaProviders = quotaProviders?.ToList() ?? [];
        _headerProfileCatalog = headerProfileCatalog;
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
        // 空闲超时允许 0（不启用），并做宽松上限钳制避免误填异常值。
        settings.ProxyStreamIdleTimeoutSeconds = Math.Max(0, Math.Min(86400, request.ProxyStreamIdleTimeoutSeconds));
        settings.ProxyRetryCount = Math.Max(0, request.ProxyRetryCount);
        // 429 连续重试次数：0-10 合理区间，负数归零。
        settings.RateLimitRetryCount = Math.Max(0, Math.Min(10, request.RateLimitRetryCount));
        settings.DetectionRequestTimeoutSeconds = Math.Max(1, request.DetectionRequestTimeoutSeconds);
        settings.DetectionRetryCount = Math.Max(0, request.DetectionRetryCount);
        settings.DetectionConcurrency = Math.Max(1, request.DetectionConcurrency);
        settings.CircuitBreakerFailureThreshold = Math.Max(1, request.CircuitBreakerFailureThreshold);
        settings.CircuitBreakerRecoveryMinutes = Math.Max(1, request.CircuitBreakerRecoveryMinutes);
        settings.UsageLogRetentionDays = Math.Max(1, request.UsageLogRetentionDays);
        settings.UsageLogAutoCleanupEnabled = request.UsageLogAutoCleanupEnabled;
        settings.DeveloperFeaturesEnabled = request.DeveloperFeaturesEnabled;
        // 分页开关用可空语义：请求缺失（旧客户端部分回写，如桌面端模型未含新字段）时保持现值，
        // 避免保存动作意外把用户已关闭的功能重置为默认开。
        settings.DeveloperTraceEnabled = request.DeveloperTraceEnabled ?? settings.DeveloperTraceEnabled;
        settings.DeveloperFailureDumpEnabled = request.DeveloperFailureDumpEnabled ?? settings.DeveloperFailureDumpEnabled;
        settings.DeveloperSimulatorEnabled = request.DeveloperSimulatorEnabled ?? settings.DeveloperSimulatorEnabled;
        settings.DeveloperProtocolDiagnosticsEnabled = request.DeveloperProtocolDiagnosticsEnabled ?? settings.DeveloperProtocolDiagnosticsEnabled;
        settings.DeveloperSqlMigrationsEnabled = request.DeveloperSqlMigrationsEnabled ?? settings.DeveloperSqlMigrationsEnabled;
        settings.DeveloperProxyProfilesEnabled = request.DeveloperProxyProfilesEnabled ?? settings.DeveloperProxyProfilesEnabled;
        settings.ConversationLogEnabled = request.ConversationLogEnabled;
        settings.ConcurrencyMode = Math.Max(0, Math.Min(1, request.ConcurrencyMode));
        settings.ConcurrencyQueueTimeoutSeconds = Math.Max(1, request.ConcurrencyQueueTimeoutSeconds);

        // OAuth 账号设置：边界保护 + 总开关联动禁用
        var wasOAuthEnabled = settings.OAuthFeaturesEnabled;
        settings.OAuthFeaturesEnabled = request.OAuthFeaturesEnabled;
        settings.OAuthInspectionEnabled = request.OAuthInspectionEnabled;
        settings.OAuthInspectionIntervalSeconds = Math.Max(30, request.OAuthInspectionIntervalSeconds);
        settings.OAuthQuotaMaxCacheHours = Math.Max(1, request.OAuthQuotaMaxCacheHours);
        settings.OAuthAutoDisableThresholdPercent = Math.Max(1, Math.Min(100, request.OAuthAutoDisableThresholdPercent));
        settings.OAuthInspectionCacheEnabled = request.OAuthInspectionCacheEnabled;

        await _dbContext.UpdateAsync(settings, cancellationToken);

        // 总开关 true→false：交给每个额度提供程序禁用自己的账号和关联站点。
        // 总开关 false→true：仅恢复各提供程序标记为「被总开关禁用」的账号。
        if (wasOAuthEnabled && !settings.OAuthFeaturesEnabled)
        {
            await ApplyQuotaProviderFeatureToggleAsync(false, cancellationToken);
        }
        else if (!wasOAuthEnabled && settings.OAuthFeaturesEnabled)
        {
            await ApplyQuotaProviderFeatureToggleAsync(true, cancellationToken);
        }

        return settings;
    }

    /// <summary>
    /// 从当前数据库主数据构建一份完整的 Core 运行时配置快照。
    /// 这份快照作为 Admin → Core 全量同步的载体（split 双宿主）。
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
        var siteKeys = await _dbContext.SiteKeys.ToListAsync(cancellationToken);
        // 托管 OAuth 账号凭证（Core 401 即刷所需）：三家账号表全量读取后由 Builder 过滤投影。
        var codexAccounts = await _dbContext.CodexAccounts.ToListAsync(cancellationToken);
        var googleAccounts = await _dbContext.GoogleAccounts.ToListAsync(cancellationToken);
        var kimiAccounts = await _dbContext.KimiAccounts.ToListAsync(cancellationToken);
        // 客户端特征模拟档案：出口代理池（表）+ 请求头模板（JSON 目录，经目录服务读取）。
        var proxyProfiles = await _dbContext.ProxyProfiles.ToListAsync(cancellationToken);
        var activeHeaderProfiles = _headerProfileCatalog is not null
            ? await _headerProfileCatalog.GetActiveProfilesDictionaryAsync(cancellationToken)
            : null;
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
            compatibilityProfiles,
            siteKeys,
            codexAccounts,
            googleAccounts,
            kimiAccounts,
            proxyProfiles,
            activeHeaderProfiles);
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
        // 改为分批查出 Id，再用 In 删除，确保删除真正执行，同时避免一次性加载全部日志 Id。
        while (true)
        {
            var idsToDelete = await query
                .OrderBy(x => x.RequestedAt)
                .Take(UsageLogDeleteBatchSize)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            if (idsToDelete.Count == 0)
            {
                break;
            }

            await _dbContext.Client.Deleteable<ProxyUsageLog>()
                .In(idsToDelete)
                .ExecuteCommandAsync(cancellationToken);
        }

        settings.LastUsageLogPrunedAt = DateTimeOffset.UtcNow;
        settings.LastUsageLogPrunedCount = deletedCount;
        await _dbContext.UpdateAsync(settings, cancellationToken);
        return deletedCount;
    }

    private async Task ApplyQuotaProviderFeatureToggleAsync(bool enabled, CancellationToken cancellationToken)
    {
        foreach (var provider in _quotaProviders)
        {
            await provider.ApplyFeatureToggleAsync(enabled, cancellationToken);
        }
    }
}
