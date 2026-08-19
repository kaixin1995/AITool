using System.Collections.Concurrent;
using AITool.Application.Accounts;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 通用 OAuth 账号额度巡检编排器。
/// <para>
/// 巡检逻辑只关心账号、额度窗口、缓存策略和启停决策；具体 OAuth 提供程序通过
/// <see cref="IAccountQuotaProvider"/> 负责账号枚举、额度查询、原始响应解析和状态同步。
/// 因此新增其它账号类型时无需复制多窗口额度巡检流程。
/// </para>
/// </summary>
public sealed class AccountQuotaInspectionService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private const int MaxLogs = 200;

    private readonly IServiceProvider _services;
    private readonly ILogger<AccountQuotaInspectionService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly SiteUsageTracker _siteUsageTracker;

    private readonly object _stateLock = new();
    private AccountInspectionRunResult? _lastRun;
    private readonly ConcurrentQueue<AccountInspectionLogEntry> _logs = new();
    private long _nextScheduledAtUtcTicks;
    private int _running;

    public AccountQuotaInspectionService(
        IServiceProvider services,
        ILogger<AccountQuotaInspectionService> logger,
        IHostEnvironment environment,
        SiteUsageTracker siteUsageTracker)
    {
        _services = services;
        _logger = logger;
        _environment = environment;
        _siteUsageTracker = siteUsageTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsEnvironment("Testing")) return;

        AddLog("system", "OAuth 账号额度巡检服务已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Account quota inspection loop error");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<AccountInspectionRunResult> RunManualAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return GetLastRun() ?? new AccountInspectionRunResult
            {
                FinishedAt = DateTimeOffset.UtcNow,
            };
        }

        try
        {
            using var scope = _services.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
            var runtime = await cache.GetRuntimeSettingsAsync(cancellationToken);
            var providers = scope.ServiceProvider
                .GetServices<IAccountQuotaProvider>()
                .ToList();

            return await RunInspectionAsync(
                providers,
                cache,
                Math.Max(1, runtime.OAuthQuotaMaxCacheHours),
                runtime.OAuthInspectionCacheEnabled,
                Math.Clamp(runtime.OAuthAutoDisableThresholdPercent, 1, 100),
                forceRefresh,
                autoTriggered: false,
                cancellationToken: cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// 获取最近一次巡检的不可变快照，避免管理接口序列化时与巡检结果列表并发读写。
    /// </summary>
    public AccountInspectionRunResult? GetLastRun()
    {
        lock (_stateLock)
        {
            return _lastRun is null ? null : CloneRunResult(_lastRun);
        }
    }

    public List<AccountInspectionLogEntry> GetLogs() => _logs.Reverse().ToList();

    public object GetStatus()
    {
        DateTimeOffset? lastFinishedAt;
        lock (_stateLock)
        {
            lastFinishedAt = _lastRun?.FinishedAt;
        }

        return new
        {
            isRunning = Volatile.Read(ref _running) != 0,
            nextScheduledAt = ReadNextScheduledAt(),
            lastFinishedAt,
        };
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var runtime = await cache.GetRuntimeSettingsAsync(cancellationToken);

        if (!runtime.OAuthFeaturesEnabled || !runtime.OAuthInspectionEnabled) return;

        var now = DateTimeOffset.UtcNow;
        var nextScheduledAt = ReadNextScheduledAt();
        if (nextScheduledAt is null)
        {
            Interlocked.CompareExchange(
                ref _nextScheduledAtUtcTicks,
                now.UtcDateTime.Ticks,
                0);
            nextScheduledAt = ReadNextScheduledAt();
        }

        if (nextScheduledAt is not null && now < nextScheduledAt.Value) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        try
        {
            Volatile.Write(
                ref _nextScheduledAtUtcTicks,
                (now + TimeSpan.FromSeconds(Math.Max(30, runtime.OAuthInspectionIntervalSeconds))).UtcDateTime.Ticks);
            var providers = scope.ServiceProvider
                .GetServices<IAccountQuotaProvider>()
                .ToList();

            await RunInspectionAsync(
                providers,
                cache,
                Math.Max(1, runtime.OAuthQuotaMaxCacheHours),
                runtime.OAuthInspectionCacheEnabled,
                Math.Clamp(runtime.OAuthAutoDisableThresholdPercent, 1, 100),
                forceRefresh: false,
                autoTriggered: true,
                cancellationToken: cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<AccountInspectionRunResult> RunInspectionAsync(
        IReadOnlyList<IAccountQuotaProvider> providers,
        ProxyRequestMetadataCache cache,
        int maxCacheHours,
        bool inspectionCacheEnabled,
        int autoDisableThresholdPercent,
        bool forceRefresh,
        bool autoTriggered,
        CancellationToken cancellationToken)
    {
        var result = new AccountInspectionRunResult
        {
            IsRunning = true,
            ForcedRefresh = forceRefresh,
            AutoTriggered = autoTriggered,
            StartedAt = DateTimeOffset.UtcNow,
        };
        AddLog("inspection", $"账号额度巡检开始{(forceRefresh ? "（强制真实刷新）" : "")}");

        foreach (var provider in providers)
        {
            IReadOnlyList<AccountQuotaTarget> accounts;
            try
            {
                accounts = await provider.GetAccountsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                AddLog("provider", $"提供程序 {provider.ProviderKey} 获取账号失败：{ex.Message}");
                _logger.LogError(ex, "Account quota provider {ProviderKey} failed to list accounts", provider.ProviderKey);
                continue;
            }

            foreach (var account in accounts)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var accountResult = await InspectAccountAsync(
                        provider,
                        account,
                        cache,
                        maxCacheHours,
                        inspectionCacheEnabled,
                        autoDisableThresholdPercent,
                        forceRefresh,
                        cancellationToken);
                    result.Accounts.Add(accountResult);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AddLog("account", $"账号 {account.DisplayName} 巡检失败：{ex.Message}");
                    _logger.LogError(ex, "Account quota inspection failed for {ProviderKey}/{AccountId}", provider.ProviderKey, account.AccountId);
                    result.Accounts.Add(new AccountInspectionAccountResult
                    {
                        ProviderKey = provider.ProviderKey,
                        AccountId = account.AccountId,
                        DisplayName = account.DisplayName,
                        Reason = ex.Message,
                        CheckedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
        }

        result.IsRunning = false;
        result.FinishedAt = DateTimeOffset.UtcNow;
        lock (_stateLock)
        {
            _lastRun = CloneRunResult(result);
        }
        AddLog(
            "inspection",
            $"账号额度巡检完成：保留 {result.KeepCount}、禁用 {result.DisableCount}、启用 {result.EnableCount}、缓存命中 {result.CacheCount}、真实刷新 {result.RealRefreshCount}");
        return result;
    }

    private DateTimeOffset? ReadNextScheduledAt()
    {
        var ticks = Volatile.Read(ref _nextScheduledAtUtcTicks);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static AccountInspectionRunResult CloneRunResult(AccountInspectionRunResult source)
    {
        return new AccountInspectionRunResult
        {
            IsRunning = source.IsRunning,
            ForcedRefresh = source.ForcedRefresh,
            StartedAt = source.StartedAt,
            FinishedAt = source.FinishedAt,
            AutoTriggered = source.AutoTriggered,
            Accounts = source.Accounts.Select(CloneAccountResult).ToList()
        };
    }

    private static AccountInspectionAccountResult CloneAccountResult(AccountInspectionAccountResult source)
    {
        return new AccountInspectionAccountResult
        {
            ProviderKey = source.ProviderKey,
            AccountId = source.AccountId,
            DisplayName = source.DisplayName,
            Action = source.Action,
            Reason = source.Reason,
            FromCache = source.FromCache,
            CheckedAt = source.CheckedAt,
            Windows = source.Windows.Select(window => new AccountQuotaWindow
            {
                Id = window.Id,
                Label = window.Label,
                UsedPercent = window.UsedPercent,
                ResetLabel = window.ResetLabel,
                ResetAtUtc = window.ResetAtUtc,
                LimitWindowSeconds = window.LimitWindowSeconds
            }).ToList()
        };
    }

    private async Task<AccountInspectionAccountResult> InspectAccountAsync(
        IAccountQuotaProvider provider,
        AccountQuotaTarget account,
        ProxyRequestMetadataCache cache,
        int maxCacheHours,
        bool inspectionCacheEnabled,
        int autoDisableThresholdPercent,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var result = new AccountInspectionAccountResult
        {
            ProviderKey = provider.ProviderKey,
            AccountId = account.AccountId,
            DisplayName = account.DisplayName,
            CheckedAt = DateTimeOffset.UtcNow,
        };

        if (account.TokenExpiresAt is { } tokenExpiresAt && tokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            result.Reason = "access_token 已过期，等待后台刷新，暂不进行额度巡检";
            AddLog("quota", $"账号 {account.DisplayName} access_token 已过期，暂不巡检");
            return result;
        }

        AccountQuotaSnapshot? cachedSnapshot = null;
        if (!string.IsNullOrWhiteSpace(account.LastQuotaRawJson))
        {
            cachedSnapshot = provider.ParseCachedQuota(account.LastQuotaRawJson);
        }

        var usedCache = false;
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && inspectionCacheEnabled)
        {
            var hasRecentUsage = HasRecentUsage(account.LinkedSiteId, account.LastQuotaCheckedAt);
            if (QuotaCachePolicy.TryReuseQuota(
                    cachedSnapshot,
                    account.LastQuotaCheckedAt,
                    hasRecentUsage,
                    maxCacheHours,
                    now,
                    out var cacheReason))
            {
                usedCache = true;
                result.FromCache = true;
                result.Reason = $"命中缓存：{cacheReason}";
                AddLog("quota", $"账号 {account.DisplayName} 命中缓存：{cacheReason}");
            }
        }

        var snapshot = usedCache && cachedSnapshot is not null
            ? cachedSnapshot
            : await provider.QueryAsync(account, forceRefresh: true, cancellationToken);

        result.Windows = snapshot.Windows;
        result.CheckedAt = snapshot.CheckedAt;
        if (!snapshot.Success)
        {
            result.Reason = string.IsNullOrWhiteSpace(snapshot.Error) ? "额度查询失败" : snapshot.Error;
            AddLog("quota", $"账号 {account.DisplayName} 额度查询失败：{result.Reason}");
            return result;
        }

        if (!usedCache)
        {
            result.Reason = "已真实刷新额度";
            AddLog("quota", $"账号 {account.DisplayName} 真实刷新成功");
        }

        var usedPercent = SelectThresholdPercent(snapshot.Windows);
        var threshold = (double)autoDisableThresholdPercent;
        if (usedPercent.HasValue && usedPercent.Value >= threshold && account.IsEnabled)
        {
            await provider.SetEnabledAsync(account, false, "quota-threshold", cancellationToken);
            result.Action = "disable";
            result.Reason = AppendReason(result.Reason, $"额度 {usedPercent.Value:F1}%≥{threshold}，已自动禁用");
        }
        else if (usedPercent.HasValue
                 && !account.IsEnabled
                 && !account.IsQuotaCooling
                 && !account.DisabledByFeatureToggle
                 && !account.ManuallyDisabled
                 && !account.DisabledByUpstream
                 && usedPercent.Value < threshold)
        {
            await provider.SetEnabledAsync(account, true, "quota-recovered", cancellationToken);
            result.Action = "enable";
            result.Reason = AppendReason(result.Reason, "额度已恢复，已自动启用");
        }

        return result;
    }

    private bool HasRecentUsage(Guid siteId, DateTimeOffset? since)
    {
        var lastUsedAt = _siteUsageTracker.GetLastUsedAt(siteId);
        if (lastUsedAt is null) return false;
        var cutoff = since ?? DateTimeOffset.UtcNow.AddDays(-1);
        return lastUsedAt.Value > cutoff;
    }

    private static double? SelectThresholdPercent(IReadOnlyList<AccountQuotaWindow> windows)
    {
        var candidates = windows
            .Where(window => window.UsedPercent.HasValue)
            .Select(window => window.UsedPercent!.Value)
            .ToList();
        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static string AppendReason(string current, string addition)
    {
        return string.IsNullOrWhiteSpace(current) ? addition : $"{current}；{addition}";
    }

    private void AddLog(string category, string message)
    {
        _logs.Enqueue(new AccountInspectionLogEntry
        {
            Category = category,
            Message = message,
        });
        while (_logs.Count > MaxLogs) _logs.TryDequeue(out _);
    }
}
