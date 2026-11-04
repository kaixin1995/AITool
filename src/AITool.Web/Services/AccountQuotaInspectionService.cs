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
    private readonly IQuotaRecheckScheduleStore? _scheduleStore;
    private readonly TimeProvider _timeProvider;
    private readonly RouteCandidatePolicyService? _routePolicy;
    private readonly Dictionary<(string Provider, Guid Account), DateTimeOffset> _recheckTimes = new();
    private long _nextGlobalAtUtcTicks;

    private readonly object _stateLock = new();
    private AccountInspectionRunResult? _lastRun;
    private readonly ConcurrentQueue<AccountInspectionLogEntry> _logs = new();
    private long _nextScheduledAtUtcTicks;
    private int _running;

    public AccountQuotaInspectionService(
        IServiceProvider services,
        ILogger<AccountQuotaInspectionService> logger,
        IHostEnvironment environment,
        SiteUsageTracker siteUsageTracker,
        IQuotaRecheckScheduleStore? scheduleStore = null,
        TimeProvider? timeProvider = null,
        RouteCandidatePolicyService? routePolicy = null)
    {
        _services = services;
        _logger = logger;
        _environment = environment;
        _siteUsageTracker = siteUsageTracker;
        _scheduleStore = scheduleStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _routePolicy = routePolicy;
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
                await Task.Delay(TickInterval, _timeProvider, stoppingToken);
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
                FinishedAt = _timeProvider.GetUtcNow(),
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

    /// <summary>One heartbeat; exposed internally for deterministic clock-driven tests.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        // Cover listing as well as queries: a manual pass must never overlap a heartbeat.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            using var scope = _services.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
            var runtime = await cache.GetRuntimeSettingsAsync(cancellationToken);
            if (!runtime.OAuthFeaturesEnabled || !runtime.OAuthInspectionEnabled)
            {
                Volatile.Write(ref _nextScheduledAtUtcTicks, 0);
                Volatile.Write(ref _nextGlobalAtUtcTicks, 0);
                return;
            }

            var globalDue = _nextGlobalAtUtcTicks == 0 || _timeProvider.GetUtcNow().UtcTicks >= _nextGlobalAtUtcTicks;
            if (!globalDue && _scheduleStore is null) return;
            var providers = scope.ServiceProvider.GetServices<IAccountQuotaProvider>().ToList();
            await RunInspectionAsync(
                providers, cache,
                Math.Max(1, runtime.OAuthQuotaMaxCacheHours),
                runtime.OAuthInspectionCacheEnabled,
                Math.Clamp(runtime.OAuthAutoDisableThresholdPercent, 1, 100),
                forceRefresh: false,
                autoTriggered: true,
                cancellationToken: cancellationToken,
                runRegular: globalDue);

            // Start the normal interval at completion, not at the beginning of slow upstream work.
            if (globalDue)
                _nextGlobalAtUtcTicks = (_timeProvider.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(30, runtime.OAuthInspectionIntervalSeconds))).UtcTicks;
            PublishNextScheduledAt();
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static bool IsRecoveryEligible(AccountQuotaTarget account) =>
        !account.IsEnabled && !account.ManuallyDisabled && !account.DisabledByFeatureToggle
        && !account.DisabledByUpstream && !account.IsQuotaCooling;

    private void PublishNextScheduledAt()
    {
        var next = _nextGlobalAtUtcTicks;
        foreach (var time in _recheckTimes.Values)
            if (next == 0 || time.UtcTicks < next) next = time.UtcTicks;
        Volatile.Write(ref _nextScheduledAtUtcTicks, next);
    }

    private sealed record ProviderListing(IReadOnlyList<AccountQuotaTarget> Accounts, QuotaRecheckObservation? Observation);

    private async Task ReconcileAsync(IAccountQuotaProvider provider, ProviderListing listing, CancellationToken cancellationToken)
    {
        if (_scheduleStore is null) return;
        var eligible = listing.Accounts.Where(IsRecoveryEligible).ToList();
        var schedules = listing.Observation is null
            ? await _scheduleStore.ReconcileAsync(provider.ProviderKey, eligible, cancellationToken)
            : await _scheduleStore.ReconcileAsync(provider.ProviderKey, eligible, listing.Observation, cancellationToken);
        foreach (var key in _recheckTimes.Keys.Where(key => key.Provider == provider.ProviderKey).ToList())
            _recheckTimes.Remove(key);
        foreach (var schedule in schedules)
            _recheckTimes[(provider.ProviderKey, schedule.AccountId)] = schedule.NextCheckAt;
    }

    private async Task<AccountInspectionRunResult> RunInspectionAsync(
        IReadOnlyList<IAccountQuotaProvider> providers,
        ProxyRequestMetadataCache cache,
        int maxCacheHours,
        bool inspectionCacheEnabled,
        int autoDisableThresholdPercent,
        bool forceRefresh,
        bool autoTriggered,
        CancellationToken cancellationToken,
        bool runRegular = true)
    {
        var result = new AccountInspectionRunResult
        {
            IsRunning = true,
            ForcedRefresh = forceRefresh,
            AutoTriggered = autoTriggered,
            StartedAt = _timeProvider.GetUtcNow(),
        };
        var listed = new List<(IAccountQuotaProvider Provider, AccountQuotaTarget Account)>();
        foreach (var provider in providers)
        {
            var accounts = await ListAccountsAsync(provider, cancellationToken);
            if (accounts is null) continue; // Failed listings must not delete persistent schedules.
            await ReconcileAsync(provider, accounts, cancellationToken);
            listed.AddRange(accounts.Accounts.Select(account => (provider, account)));
        }

        var now = _timeProvider.GetUtcNow();
        var recovery = _scheduleStore is null
            ? []
            : listed.Where(item => IsRecoveryEligible(item.Account)
                && ((!autoTriggered && forceRefresh)
                    || (_recheckTimes.TryGetValue((item.Provider.ProviderKey, item.Account.AccountId), out var at) && at <= now)))
                .OrderBy(item => _recheckTimes.GetValueOrDefault((item.Provider.ProviderKey, item.Account.AccountId)))
                .ThenBy(item => item.Provider.ProviderKey, StringComparer.Ordinal)
                .ThenBy(item => item.Account.AccountId).ToList();
        var regular = runRegular
            ? listed.Where(item => _scheduleStore is null || !IsRecoveryEligible(item.Account)).ToList()
            : [];
        var work = recovery.Concat(regular).DistinctBy(item => (item.Provider.ProviderKey, item.Account.AccountId)).ToList();
        if (work.Count == 0 && !runRegular)
        {
            PublishNextScheduledAt();
            return result; // Quiet heartbeat: preserve the last real run and avoid ten-second log spam.
        }

        AddLog("inspection", $"账号额度巡检开始{(forceRefresh ? "（强制真实刷新）" : "")}{(!runRegular ? "（到期恢复复查）" : "")}");
        foreach (var (provider, originalAccount) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = originalAccount;
            QuotaRecheckLease? lease = null;
            var recoveryWork = _scheduleStore is not null && IsRecoveryEligible(account);
            if (_scheduleStore is not null)
            {
                // An earlier callback may have changed another planned target.
                var (success, freshAccount) = await GetAccountAsync(provider, account.AccountId, cancellationToken);
                if (!success || freshAccount is null || IsRecoveryEligible(freshAccount) != recoveryWork) continue;
                account = freshAccount;
            }
            if (recoveryWork)
            {
                lease = await _scheduleStore!.TryAcquireAsync(provider.ProviderKey, account.AccountId, cancellationToken,
                    force: !autoTriggered && forceRefresh);
                if (lease is null) continue;
                account.QuotaRecheckLeaseToken = lease.Token;
                AddLog("schedule", $"账号 {account.DisplayName} 开始{(!autoTriggered && forceRefresh ? "手动强制" : "到期")}额度恢复复查（绕过缓存）");
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (lease is not null) attempt.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                result.Accounts.Add(await InspectAccountAsync(
                    provider, account, cache, maxCacheHours, inspectionCacheEnabled,
                    autoDisableThresholdPercent, forceRefresh || recoveryWork, attempt.Token));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
                    CheckedAt = _timeProvider.GetUtcNow(),
                });
            }
            finally
            {
                if (_scheduleStore is not null)
                {
                    // Cancellation must not strand a lease or create an immediate retry loop.
                    var success = false;
                    AccountQuotaTarget? current = null;
                    using (var listingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        try { (success, current) = await GetAccountAsync(provider, account.AccountId, listingTimeout.Token); }
                        catch (OperationCanceledException) when (listingTimeout.IsCancellationRequested) { }
                    }
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (lease is not null)
                        await _scheduleStore.CompleteAsync(lease,
                            keepScheduled: !success || (current is not null && IsRecoveryEligible(current)), cleanup.Token);
                    else if (current is not null && IsRecoveryEligible(current))
                        await _scheduleStore.EnsureScheduledAsync(provider.ProviderKey, current.AccountId, current.LastQuotaCheckedAt, cleanup.Token);
                    if (lease is not null)
                        AddLog("schedule", $"账号 {account.DisplayName} 额度恢复复查完成（完成后随机间隔）");
                }
            }
        }

        // Refresh schedules once per affected provider, rather than after every account.
        if (_scheduleStore is not null)
        {
            foreach (var provider in work.Select(item => item.Provider).Distinct())
            {
                var fresh = await ListAccountsAsync(provider, cancellationToken);
                if (fresh is not null) await ReconcileAsync(provider, fresh, cancellationToken);
            }
        }

        result.IsRunning = false;
        result.FinishedAt = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            _lastRun = CloneRunResult(result);
        }
        PublishNextScheduledAt();
        AddLog(
            "inspection",
            $"账号额度巡检完成：保留 {result.KeepCount}、禁用 {result.DisableCount}、启用 {result.EnableCount}、缓存命中 {result.CacheCount}、真实刷新 {result.RealRefreshCount}");
        return result;
    }

    private async Task<ProviderListing?> ListAccountsAsync(IAccountQuotaProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            // Capture generations BEFORE the account snapshot; later events must survive stale cleanup.
            var observation = _scheduleStore is null ? null
                : await _scheduleStore.BeginReconciliationAsync(provider.ProviderKey, cancellationToken);
            var accounts = await provider.GetAccountsAsync(cancellationToken);
            return new ProviderListing(accounts, observation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog("provider", $"提供程序 {provider.ProviderKey} 获取账号失败：{ex.Message}");
            _logger.LogError(ex, "Account quota provider {ProviderKey} failed to list accounts", provider.ProviderKey);
            return null;
        }
    }

    private async Task<(bool Success, AccountQuotaTarget? Account)> GetAccountAsync(
        IAccountQuotaProvider provider, Guid accountId, CancellationToken cancellationToken)
    {
        try { return (true, await provider.GetAccountAsync(accountId, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AddLog("provider", $"提供程序 {provider.ProviderKey} 获取账号失败：{ex.Message}");
            _logger.LogError(ex, "Account quota provider {ProviderKey} failed to read account {AccountId}", provider.ProviderKey, accountId);
            return (false, null);
        }
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

    private static async Task<AccountQuotaSnapshot> QueryWithBoundedWaitAsync(
        IAccountQuotaProvider provider, AccountQuotaTarget account, CancellationToken cancellationToken)
    {
        var query = provider.QueryAsync(account, forceRefresh: true, cancellationToken);
        // A provider may ignore cancellation and fault after the caller stops waiting.
        _ = query.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await query.WaitAsync(cancellationToken);
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
            CheckedAt = _timeProvider.GetUtcNow(),
        };

        if (account.TokenExpiresAt is { } tokenExpiresAt && tokenExpiresAt <= _timeProvider.GetUtcNow())
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
        var now = _timeProvider.GetUtcNow();
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

        // 必须在 QueryAsync 之前捕获令牌，查询期间新到的 402 写入的休息将拥有更高版本，从而避免被本轮旧快照解除。
        QuotaRestReleaseToken? quotaRestToken = _routePolicy is not null && !usedCache
            ? _routePolicy.CaptureQuotaRestReleaseToken(account.LinkedSiteId)
            : null;

        var snapshot = usedCache && cachedSnapshot is not null
            ? cachedSnapshot
            : await QueryWithBoundedWaitAsync(provider, account, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        result.Windows = snapshot.Windows;
        result.CheckedAt = snapshot.CheckedAt;
        if (!snapshot.Success)
        {
            result.Reason = string.IsNullOrWhiteSpace(snapshot.Error) ? "额度查询失败" : snapshot.Error;
            AddLog("quota", $"账号 {account.DisplayName} 额度查询失败：{result.Reason}");
            return result;
        }

        if (snapshot.Windows.Count == 0)
        {
            result.Reason = "提供程序未返回可主动检查的额度窗口，本轮不判断恢复";
            AddLog("quota", $"账号 {account.DisplayName} {result.Reason}");
            return result;
        }

        if (!usedCache)
        {
            result.Reason = "已真实刷新额度";
            AddLog("quota", $"账号 {account.DisplayName} 真实刷新成功");
        }

        var usedPercent = provider.SelectDisablePercent(snapshot);
        var threshold = (double)autoDisableThresholdPercent;
        // 非对称阈值（滞回带）：禁用沿用配置阈值，回启要求低于阈值减去滞回量，
        // 避免读数在阈值附近波动时账号被反复禁用/启用（对齐 magpie 的 98/90 双阈值做法）。
        var reenableThreshold = Math.Max(1, threshold - ReenableHysteresisPercent);
        if (usedPercent.HasValue && usedPercent.Value >= threshold && account.IsEnabled)
        {
            if (usedCache)
            {
                // 禁用是高影响动作：若判定基于缓存读数，先绕过缓存强制一次新鲜读数复核，
                // 新鲜读数失败则本轮跳过自动禁用（宁可靠代理侧冷却兜底，不基于过期数据禁用）。
                AddLog("quota", $"账号 {account.DisplayName} 缓存读数达阈值，强制新鲜读数复核");
                var fresh = await QueryWithBoundedWaitAsync(provider, account, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!fresh.Success)
                {
                    result.Reason = AppendReason(result.Reason, "新鲜读数失败，本轮跳过自动禁用");
                    AddLog("quota", $"账号 {account.DisplayName} 新鲜读数失败，暂不自动禁用");
                    return result;
                }

                snapshot = fresh;
                result.Windows = fresh.Windows;
                result.CheckedAt = fresh.CheckedAt;
                result.FromCache = false;
                usedPercent = provider.SelectDisablePercent(fresh);
            }

            if (usedPercent.HasValue && usedPercent.Value >= threshold)
            {
                await provider.SetEnabledAsync(account, false, "quota-threshold", cancellationToken);
                result.Action = "disable";
                result.Reason = AppendReason(result.Reason, $"额度 {usedPercent.Value:F1}%≥{threshold}，已自动禁用");
            }
            else
            {
                result.Reason = AppendReason(result.Reason, $"复核读数 {usedPercent:F1}%，低于阈值，保持启用");
            }
        }
        else if (usedPercent.HasValue
                 && !account.IsEnabled
                 && !account.IsQuotaCooling
                 && !account.DisabledByFeatureToggle
                 && !account.ManuallyDisabled
                 && !account.DisabledByUpstream
                 && usedPercent.Value < reenableThreshold)
        {
            await provider.SetEnabledAsync(account, true, "quota-recovered", cancellationToken);
            // Provider guards can reject recovery when a concurrent manual/feature disable wins.
            var (_, confirmed) = await GetAccountAsync(provider, account.AccountId, cancellationToken);
            if (confirmed?.IsEnabled == true)
            {
                result.Action = "enable";
                result.Reason = AppendReason(
                    result.Reason,
                    $"额度已恢复（{usedPercent.Value:F1}%<回启阈值 {reenableThreshold:F0}%），已自动启用");
            }
            else
                result.Reason = AppendReason(result.Reason, "额度已恢复，但账号当前状态不允许自动启用或未能确认启用");
        }

        // Retrying this idempotent signal on later fresh healthy checks also covers a successful
        // enable whose immediate confirmation failed. Cached/empty/protected snapshots cannot release rests.
        if (_routePolicy is not null && quotaRestToken.HasValue && !usedCache && snapshot.Success && snapshot.Windows.Count > 0
            && usedPercent.HasValue && usedPercent.Value < reenableThreshold)
        {
            var (_, confirmed) = await GetAccountAsync(provider, account.AccountId, cancellationToken);
            if (confirmed?.IsEnabled == true && confirmed.LinkedSiteId == quotaRestToken.Value.SiteId
                && !confirmed.ManuallyDisabled && !confirmed.DisabledByFeatureToggle
                && !confirmed.DisabledByUpstream && !confirmed.IsQuotaCooling)
                _routePolicy.OnAccountQuotaRecovered(quotaRestToken.Value);
        }

        return result;
    }

    /// <summary>回启滞回量（百分点）：自动禁用阈值减去该值得回启阈值，防止阈值附近反复启停。</summary>
    private const int ReenableHysteresisPercent = 8;

    private bool HasRecentUsage(Guid siteId, DateTimeOffset? since)
    {
        var lastUsedAt = _siteUsageTracker.GetLastUsedAt(siteId);
        if (lastUsedAt is null) return false;
        var cutoff = since ?? _timeProvider.GetUtcNow().AddDays(-1);
        return lastUsedAt.Value > cutoff;
    }

    private static string AppendReason(string current, string addition)
    {
        return string.IsNullOrWhiteSpace(current) ? addition : $"{current}；{addition}";
    }

    private void AddLog(string category, string message)
    {
        _logs.Enqueue(new AccountInspectionLogEntry
        {
            At = _timeProvider.GetUtcNow(),
            Category = category,
            Message = message,
        });
        while (_logs.Count > MaxLogs) _logs.TryDequeue(out _);
    }
}
