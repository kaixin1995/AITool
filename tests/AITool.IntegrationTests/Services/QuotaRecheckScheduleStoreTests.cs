using AITool.Application.Accounts;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;

namespace AITool.IntegrationTests.Services;

public sealed class QuotaRecheckScheduleStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-recheck-{Guid.NewGuid():N}.db");
    private readonly List<ServiceProvider> _providers = [];
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly TestIntervals _intervals = new();
    private readonly Guid _id = Guid.NewGuid();
    private readonly ServiceProvider _provider;
    private readonly QuotaRecheckScheduleStore _store;

    public QuotaRecheckScheduleStoreTests()
    {
        _provider = CreateProvider();
        var client = _provider.GetRequiredService<ISqlSugarClient>();
        client.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
        client.CodeFirst.InitTables<QuotaRecheckSchedule>();
        _store = Store(_provider);
    }

    [Fact]
    public async Task Ensure_is_idempotent_even_when_duplicate_event_has_newer_success_time()
    {
        await Ensure();
        var original = await Row();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Ensure();
        var current = await Row();
        current.NextCheckAtUtcTicks.Should().Be(original.NextCheckAtUtcTicks);
        current.CreatedAtUtcTicks.Should().Be(original.CreatedAtUtcTicks);
        current.UpdatedAtUtcTicks.Should().Be(original.UpdatedAtUtcTicks);
        current.Generation.Should().NotBe(original.Generation, "a repeated disable event fences stale listings without postponing due");
        (await Rows()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Recent_legacy_success_uses_its_instant_not_initialization_time()
    {
        var success = _clock.GetUtcNow().AddMinutes(-10).ToOffset(TimeSpan.FromHours(8));
        var result = await Reconcile(Target(success));
        result.Single().NextCheckAt.Should().Be(success.AddMinutes(50));
        result.Single().NextCheckAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overdue_or_unknown_legacy_success_gets_bounded_catchup_not_fresh_interval(bool unknown)
    {
        var now = _clock.GetUtcNow();
        var result = await Reconcile(Target(unknown ? null : now.AddDays(-5)));
        result.Single().NextCheckAt.Should().Be(now.AddSeconds(17));
        _intervals.CatchUpCalls.Should().Be(1);
    }

    [Fact]
    public async Task New_disable_event_checks_immediately_when_previous_check_is_overdue()
    {
        // 到期即查：距上次检查已超过随机间隔（额度窗口可能早已重置）→ 禁用事件立即以 0–60 秒错峰复查，
        // 而不是再等一个完整间隔（7d5ef23 曾误删该语义，用户要求保留）。
        await Ensure(_clock.GetUtcNow().AddMinutes(-90));
        (await Row()).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow().AddSeconds(17)).UtcTicks);
        _intervals.CatchUpCalls.Should().Be(1);
    }

    [Fact]
    public async Task New_disable_event_never_checks_later_than_one_random_interval()
    {
        // 保底上限：上次检查就在刚才 → next = last + 40–60 分钟随机，最迟约一小时内必有复查
        // （防上游提前重置额度而长期无人发现）。
        await Ensure(_clock.GetUtcNow());
        (await Row()).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow() + TimeSpan.FromMinutes(50)).UtcTicks);
        _intervals.CatchUpCalls.Should().Be(0);
    }

    [Fact]
    public async Task New_disable_event_without_history_checks_immediately()
    {
        // 从未检查过的账号（无历史时间）：同样立即错峰复查。
        await Ensure();
        (await Row()).NextCheckAtUtcTicks.Should().Be((_clock.GetUtcNow().AddSeconds(17)).UtcTicks);
        _intervals.CatchUpCalls.Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_preserves_existing_due_and_removes_only_ineligible_same_provider()
    {
        await Ensure();
        var due = (await Row()).NextCheckAtUtcTicks;
        var removed = Guid.NewGuid();
        await _store.EnsureScheduledAsync("p", removed, null, default);
        await _store.EnsureScheduledAsync("other", removed, null, default);
        _clock.Advance(TimeSpan.FromHours(2));
        var result = await Reconcile(Target(_clock.GetUtcNow()), Target(null));
        result.Should().ContainSingle().Which.NextCheckAt.UtcTicks.Should().Be(due);
        (await Rows()).Should().HaveCount(2).And.Contain(s => s.ProviderKey == "other");
        await Reconcile();
        (await Rows()).Should().ContainSingle().Which.ProviderKey.Should().Be("other");
    }

    [Fact]
    public async Task Unchanged_reconciliation_does_not_draw_intervals_or_modify_rows()
    {
        await Reconcile(Target(_clock.GetUtcNow()));
        var original = await Row();
        var intervalCalls = _intervals.IntervalCalls;
        var catchUpCalls = _intervals.CatchUpCalls;
        _clock.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 5; i++)
            await Reconcile(Target(null));
        (await Row()).Should().BeEquivalentTo(original);
        _intervals.IntervalCalls.Should().Be(intervalCalls);
        _intervals.CatchUpCalls.Should().Be(catchUpCalls);
    }

    [Fact]
    public async Task Not_due_cannot_be_acquired_but_exact_due_can()
    {
        await Ensure();
        (await _store.TryAcquireAsync("p", _id, default)).Should().BeNull();
        _clock.Advance(_intervals.Interval);
        (await _store.TryAcquireAsync("p", _id, default)).Should().NotBeNull();
        (await _store.TryAcquireAsync("p", Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task Forced_claim_bypasses_due_but_never_active_lease()
    {
        await Ensure();
        var lease = await _store.TryAcquireAsync("p", _id, default, force: true);
        lease.Should().NotBeNull();
        (await Store(CreateProvider()).TryAcquireAsync("p", _id, default, force: true)).Should().BeNull();
        _clock.Advance(TimeSpan.FromMinutes(3)); // Longer than bounded upstream request, shorter than lease.
        (await _store.TryAcquireAsync("p", _id, default, force: true)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_claims_across_independent_connections_have_exactly_one_winner()
    {
        await Ensure();
        _clock.Advance(_intervals.Interval);
        var other = Store(CreateProvider());
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return await (i % 2 == 0 ? _store : other).TryAcquireAsync("p", _id, default);
        })).ToArray();
        start.Set();
        var leases = await Task.WhenAll(attempts);
        leases.Count(l => l != null).Should().Be(1);
        (await Row()).LeaseToken.Should().Be(leases.Single(l => l != null)!.Token.ToString("D"));
    }

    [Fact]
    public async Task Concurrent_ensure_events_across_connections_insert_once()
    {
        var other = Store(CreateProvider());
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            (i % 2 == 0 ? _store : other).EnsureScheduledAsync("p", _id, _clock.GetUtcNow(), default))));
        (await Rows()).Should().ContainSingle();
    }

    [Fact]
    public async Task Inflight_reconcile_and_duplicate_disable_preserve_token_and_deadline()
    {
        await Ensure();
        var lease = await _store.TryAcquireAsync("p", _id, default, force: true);
        var claimed = await Row();
        await Ensure();
        await Reconcile(Target(_clock.GetUtcNow()));
        var current = await Row();
        current.NextCheckAtUtcTicks.Should().Be(claimed.NextCheckAtUtcTicks);
        current.LeaseToken.Should().Be(lease!.Token.ToString("D"));
    }

    [Fact]
    public async Task Failed_or_still_disabled_completion_schedules_from_completion_and_draws_again()
    {
        await Ensure();
        var lease = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        _clock.Advance(TimeSpan.FromMinutes(2));
        _intervals.Interval = TimeSpan.FromMinutes(41);
        await _store.CompleteAsync(lease, keepScheduled: true, default);
        var row = await Row();
        row.NextCheckAtUtcTicks.Should().Be(_clock.GetUtcNow().AddMinutes(41).UtcTicks);
        row.LeaseToken.Should().BeNull();
        row.LeaseExpiresAtUtcTicks.Should().BeNull();
        _clock.Advance(TimeSpan.FromMinutes(41));
        var nextLease = (await _store.TryAcquireAsync("p", _id, default))!;
        _intervals.Interval = TimeSpan.FromMinutes(59);
        await _store.CompleteAsync(nextLease, true, default);
        (await Row()).NextCheckAtUtcTicks.Should().Be(_clock.GetUtcNow().AddMinutes(59).UtcTicks);
    }

    [Fact]
    public async Task Completion_removes_row_without_resurrection_by_repeated_completion()
    {
        await Ensure();
        var lease = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        await _store.CompleteAsync(lease, false, default);
        await _store.CompleteAsync(lease, true, default);
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reconciliation_deletion_and_recreation_reject_both_forms_of_stale_completion()
    {
        await Ensure();
        var stale = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        _clock.Advance(QuotaRecheckScheduleStore.LeaseDuration);
        await Reconcile();
        await _store.CompleteAsync(stale, true, default);
        (await Rows()).Should().BeEmpty();
        await Ensure();
        var due = (await Row()).NextCheckAtUtcTicks;
        await _store.CompleteAsync(stale, true, default);
        await _store.CompleteAsync(stale, false, default);
        (await Row()).NextCheckAtUtcTicks.Should().Be(due);
    }

    [Fact]
    public async Task Crash_lease_expiry_allows_retry_without_fresh_interval_and_fences_old_worker()
    {
        await Ensure();
        var stale = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        var started = _clock.GetUtcNow();
        (await Row()).NextCheckAtUtcTicks.Should().Be(started.Add(QuotaRecheckScheduleStore.LeaseDuration).UtcTicks);
        _clock.Advance(QuotaRecheckScheduleStore.LeaseDuration - TimeSpan.FromTicks(1));
        (await Store(CreateProvider()).TryAcquireAsync("p", _id, default)).Should().BeNull();
        _clock.Advance(TimeSpan.FromTicks(1));
        var current = await Store(CreateProvider()).TryAcquireAsync("p", _id, default);
        current.Should().NotBeNull().And.NotBe(stale);
        await _store.CompleteAsync(stale, false, default);
        await _store.CompleteAsync(stale, true, default);
        (await Row()).LeaseToken.Should().Be(current!.Token.ToString("D"));
        await _store.CompleteAsync(current, true, default);
        (await Row()).LeaseToken.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expired_worker_cannot_complete_even_before_replacement_claim(bool keep)
    {
        await Ensure();
        var lease = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        _clock.Advance(QuotaRecheckScheduleStore.LeaseDuration);
        await _store.CompleteAsync(lease, keep, default);
        (await Row()).LeaseToken.Should().Be(lease.Token.ToString("D"));
    }

    [Fact]
    public async Task Schedules_survive_new_store_and_independent_service_provider()
    {
        await Ensure();
        var due = (await Row()).NextCheckAtUtcTicks;
        _clock.Advance(TimeSpan.FromMinutes(4));
        var result = await Store(CreateProvider()).ReconcileAsync("p", [Target(_clock.GetUtcNow())], default);
        result.Single().NextCheckAt.UtcTicks.Should().Be(due);
    }

    [Fact]
    public async Task Cancellation_does_not_mutate_existing_schedules()
    {
        await Ensure();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> reconcile = () => _store.ReconcileAsync("p", [], cts.Token);
        Func<Task> ensure = () => _store.EnsureScheduledAsync("p", Guid.NewGuid(), null, cts.Token);
        Func<Task> claim = () => _store.TryAcquireAsync("p", _id, cts.Token, force: true);
        await reconcile.Should().ThrowAsync<OperationCanceledException>();
        await ensure.Should().ThrowAsync<OperationCanceledException>();
        await claim.Should().ThrowAsync<OperationCanceledException>();
        (await Rows()).Should().ContainSingle().Which.LeaseToken.Should().BeNull();
    }

    [Fact]
    public async Task Stale_empty_listing_cannot_delete_new_disable_event_or_redraw_its_due()
    {
        var observation = await _store.BeginReconciliationAsync("p", default);
        var other = Store(CreateProvider());
        await other.EnsureScheduledAsync("p", _id, _clock.GetUtcNow(), default);
        var original = await Row();
        await _store.ReconcileAsync("p", [], observation, default);
        (await Row()).Should().BeEquivalentTo(original);
        await Reconcile(Target(_clock.GetUtcNow()));
        (await Row()).NextCheckAtUtcTicks.Should().Be(original.NextCheckAtUtcTicks);
        await Reconcile();
        (await Rows()).Should().BeEmpty("a fresh successful exclusion still cleans promptly");
    }

    [Fact]
    public async Task Same_tick_existing_ensure_fences_stale_exclusion_without_postponing_due()
    {
        await Ensure();
        var original = await Row();
        var observation = await _store.BeginReconciliationAsync("p", default);
        await Store(CreateProvider()).EnsureScheduledAsync("p", _id, _clock.GetUtcNow(), default);
        await _store.ReconcileAsync("p", [], observation, default);
        var current = await Row();
        current.Generation.Should().NotBe(original.Generation);
        current.NextCheckAtUtcTicks.Should().Be(original.NextCheckAtUtcTicks);
        current.UpdatedAtUtcTicks.Should().Be(original.UpdatedAtUtcTicks, "wall-clock ticks are deliberately unchanged");
        await Reconcile();
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_exclusion_cannot_remove_claim_or_permit_second_normal_due_claim()
    {
        await Ensure();
        var observation = await _store.BeginReconciliationAsync("p", default);
        _clock.Advance(_intervals.Interval);
        var other = Store(CreateProvider());
        var lease = (await other.TryAcquireAsync("p", _id, default))!;
        var claimed = await Row();
        await _store.ReconcileAsync("p", [], observation, default);
        (await Row()).Should().BeEquivalentTo(claimed);
        await Reconcile(Target(null));
        (await _store.TryAcquireAsync("p", _id, default)).Should().BeNull();
        (await _store.TryAcquireAsync("p", _id, default, force: true)).Should().BeNull();
        await other.CompleteAsync(lease, true, default);
    }

    [Fact]
    public async Task Even_fresh_observation_never_removes_live_lease_but_cleanup_resumes_after_completion()
    {
        await Ensure();
        var lease = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        var observation = await _store.BeginReconciliationAsync("p", default);
        await _store.ReconcileAsync("p", [], observation, default);
        (await Row()).LeaseToken.Should().Be(lease.Token.ToString("D"));
        await _store.CompleteAsync(lease, true, default);
        var completed = await Row();
        await _store.ReconcileAsync("p", [], observation, default);
        (await Row()).Should().BeEquivalentTo(completed, "completion changes generation too");
        await Reconcile();
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_observed_lease_is_cleaned_without_waiting_another_interval()
    {
        await Ensure();
        await _store.TryAcquireAsync("p", _id, default, force: true);
        var observation = await _store.BeginReconciliationAsync("p", default);
        _clock.Advance(QuotaRecheckScheduleStore.LeaseDuration);
        await _store.ReconcileAsync("p", [], observation, default);
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task New_disable_event_survives_old_worker_remove_completion_and_releases_lease()
    {
        await Ensure();
        var lease = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        var observation = await _store.BeginReconciliationAsync("p", default);
        await Store(CreateProvider()).EnsureScheduledAsync("p", _id, _clock.GetUtcNow(), default);
        _clock.Advance(TimeSpan.FromSeconds(10));
        _intervals.Interval = TimeSpan.FromMinutes(43);
        await _store.CompleteAsync(lease, false, default);
        var renewed = await Row();
        renewed.LeaseToken.Should().BeNull();
        renewed.LeaseExpiresAtUtcTicks.Should().BeNull();
        renewed.NextCheckAtUtcTicks.Should().Be(_clock.GetUtcNow().AddMinutes(43).UtcTicks);
        await _store.ReconcileAsync("p", [], observation, default);
        (await Row()).Should().BeEquivalentTo(renewed);
        await _store.CompleteAsync(lease, false, default);
        (await Row()).Should().BeEquivalentTo(renewed, "repeated old completion cannot alter the renewed event");
    }

    [Fact]
    public async Task Old_observation_cannot_delete_same_id_recreated_after_completed_removal()
    {
        await Ensure();
        var observation = await _store.BeginReconciliationAsync("p", default);
        var lease = (await _store.TryAcquireAsync("p", _id, default, force: true))!;
        await _store.CompleteAsync(lease, false, default);
        await Ensure();
        var recreated = await Row();
        await _store.ReconcileAsync("p", [], observation, default);
        (await Row()).Should().BeEquivalentTo(recreated);
    }

    [Fact]
    public async Task Compatibility_reconcile_without_observation_never_authorizes_cleanup()
    {
        await Ensure();
        var original = await Row();
        await _store.ReconcileAsync("p", [], default);
        (await Row()).Should().BeEquivalentTo(original);
        await Reconcile();
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task Observation_from_other_provider_is_rejected_without_mutation()
    {
        await Ensure();
        var observation = await _store.BeginReconciliationAsync("other", default);
        Func<Task> reconcile = () => _store.ReconcileAsync("p", [], observation, default);
        await reconcile.Should().ThrowAsync<ArgumentException>();
        (await Rows()).Should().ContainSingle();
    }

    [Fact]
    public void Production_jitter_always_uses_whole_seconds_in_inclusive_ranges()
    {
        var random = new QuotaRecheckIntervalSource();
        for (var i = 0; i < 10000; i++)
        {
            var interval = random.NextInterval();
            var catchUp = random.NextCatchUpDelay();
            interval.TotalSeconds.Should().BeInRange(2400, 3600);
            catchUp.TotalSeconds.Should().BeInRange(0, 60);
            (interval.Ticks % TimeSpan.TicksPerSecond).Should().Be(0);
            (catchUp.Ticks % TimeSpan.TicksPerSecond).Should().Be(0);
        }
    }

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={_path};Pooling=False");
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private QuotaRecheckScheduleStore Store(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), _clock, _intervals);
    private Task Ensure(DateTimeOffset? lastQuotaCheckedAt) => _store.EnsureScheduledAsync("p", _id, lastQuotaCheckedAt, default);
    private Task Ensure() => Ensure(null);
    private async Task<IReadOnlyList<QuotaRecheckScheduleInfo>> Reconcile(params AccountQuotaTarget[] targets)
    {
        var observation = await _store.BeginReconciliationAsync("p", default);
        return await _store.ReconcileAsync("p", targets, observation, default);
    }
    private AccountQuotaTarget Target(DateTimeOffset? success) => new()
    {
        ProviderKey = "p", AccountId = _id, LastQuotaCheckedAt = success
    };
    private async Task<List<QuotaRecheckSchedule>> Rows()
    {
        using var scope = _provider.CreateScope();
        using var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        return await client.Queryable<QuotaRecheckSchedule>().ToListAsync();
    }
    private async Task<QuotaRecheckSchedule> Row() => (await Rows()).Single(s => s.ProviderKey == "p" && s.AccountId == _id);

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            // AddSqlSugar registers a pre-created instance, which DI does not dispose.
            provider.GetRequiredService<ISqlSugarClient>().Dispose();
            provider.Dispose();
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delay) => now += delay;
    }

    private sealed class TestIntervals : IQuotaRecheckIntervalSource
    {
        public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(50);
        public int CatchUpCalls { get; private set; }
        public int IntervalCalls { get; private set; }
        public TimeSpan NextInterval() { IntervalCalls++; return Interval; }
        public TimeSpan NextCatchUpDelay()
        {
            CatchUpCalls++;
            return TimeSpan.FromSeconds(17);
        }
    }
}
