using AITool.Application.Accounts;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Services;

public sealed class QuotaRecoveryRouteRestTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-quota-recovery-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly RouteCircuitStateStore _store = new();
    private readonly CachedAccountProvider _accounts = new();
    private readonly RouteCandidatePolicyService _policy;

    public QuotaRecoveryRouteRestTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddSingleton<IAccountQuotaProvider>(_accounts);
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _policy = new RouteCandidatePolicyService(_services, _store, new SessionAffinityService(),
            NullLogger<RouteCandidatePolicyService>.Instance);
    }

    [Theory]
    [InlineData(402, "payment required")]
    [InlineData(429, "insufficient_quota")]
    public async Task Confirmed_recovery_releases_quota_rest_for_direct_and_reference_candidates(int status, string body)
    {
        var direct = Route(Guid.NewGuid());
        var reference = new CachedProxyRouteTarget
        {
            SiteId = direct.SiteId, CircuitKey = direct.CircuitKey,
            FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 1,
        };
        await Fail(direct, status, body);
        _store.IsBlocked(reference.CircuitKey).Should().BeTrue();

        _policy.OnAccountQuotaRecovered(direct.SiteId);

        _store.GetRest(direct.CircuitKey).Should().BeNull();
        _store.IsBlocked(direct.CircuitKey).Should().BeFalse();
        _store.IsBlocked(reference.CircuitKey).Should().BeFalse();
        _store.GetAllCircuitStates().Should().NotContainKey(direct.CircuitKey);
    }

    [Fact]
    public async Task Recovery_does_not_match_display_name_or_touch_another_physical_site()
    {
        var recovered = Route(Guid.NewGuid());
        var sameName = Route(Guid.NewGuid());
        var anotherModel = Route(recovered.SiteId);
        await Fail(recovered, 402, "quota");
        await Fail(sameName, 402, "quota");
        await Fail(anotherModel, 402, "quota");

        _policy.OnAccountQuotaRecovered(recovered.SiteId);

        _store.IsBlocked(recovered.CircuitKey).Should().BeFalse();
        _store.IsBlocked(anotherModel.CircuitKey).Should().BeFalse();
        _store.IsBlocked(sameName.CircuitKey).Should().BeTrue();
        _store.GetAllCircuitStates()[sameName.CircuitKey].Meta.Should().Be(new CircuitRouteMeta("same-name", "model"));
    }

    [Fact]
    public async Task Recovery_preserves_pure_rate_limit_and_ordinary_failure_counts_and_blocks()
    {
        var siteId = Guid.NewGuid();
        var rate = Route(siteId);
        var counted = Route(siteId);
        var blocked = Route(siteId);
        await Fail(rate, 429, "rate limit, try again", 240);
        await Fail(counted, 500, "server error");
        for (var i = 0; i < 5; i++) await Fail(blocked, 500, "server error");
        var before = _store.GetAllCircuitStates();

        _policy.OnAccountQuotaRecovered(siteId);

        var after = _store.GetAllCircuitStates();
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach (var pair in before)
        {
            (after[pair.Key] with { RemainingTime = null }).Should()
                .Be(pair.Value with { RemainingTime = null });
        }
        _store.GetRest(rate.CircuitKey)!.Value.Reason.Should().Contain("限流");
        _store.GetAllCircuitStates()[counted.CircuitKey].FailureCount.Should().Be(1);
        _store.IsBlocked(blocked.CircuitKey).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Short_rate_limit_and_long_quota_rest_keep_independent_deadlines_and_metadata(bool quotaFirst)
    {
        var route = Route(Guid.NewGuid());
        if (quotaFirst)
        {
            await Fail(route, 402, "quota");
            await Fail(route, 429, "rate limit", 120);
        }
        else
        {
            await Fail(route, 429, "rate limit", 120);
            await Fail(route, 402, "quota");
        }

        _store.GetRest(route.CircuitKey)!.Value.Reason.Should().Contain("配额");
        _store.GetRest(route.CircuitKey)!.Value.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(29));
        _store.GetAllCircuitStates()[route.CircuitKey].RestReason.Should().Contain("配额");

        _policy.OnAccountQuotaRecovered(route.SiteId);

        var rest = _store.GetRest(route.CircuitKey)!.Value;
        rest.Reason.Should().Contain("限流");
        rest.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(1));
        rest.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(3));
        _store.GetAllCircuitStates()[route.CircuitKey].RestReason.Should().Contain("限流");
        _store.IsBlocked(route.CircuitKey).Should().BeTrue();
    }

    [Fact]
    public void Recovery_preserves_existing_ordinary_block_on_the_same_circuit_key()
    {
        var siteId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var meta = new CircuitRouteMeta("ordinary", "model");
        for (var i = 0; i < 5; i++) _store.Block(key, meta);
        var blockedUntil = _store.GetAllCircuitStates()[key].BlockedUntil;
        _store.Rest(key, DateTimeOffset.UtcNow.AddHours(6), "quota", meta, RouteRestKind.Quota, siteId);

        _policy.OnAccountQuotaRecovered(siteId);

        _store.GetRest(key).Should().BeNull();
        _store.IsBlocked(key).Should().BeTrue();
        _store.GetAllCircuitStates()[key].BlockedUntil.Should().Be(blockedUntil);
        _store.GetAllCircuitStates()[key].Meta.Should().Be(meta);
    }

    [Fact]
    public void Legacy_untyped_rest_and_unowned_quota_are_not_released_by_reason_text()
    {
        var siteId = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        var unowned = Guid.NewGuid();
        var other = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddHours(1);
        _store.Rest(legacy, until, "配额耗尽 quota");
        _store.Rest(unowned, until, "quota", kind: RouteRestKind.Quota);
        _store.Rest(other, until, "quota", kind: RouteRestKind.Other, siteId: siteId);

        _store.ReleaseQuotaRests(siteId).Should().Be(0);

        _store.IsBlocked(legacy).Should().BeTrue();
        _store.IsBlocked(unowned).Should().BeTrue();
        _store.IsBlocked(other).Should().BeTrue();
    }

    [Fact]
    public void Maximum_deadline_keeps_its_own_reason_and_metadata_then_reveals_remaining_rest()
    {
        var siteId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddHours(6);
        var quotaMeta = new CircuitRouteMeta("quota-site", "quota-model");
        var rateMeta = new CircuitRouteMeta("rate-site", "rate-model");
        _store.Rest(key, until, "long quota", quotaMeta, RouteRestKind.Quota, siteId);
        _store.Rest(key, until.AddHours(-1), "short quota", rateMeta, RouteRestKind.Quota, siteId);
        _store.Rest(key, until.AddHours(-5), "rate", rateMeta, RouteRestKind.RateLimit, siteId);

        var state = _store.GetAllCircuitStates()[key];
        state.BlockedUntil.Should().Be(until);
        state.RestReason.Should().Be("long quota");
        state.Meta.Should().Be(quotaMeta);

        _store.ReleaseQuotaRests(siteId).Should().Be(1);
        state = _store.GetAllCircuitStates()[key];
        state.BlockedUntil.Should().Be(until.AddHours(-5));
        state.RestReason.Should().Be("rate");
        state.Meta.Should().Be(rateMeta);
        _store.ReleaseQuotaRests(siteId).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_rest_writes_and_recovery_never_shorten_or_remove_rate_limit_rest()
    {
        var siteId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(1);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            _store.Rest(key, start.AddMinutes(i), $"rate-{i}", kind: RouteRestKind.RateLimit, siteId: siteId);
            _store.Rest(key, start.AddDays(1), "quota", kind: RouteRestKind.Quota, siteId: siteId);
            _store.ReleaseQuotaRests(siteId);
            _store.GetAllCircuitStates();
            _store.IsBlocked(key);
        })));
        _store.ReleaseQuotaRests(siteId);

        var rest = _store.GetRest(key)!.Value;
        rest.UntilUtc.Should().Be(start.AddMinutes(99));
        rest.Reason.Should().Be("rate-99");
        _store.GetAllCircuitStates()[key].BlockedUntil.Should().Be(rest.UntilUtc);
    }

    [Fact]
    public async Task Recovery_invalidates_no_key_oauth_reset_cache_before_next_failure()
    {
        var route = Route(Guid.NewGuid());
        _accounts.Reset = DateTimeOffset.UtcNow.AddHours(6);
        await Fail(route, 402, "quota");
        _store.GetRest(route.CircuitKey)!.Value.UntilUtc.Should().Be(_accounts.Reset);
        _accounts.LookupCount.Should().Be(1);

        _accounts.Reset = DateTimeOffset.UtcNow.AddHours(1);
        _policy.OnAccountQuotaRecovered(route.SiteId);
        await Fail(route, 402, "quota");

        _store.GetRest(route.CircuitKey)!.Value.UntilUtc.Should().Be(_accounts.Reset);
        _accounts.LookupCount.Should().Be(2, "recovery must invalidate the stale six-hour site cache");
    }

    [Fact]
    public void Combined_deadline_includes_ordinary_block_without_extending_short_rest_storage()
    {
        var key = Guid.NewGuid();
        for (var i = 0; i < 5; i++) _store.Block(key);
        var blockedUntil = _store.GetAllCircuitStates()[key].BlockedUntil;
        _store.Rest(key, DateTimeOffset.UtcNow.AddSeconds(10), "short rate", kind: RouteRestKind.RateLimit);

        _store.GetRest(key)!.Value.UntilUtc.Should().Be(blockedUntil);
        _store.GetAllCircuitStates()[key].BlockedUntil.Should().Be(blockedUntil);
        _store.Reset(key).Should().BeTrue();
        _store.GetAllCircuitStates().Should().BeEmpty();
    }

    private static CachedProxyRouteTarget Route(Guid siteId) => new() { SiteId = siteId, CircuitKey = Guid.NewGuid() };

    private Task Fail(CachedProxyRouteTarget route, int status, string body, int? retryAfter = null)
        => _policy.RecordRouteFailureAsync(route, status, body, retryAfter, true,
            new CircuitRouteMeta("same-name", "model"), CancellationToken.None);

    public void Dispose()
    {
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); } catch { }
        }
    }

    private sealed class CachedAccountProvider : IAccountQuotaProvider
    {
        public string ProviderKey => "test-cache-only";
        public DateTimeOffset Reset { get; set; } = DateTimeOffset.UtcNow.AddHours(6);
        public int LookupCount { get; private set; }

        public Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AccountQuotaTarget>>([]);

        public Task<IReadOnlyList<AccountQuotaTarget>> FindByLinkedSiteIdsAsync(IReadOnlyList<Guid> siteIds, CancellationToken cancellationToken)
        {
            LookupCount++;
            return Task.FromResult<IReadOnlyList<AccountQuotaTarget>>(siteIds.Select(siteId => new AccountQuotaTarget
            {
                ProviderKey = ProviderKey, AccountId = siteId, LinkedSiteId = siteId,
                DisplayName = "same-name", IsEnabled = true, LastQuotaRawJson = "cached-only",
            }).ToList());
        }

        public AccountQuotaSnapshot? ParseCachedQuota(string rawJson) => new()
        {
            Success = true,
            Windows = [new AccountQuotaWindow { Id = "quota", Label = "quota", UsedPercent = 50, ResetAtUtc = Reset }],
        };

        public Task<AccountQuotaSnapshot> QueryAsync(AccountQuotaTarget account, bool forceRefresh, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network queries are forbidden in this test.");
        public Task SetEnabledAsync(AccountQuotaTarget account, bool enabled, string reason, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
