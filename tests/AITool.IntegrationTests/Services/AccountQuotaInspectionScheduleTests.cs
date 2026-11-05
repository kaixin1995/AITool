using AITool.Application.Accounts;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace AITool.IntegrationTests.Services;

/// <summary>Real SQLite schedules with a deterministic clock; no upstream requests or sleeps.</summary>
public sealed class AccountQuotaInspectionScheduleTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-inspector-schedule-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly Clock _clock = new();
    private readonly Provider _provider;
    private readonly QuotaRecheckScheduleStore _store;
    private readonly AccountQuotaInspectionService _inspection;

    public AccountQuotaInspectionScheduleTests()
    {
        _provider = new Provider(_clock);
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath};Pooling=False");
        services.AddSingleton<ProxyRequestMetadataCache>();
        services.AddSingleton<SiteUsageTracker>();
        services.AddSingleton<IAccountQuotaProvider>(_provider);
        _services = services.BuildServiceProvider();
        var client = _services.GetRequiredService<ISqlSugarClient>();
        SqlSugarSetup.InitializeDatabase(client);
        client.CodeFirst.InitTables<QuotaRecheckSchedule>();
        client.Insertable(new SystemRuntimeSettings
        {
            Id = 1, OAuthFeaturesEnabled = true, OAuthInspectionEnabled = true,
            OAuthInspectionIntervalSeconds = 7200, OAuthInspectionCacheEnabled = true,
            OAuthQuotaMaxCacheHours = 6, OAuthAutoDisableThresholdPercent = 95
        }).ExecuteCommand();
        _store = NewStore();
        _inspection = NewInspection(_store);
    }

    [Fact]
    public async Task Heartbeat_processes_only_due_disabled_accounts_before_global_sweep_and_bypasses_cache()
    {
        var enabled = _provider.Add(enabled: true);
        var disabled = _provider.Add(enabled: false);
        await Tick();
        _provider.Queries.Should().ContainSingle().Which.Id.Should().Be(enabled.Id);
        var firstDue = Schedule(disabled).NextCheckAtUtcTicks;
        firstDue.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
        var logs = _inspection.GetLogs().Count;
        _clock.Advance(TimeSpan.FromSeconds(10));
        await Tick();
        _inspection.GetLogs().Count.Should().Be(logs, "idle heartbeat must not spam inspection logs");
        _provider.ListCalls.Should().BeGreaterThan(1, "heartbeats still enumerate before global sweep");
        _clock.Set(new DateTimeOffset(firstDue, TimeSpan.Zero));
        await Tick();
        _provider.Queries.Select(query => query.Id).Should().Equal(enabled.Id, disabled.Id);
        _provider.Queries.Should().OnlyContain(query => query.Force);
        _inspection.GetLastRun()!.Accounts.Should().ContainSingle().Which.FromCache.Should().BeFalse();
        NextScheduled().Should().Be(_clock.GetUtcNow() + TimeSpan.FromMinutes(50));
    }

    [Fact]
    public async Task Many_accounts_use_constant_full_listings_and_refresh_each_target()
    {
        for (var i = 0; i < 20; i++) _provider.Add(enabled: true);
        _provider.UsePointReads = true;
        await Tick();
        _provider.Queries.Should().HaveCount(20);
        _provider.ListCalls.Should().Be(2, "only the initial and final provider snapshots are needed");
        _provider.PointCalls.Should().BeGreaterThanOrEqualTo(40);
    }

    [Fact]
    public async Task Restart_keeps_persisted_due_date_and_global_due_does_not_query_recovery_early()
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        var due = Schedule(disabled).NextCheckAtUtcTicks;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = NewInspection(NewStore());
        await restarted.TickAsync(CancellationToken.None);
        Schedule(disabled).NextCheckAtUtcTicks.Should().Be(due);
        _provider.Queries.Should().BeEmpty();
        _clock.Set(new DateTimeOffset(due, TimeSpan.Zero));
        await restarted.TickAsync(CancellationToken.None);
        _provider.Queries.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_throwing_query_reschedules_from_completion_without_retry_loop(bool throws)
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        _clock.Advance(TimeSpan.FromMinutes(50));
        _provider.QueryCallback = _ =>
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            if (throws) throw new InvalidOperationException("query failed");
        };
        _provider.QueryFails = true;
        await Tick();
        Schedule(disabled).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
        Schedule(disabled).LeaseToken.Should().BeNull();
        await Tick();
        _provider.Queries.Should().ContainSingle();
        _clock.Advance(TimeSpan.FromMinutes(50));
        await Tick();
        _provider.Queries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Expired_token_attempt_is_logged_and_rescheduled_not_stuck_due()
    {
        var disabled = _provider.Add(enabled: false);
        disabled.TokenExpiresAt = _clock.GetUtcNow().AddSeconds(-1);
        await Tick();
        _clock.Advance(TimeSpan.FromMinutes(50));
        await Tick();
        _provider.Queries.Should().BeEmpty();
        _inspection.GetLastRun()!.Accounts.Should().ContainSingle().Which.Reason.Should().Contain("已过期");
        Schedule(disabled).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
        await Tick();
        _inspection.GetLastRun()!.Accounts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("empty", false)]
    [InlineData("failed", false)]
    [InlineData("protected", false)]
    [InlineData("cached", false)]
    public async Task Route_quota_rest_is_released_only_after_confirmed_fresh_nonempty_recovery(string outcome, bool releases)
    {
        var disabled = _provider.Add(enabled: false);
        var circuits = new RouteCircuitStateStore();
        var quotaRoute = Guid.NewGuid();
        var rateRoute = Guid.NewGuid();
        circuits.Rest(quotaRoute, DateTimeOffset.UtcNow.AddDays(1), "quota", kind: RouteRestKind.Quota, siteId: disabled.Id);
        circuits.Rest(rateRoute, DateTimeOffset.UtcNow.AddDays(1), "rate", kind: RouteRestKind.RateLimit, siteId: disabled.Id);
        var policy = new RouteCandidatePolicyService(_services, circuits, new SessionAffinityService(), NullLogger<RouteCandidatePolicyService>.Instance);
        using var inspection = new AccountQuotaInspectionService(_services,
            NullLogger<AccountQuotaInspectionService>.Instance, new Environment(),
            _services.GetRequiredService<SiteUsageTracker>(), scheduleStore: outcome == "cached" ? null : _store,
            timeProvider: _clock, routePolicy: policy);
        _provider.UsedPercent = 20;
        _provider.CachedUsedPercent = 20;
        _provider.EmptyWindows = outcome == "empty";
        _provider.QueryFails = outcome == "failed";
        if (outcome == "protected") _provider.QueryCallback = account => account.Manual = true;
        var result = await inspection.RunManualAsync(forceRefresh: outcome != "cached", CancellationToken.None);
        if (outcome == "empty")
        {
            result.Accounts.Should().ContainSingle().Which.Reason.Should().Contain("未返回可主动检查的额度窗口");
            inspection.GetLogs().Should().Contain(entry => entry.Message.Contains("未返回可主动检查的额度窗口"));
            inspection.GetLogs().Should().NotContain(entry => entry.Message.Contains("真实刷新成功"));
        }
        circuits.IsBlocked(quotaRoute).Should().Be(!releases);
        circuits.IsBlocked(rateRoute).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_quota_exhaustion_during_healthy_query_is_preserved()
    {
        var disabled = _provider.Add(enabled: false);
        var circuits = new RouteCircuitStateStore();
        var route = Guid.NewGuid();
        var policy = new RouteCandidatePolicyService(_services, circuits, new SessionAffinityService(), NullLogger<RouteCandidatePolicyService>.Instance);
        using var inspection = new AccountQuotaInspectionService(_services,
            NullLogger<AccountQuotaInspectionService>.Instance, new Environment(),
            _services.GetRequiredService<SiteUsageTracker>(), _store, _clock, policy);

        _provider.UsedPercent = 20;
        // 模拟健康额度查询执行期间（在 QueryAsync 回调内），并发请求遭遇 402 并写入新的 Quota 休息
        _provider.QueryCallback = _ =>
        {
            circuits.Rest(route, DateTimeOffset.UtcNow.AddDays(1), "quota during query", kind: RouteRestKind.Quota, siteId: disabled.Id);
        };

        var result = await inspection.RunManualAsync(forceRefresh: true, CancellationToken.None);

        result.Accounts.Should().ContainSingle().Which.Action.Should().NotBe("enable");
        disabled.Enabled.Should().BeFalse();
        circuits.IsBlocked(route).Should().BeTrue();
        circuits.GetRest(route).Should().NotBeNull();
        Schedule(disabled).LeaseToken.Should().BeNull();

        // 后续新鲜查询未遇到新耗尽时仍可恢复。
        _provider.QueryCallback = null;
        var later = await inspection.RunManualAsync(forceRefresh: true, CancellationToken.None);
        later.Accounts.Should().ContainSingle().Which.Action.Should().Be("enable");
        disabled.Enabled.Should().BeTrue();
        circuits.IsBlocked(route).Should().BeFalse();
    }

    [Fact]
    public async Task Quota_recovery_serializes_with_same_site_exhaustion_during_async_enable()
    {
        var circuits = new RouteCircuitStateStore();
        var site = Guid.NewGuid();
        var route = Guid.NewGuid();
        var token = circuits.CaptureQuotaRestReleaseToken(site);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = circuits.TryRunQuotaRecoveryAsync(token, async () =>
        {
            entered.SetResult();
            await finish.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rest = Task.Run(() => circuits.Rest(route, DateTimeOffset.UtcNow.AddDays(1), "quota",
            kind: RouteRestKind.Quota, siteId: site));
        try
        {
            // A different site's write is not held up by the pending enable.
            var other = Task.Run(() => circuits.Rest(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), "quota",
                kind: RouteRestKind.Quota, siteId: Guid.NewGuid()));
            await other.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { finish.TrySetResult(); }
        (await recovery.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        await rest.WaitAsync(TimeSpan.FromSeconds(5));
        circuits.IsBlocked(route).Should().BeTrue();
        var invoked = false;
        (await circuits.TryRunQuotaRecoveryAsync(token, () => { invoked = true; return Task.CompletedTask; },
            CancellationToken.None)).Should().BeFalse();
        invoked.Should().BeFalse();
    }

    [Fact]
    public async Task Later_fresh_healthy_query_releases_quota_rest_after_enable_confirmation_failure()
    {
        var disabled = _provider.Add(enabled: false);
        var circuits = new RouteCircuitStateStore();
        var route = Guid.NewGuid();
        circuits.Rest(route, DateTimeOffset.UtcNow.AddDays(1), "quota", kind: RouteRestKind.Quota, siteId: disabled.Id);
        var policy = new RouteCandidatePolicyService(_services, circuits, new SessionAffinityService(), NullLogger<RouteCandidatePolicyService>.Instance);
        using var inspection = new AccountQuotaInspectionService(_services,
            NullLogger<AccountQuotaInspectionService>.Instance, new Environment(),
            _services.GetRequiredService<SiteUsageTracker>(), _store, _clock, policy);
        _provider.UsedPercent = 20;
        var failedConfirmations = 0;
        _provider.ListCallback = () =>
        {
            if (disabled.Enabled && failedConfirmations < 2)
            {
                failedConfirmations++;
                throw new InvalidOperationException("post-enable confirmation unavailable");
            }
            return Task.CompletedTask;
        };
        await inspection.RunManualAsync(true, CancellationToken.None);
        failedConfirmations.Should().Be(2, "both post-enable confirmation paths must actually fail before cleanup");
        disabled.Enabled.Should().BeTrue();
        Rows().Should().BeEmpty("final cleanup sees the successfully enabled state");
        circuits.IsBlocked(route).Should().BeTrue("both immediate confirmation reads failed");
        await inspection.RunManualAsync(true, CancellationToken.None);
        circuits.IsBlocked(route).Should().BeFalse("next real healthy query safely retries the recovery notification");
    }

    [Fact]
    public async Task Successful_recovery_removes_schedule_and_reports_confirmed_enable()
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        _provider.UsedPercent = 20;
        _clock.Advance(TimeSpan.FromMinutes(50));
        await Tick();
        disabled.Enabled.Should().BeTrue();
        Rows().Should().BeEmpty();
        _inspection.GetLastRun()!.Accounts.Should().ContainSingle().Which.Action.Should().Be("enable");
    }

    [Fact]
    public async Task Callback_manual_disable_race_is_not_reported_as_enabled_and_removes_schedule()
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        _provider.UsedPercent = 20;
        _provider.QueryCallback = account => account.Manual = true;
        _clock.Advance(TimeSpan.FromMinutes(50));
        await Tick();
        disabled.Enabled.Should().BeFalse();
        _inspection.GetLastRun()!.Accounts.Should().ContainSingle().Which.Action.Should().Be("keep");
        Rows().Should().BeEmpty();
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("feature")]
    [InlineData("cooling")]
    [InlineData("upstream")]
    public async Task Protection_flags_remove_recovery_work_without_enabling(string flag)
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        disabled.Manual = flag == "manual";
        disabled.Feature = flag == "feature";
        disabled.Cooling = flag == "cooling";
        disabled.Upstream = flag == "upstream";
        _clock.Advance(TimeSpan.FromMinutes(50));
        await Tick();
        Rows().Should().BeEmpty();
        _provider.Queries.Should().BeEmpty();
        disabled.Enabled.Should().BeFalse();
        await _inspection.RunManualAsync(true, CancellationToken.None);
        disabled.Enabled.Should().BeFalse("regular/manual protection behavior must be preserved");
    }

    [Fact]
    public async Task Reconciliation_does_not_delete_schedule_created_after_provider_snapshot_started()
    {
        var concurrentlyDisabled = Guid.NewGuid();
        _provider.SnapshotCallback = () => _store.EnsureScheduledAsync(_provider.ProviderKey,
            concurrentlyDisabled, null, CancellationToken.None);
        await Tick();
        Rows().Should().ContainSingle().Which.AccountId.Should().Be(concurrentlyDisabled);
        _provider.SnapshotCallback = null;
        await Tick();
        Rows().Should().BeEmpty("a later successful listing may safely remove the genuinely absent account");
    }

    [Fact]
    public async Task Failed_listing_preserves_persisted_schedules()
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        var next = Schedule(disabled).NextCheckAtUtcTicks;
        _provider.ListFails = true;
        _clock.Advance(TimeSpan.FromMinutes(50));
        await Tick();
        Schedule(disabled).NextCheckAtUtcTicks.Should().Be(next);
        _provider.ListFails = false;
        await Tick();
        _provider.Queries.Should().ContainSingle();
    }

    [Fact]
    public async Task Regular_query_that_disables_internally_is_scheduled_using_fresh_provider_state_and_stable_key()
    {
        var enabled = _provider.Add(enabled: true);
        _provider.QueryCallback = account => account.Enabled = false;
        await Tick();
        var row = Schedule(enabled);
        row.ProviderKey.Should().Be(_provider.ProviderKey, "target deliberately reports a wrong provider key");
        row.NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
    }

    [Fact]
    public async Task New_disable_event_during_inspection_waits_full_interval_even_with_stale_checked_at()
    {
        var enabled = _provider.Add(enabled: true);
        _provider.QueryCallback = account =>
        {
            account.Enabled = false;
            account.CheckedAt = _clock.GetUtcNow().AddDays(-2);
        };
        await Tick();
        var row = Schedule(enabled);
        row.NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
    }

    [Fact]
    public async Task Historical_overdue_account_backfilled_by_reconcile_uses_catch_up_delay()
    {
        var disabled = _provider.Add(enabled: false);
        disabled.CheckedAt = _clock.GetUtcNow().AddDays(-2);
        await Tick();
        var row = Schedule(disabled);
        row.NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromSeconds(30)).UtcTicks);
    }

    [Fact]
    public async Task Global_interval_is_measured_from_completion_and_same_account_is_never_queried_twice()
    {
        var enabled = _provider.Add(enabled: true);
        _provider.QueryCallback = _ => _clock.Advance(TimeSpan.FromHours(3));
        await Tick();
        NextScheduled().Should().Be(_clock.GetUtcNow().AddHours(2));
        await Tick();
        _provider.Queries.Should().ContainSingle().Which.Id.Should().Be(enabled.Id);
    }

    [Fact]
    public async Task Manual_force_overrides_due_with_lease_and_reschedules_but_nonforce_does_not()
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _inspection.RunManualAsync(false, CancellationToken.None);
        _provider.Queries.Should().BeEmpty();
        await _inspection.RunManualAsync(true, CancellationToken.None);
        _provider.Queries.Should().ContainSingle().Which.Force.Should().BeTrue();
        Schedule(disabled).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
        var lease = await _store.TryAcquireAsync(_provider.ProviderKey, disabled.Id, CancellationToken.None, force: true);
        lease.Should().NotBeNull();
        await _inspection.RunManualAsync(true, CancellationToken.None);
        _provider.Queries.Should().ContainSingle("manual refresh must not steal another inspector's lease");
        await _store.CompleteAsync(lease!, true, CancellationToken.None);
    }

    [Fact]
    public async Task Running_guard_covers_listing_and_prevents_tick_or_manual_overlap()
    {
        _provider.Add(enabled: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.ListCallback = async () => { entered.TrySetResult(); await release.Task; };
        var running = Tick();
        await entered.Task;
        await Tick();
        await _inspection.RunManualAsync(true, CancellationToken.None);
        _provider.ListCalls.Should().Be(1);
        release.SetResult();
        await running;
        _provider.Queries.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_global_switches_prevent_enumeration_and_queries(bool feature, bool inspection)
    {
        _services.GetRequiredService<ISqlSugarClient>().Updateable<SystemRuntimeSettings>()
            .SetColumns(settings => new SystemRuntimeSettings { OAuthFeaturesEnabled = feature, OAuthInspectionEnabled = inspection })
            .Where(settings => settings.Id == 1).ExecuteCommand();
        _provider.Add(enabled: false);
        await Tick();
        _provider.ListCalls.Should().Be(0);
        _provider.Queries.Should().BeEmpty();
        NextScheduled().Should().BeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Reenabling_switches_reschedules_immediately_and_does_not_repeat_at_same_timestamp(bool featureDisabled, bool inspectionDisabled)
    {
        var enabled = _provider.Add(enabled: true);
        await Tick();
        _provider.Queries.Should().ContainSingle().Which.Id.Should().Be(enabled.Id);
        var expectedFirstNext = _clock.GetUtcNow().AddSeconds(7200);
        NextScheduled().Should().Be(expectedFirstNext);

        _clock.Advance(TimeSpan.FromMinutes(10));

        var db = _services.GetRequiredService<ISqlSugarClient>();
        var metadataCache = _services.GetRequiredService<ProxyRequestMetadataCache>();

        db.Updateable<SystemRuntimeSettings>()
            .SetColumns(settings => new SystemRuntimeSettings { OAuthFeaturesEnabled = featureDisabled, OAuthInspectionEnabled = inspectionDisabled })
            .Where(settings => settings.Id == 1).ExecuteCommand();
        metadataCache.InvalidateRuntimeSettings();

        await Tick();
        _provider.Queries.Should().ContainSingle("no new queries while switches are disabled");
        NextScheduled().Should().BeNull();

        db.Updateable<SystemRuntimeSettings>()
            .SetColumns(settings => new SystemRuntimeSettings { OAuthFeaturesEnabled = true, OAuthInspectionEnabled = true })
            .Where(settings => settings.Id == 1).ExecuteCommand();
        metadataCache.InvalidateRuntimeSettings();

        await Tick();
        _provider.Queries.Should().HaveCount(2, "reenabling immediately triggers a regular inspection round");
        var expectedSecondNext = _clock.GetUtcNow().AddSeconds(7200);
        NextScheduled().Should().Be(expectedSecondNext);

        await Tick();
        _provider.Queries.Should().HaveCount(2, "inspection must not repeat at the same timestamp");
        NextScheduled().Should().Be(expectedSecondNext);
    }

    [Fact]
    public async Task Due_work_is_sorted_by_due_date_then_id_and_not_duplicated_on_global_sweep()
    {
        var first = _provider.Add(enabled: false);
        var second = _provider.Add(enabled: false);
        var third = _provider.Add(enabled: false);
        await Tick();
        var due = _clock.GetUtcNow().AddHours(2);
        var db = _services.GetRequiredService<ISqlSugarClient>();
        db.Updateable<QuotaRecheckSchedule>().SetColumns(row => row.NextCheckAtUtcTicks == due.UtcTicks)
            .Where(row => row.ProviderKey == _provider.ProviderKey).ExecuteCommand();
        db.Updateable<QuotaRecheckSchedule>().SetColumns(row => row.NextCheckAtUtcTicks == due.AddMinutes(-1).UtcTicks)
            .Where(row => row.AccountId == second.Id).ExecuteCommand();
        _clock.Set(due);
        await Tick();
        _provider.Queries.Select(query => query.Id).Should().Equal(
            new[] { second.Id }.Concat(new[] { first.Id, third.Id }.OrderBy(id => id)));
    }

    [Fact]
    public async Task Earlier_query_callback_disabling_later_regular_target_prevents_early_recovery_query()
    {
        var first = _provider.Add(enabled: true);
        var second = _provider.Add(enabled: true);
        _provider.QueryCallback = account => { if (account.Id == first.Id) second.Enabled = false; };
        await Tick();
        _provider.Queries.Should().ContainSingle().Which.Id.Should().Be(first.Id);
        Schedule(second).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
    }

    [Fact]
    public async Task Cancellation_completes_recovery_lease_with_independent_cleanup_token()
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        _clock.Advance(TimeSpan.FromMinutes(50));
        using var cts = new CancellationTokenSource();
        _provider.QueryCallback = _ => cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _inspection.TickAsync(cts.Token));
        Schedule(disabled).LeaseToken.Should().BeNull();
        Schedule(disabled).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
    }

    [Fact]
    public async Task Cancelled_recovery_stops_waiting_for_provider_that_ignores_cancellation()
    {
        var disabled = _provider.Add(enabled: false);
        await Tick();
        _clock.Advance(TimeSpan.FromMinutes(50));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<AccountQuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.QueryOverride = () => { entered.SetResult(); return reply.Task; };
        using var cts = new CancellationTokenSource();
        var running = _inspection.TickAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Schedule(disabled).LeaseToken.Should().BeNull();
        reply.SetResult(new AccountQuotaSnapshot { Success = true, Windows = [new AccountQuotaWindow { UsedPercent = 1 }] });
        await Task.Delay(50);
        disabled.Enabled.Should().BeFalse("a late successful result must not trigger recovery");
    }

    private Task Tick() => _inspection.TickAsync(CancellationToken.None);
    private QuotaRecheckScheduleStore NewStore() => new(_services.GetRequiredService<IServiceScopeFactory>(), _clock, new Intervals());
    private AccountQuotaInspectionService NewInspection(IQuotaRecheckScheduleStore store) => new(
        _services, NullLogger<AccountQuotaInspectionService>.Instance, new Environment(),
        _services.GetRequiredService<SiteUsageTracker>(), store, _clock);
    private List<QuotaRecheckSchedule> Rows() => _services.GetRequiredService<ISqlSugarClient>().Queryable<QuotaRecheckSchedule>().ToList();
    private QuotaRecheckSchedule Schedule(Account account) => Rows().Single(row => row.AccountId == account.Id);
    private DateTimeOffset? NextScheduled() => (DateTimeOffset?)_inspection.GetStatus().GetType().GetProperty("nextScheduledAt")!.GetValue(_inspection.GetStatus());

    public void Dispose()
    {
        _inspection.Dispose();
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_databasePath + suffix); } catch { }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
        public void Set(DateTimeOffset time) => _now = time;
    }

    private sealed class Intervals : IQuotaRecheckIntervalSource
    {
        public TimeSpan NextInterval() => TimeSpan.FromMinutes(50);
        public TimeSpan NextCatchUpDelay() => TimeSpan.FromSeconds(30);
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AITool.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Account(Guid id, bool enabled)
    {
        public Guid Id { get; } = id;
        public bool Enabled { get; set; } = enabled;
        public bool Manual { get; set; }
        public bool Feature { get; set; }
        public bool Cooling { get; set; }
        public bool Upstream { get; set; }
        public DateTimeOffset? CheckedAt { get; set; }
        public DateTimeOffset? TokenExpiresAt { get; set; }
    }

    private sealed class Provider(Clock clock) : IAccountQuotaProvider
    {
        public string ProviderKey => "schedule-test";
        public List<Account> Accounts { get; } = [];
        public List<(Guid Id, bool Force)> Queries { get; } = [];
        public int ListCalls { get; private set; }
        public int PointCalls { get; private set; }
        public bool UsePointReads { get; set; }
        public bool ListFails { get; set; }
        public bool QueryFails { get; set; }
        public double UsedPercent { get; set; } = 92;
        public double CachedUsedPercent { get; set; } = 92;
        public bool EmptyWindows { get; set; }
        public Action<Account>? QueryCallback { get; set; }
        public Func<Task<AccountQuotaSnapshot>>? QueryOverride { get; set; }
        public Func<Task>? ListCallback { get; set; }
        public Func<Task>? SnapshotCallback { get; set; }
        public Account Add(bool enabled)
        {
            var account = new Account(Guid.NewGuid(), enabled) { CheckedAt = clock.GetUtcNow() };
            Accounts.Add(account);
            return account;
        }
        public async Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            if (ListCallback is not null) await ListCallback();
            if (ListFails) throw new InvalidOperationException("enumeration unavailable");
            var snapshot = Accounts.Select(account => new AccountQuotaTarget
            {
                ProviderKey = "mislabelled-target", AccountId = account.Id, LinkedSiteId = account.Id, DisplayName = account.Id.ToString(),
                IsEnabled = account.Enabled, ManuallyDisabled = account.Manual,
                DisabledByFeatureToggle = account.Feature, DisabledByUpstream = account.Upstream,
                IsQuotaCooling = account.Cooling, LastQuotaCheckedAt = account.CheckedAt,
                LastQuotaRawJson = account.Enabled ? null : "cached", TokenExpiresAt = account.TokenExpiresAt
            }).ToList();
            if (SnapshotCallback is not null) await SnapshotCallback();
            return snapshot;
        }
        public async Task<AccountQuotaTarget?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
        {
            PointCalls++;
            if (!UsePointReads)
                return (await GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.AccountId == accountId);
            var account = Accounts.FirstOrDefault(a => a.Id == accountId);
            return account is null ? null : new AccountQuotaTarget
            {
                ProviderKey = ProviderKey, AccountId = account.Id, LinkedSiteId = account.Id,
                DisplayName = account.Id.ToString(), IsEnabled = account.Enabled,
                ManuallyDisabled = account.Manual, DisabledByFeatureToggle = account.Feature,
                DisabledByUpstream = account.Upstream, IsQuotaCooling = account.Cooling,
                LastQuotaCheckedAt = account.CheckedAt, TokenExpiresAt = account.TokenExpiresAt
            };
        }
        public AccountQuotaSnapshot? ParseCachedQuota(string rawJson) => new()
        {
            Success = true, CheckedAt = clock.GetUtcNow(),
            Windows = [new AccountQuotaWindow { UsedPercent = CachedUsedPercent, ResetAtUtc = clock.GetUtcNow().AddDays(2) }]
        };
        public Task<AccountQuotaSnapshot> QueryAsync(AccountQuotaTarget target, bool forceRefresh, CancellationToken cancellationToken)
        {
            Queries.Add((target.AccountId, forceRefresh));
            if (QueryOverride is not null) return QueryOverride();
            var account = Accounts.Single(account => account.Id == target.AccountId);
            QueryCallback?.Invoke(account);
            cancellationToken.ThrowIfCancellationRequested();
            account.CheckedAt = clock.GetUtcNow();
            return Task.FromResult(new AccountQuotaSnapshot
            {
                Success = !QueryFails, Error = QueryFails ? "no access token" : null, CheckedAt = clock.GetUtcNow(),
                Windows = EmptyWindows ? [] : [new AccountQuotaWindow { UsedPercent = UsedPercent, ResetAtUtc = clock.GetUtcNow().AddDays(2) }]
            });
        }
        public Task SetEnabledAsync(AccountQuotaTarget target, bool enabled, string reason, CancellationToken cancellationToken)
        {
            var account = Accounts.Single(account => account.Id == target.AccountId);
            if (!enabled || !(account.Manual || account.Feature || account.Cooling || account.Upstream))
            {
                account.Enabled = enabled;
            }
            return Task.CompletedTask;
        }
        public Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
