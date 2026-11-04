using System.Net;
using AITool.Application.Accounts;
using AITool.Application.Codex;
using AITool.Application.OAuth;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.OAuth;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Services;

/// <summary>Isolated SQLite databases and fake network only; no production providers are contacted.</summary>
public sealed class ProviderQuotaRecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-provider-recovery-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly ScheduleSpy _schedule = new();
    private readonly FakeNetwork _network = new();
    private readonly HttpClient _http;
    private readonly FakeVendor _vendor = new();
    private readonly ProxyRequestMetadataCache _metadata;
    private bool _realRefresh;

    public ProviderQuotaRecoveryTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_path}");
        services.AddScoped<ProxyRequestMetadataCache>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _metadata = _scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        _db.Client.Insertable(new SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = true, OAuthAutoDisableThresholdPercent = 90 }).ExecuteCommand();
        _http = new HttpClient(_network);
    }

    public static IEnumerable<object[]> Providers() => new[] { "codex", "google", "kimi", "xai", "device-oauth" }.Select(p => new object[] { p });
    [Theory, MemberData(nameof(Providers))]
    public async Task Recovery_requires_current_unexpired_lease_for_account_and_site(string key)
    {
        var target = await SeedAsync(key);
        var token = Guid.NewGuid();
        target.QuotaRecheckLeaseToken = token;
        var row = new QuotaRecheckSchedule
        {
            ProviderKey = key, AccountId = target.AccountId, Generation = token.ToString("D"),
            LeaseToken = token.ToString("D"), LeaseExpiresAtUtcTicks = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks
        };
        await _db.Client.Insertable(row).ExecuteCommandAsync();
        var provider = Create(key);
        await provider.SetEnabledAsync(target, true, "quota-recovered", default);
        (await EnabledAsync(key)).Should().BeFalse("an expired claim cannot write even before reassignment");
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();

        row.LeaseToken = Guid.NewGuid().ToString("D");
        row.Generation = row.LeaseToken;
        row.LeaseExpiresAtUtcTicks = DateTimeOffset.UtcNow.AddMinutes(10).UtcTicks;
        await _db.Client.Updateable(row).ExecuteCommandAsync();
        await provider.SetEnabledAsync(target, true, "quota-recovered", default);
        (await EnabledAsync(key)).Should().BeFalse("a new owner fences out the old claim");
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();

        target.QuotaRecheckLeaseToken = Guid.Parse(row.LeaseToken);
        row.Generation = Guid.NewGuid().ToString("D");
        await _db.Client.Updateable(row).ExecuteCommandAsync();
        await provider.SetEnabledAsync(target, true, "quota-recovered", default);
        (await EnabledAsync(key)).Should().BeFalse("a newer disable event invalidates the old observation");
        row.Generation = row.LeaseToken;
        await _db.Client.Updateable(row).ExecuteCommandAsync();
        await provider.SetEnabledAsync(target, true, "quota-recovered", default);
        (await EnabledAsync(key)).Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    public static IEnumerable<object[]> RecoveryBlockers()
    {
        foreach (var row in Providers())
        {
            var p = (string)row[0];
            foreach (var flag in new[] { "ManuallyDisabled", "DisabledByFeatureToggle", "global-off", "missing" }) yield return new object[] { p, flag };
            if (p is "codex" or "google") yield return new object[] { p, "IsQuotaCooling" };
            if (p == "google") yield return new object[] { p, "DisabledByUpstream" };
            if (p is "kimi" or "xai" or "device-oauth") yield return new object[] { p, "IsDeleted" };
            yield return new object[] { p, "RequiresReauth" };
        }
    }

    [Theory, MemberData(nameof(RecoveryBlockers))]
    public async Task Recovery_rechecks_flags_changed_after_target_read(string key, string blocker)
    {
        var target = await SeedAsync(key);
        // Prime stale metadata, then mutate the real DB, just as a concurrent management request would.
        (await _metadata.GetRuntimeSettingsAsync(default)).OAuthFeaturesEnabled.Should().BeTrue();
        if (blocker == "global-off") await _db.Client.Updateable<SystemRuntimeSettings>()
            .SetColumns(s => new SystemRuntimeSettings { OAuthFeaturesEnabled = false }).Where(s => s.Id == 1).ExecuteCommandAsync();
        else if (blocker == "missing") _db.Client.Ado.ExecuteCommand($"DELETE FROM {Table(key)}");
        else _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET {blocker}=1");

        await Create(key).SetEnabledAsync(target, true, "quota-recovered", default);

        if (blocker != "missing")
        {
            (await EnabledAsync(key)).Should().BeFalse();
            if (blocker != "global-off") _db.Client.Ado.GetInt($"SELECT {blocker} FROM {Table(key)}").Should().Be(1);
        }
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        _schedule.Calls.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Feature_restore_clears_reauth_marker_only_while_global_feature_is_enabled(string key)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RequiresReauth=1, DisabledByFeatureToggle=1");
        await _db.Client.Updateable<SystemRuntimeSettings>()
            .SetColumns(s => new SystemRuntimeSettings { OAuthFeaturesEnabled = false }).Where(s => s.Id == 1).ExecuteCommandAsync();

        var provider = Create(key);
        await provider.ApplyFeatureToggleAsync(true, default);
        _db.Client.Ado.GetInt($"SELECT DisabledByFeatureToggle FROM {Table(key)}").Should().Be(1);
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();

        await _db.Client.Updateable<SystemRuntimeSettings>()
            .SetColumns(s => new SystemRuntimeSettings { OAuthFeaturesEnabled = true }).Where(s => s.Id == 1).ExecuteCommandAsync();
        await provider.ApplyFeatureToggleAsync(true, default);
        _db.Client.Ado.GetInt($"SELECT DisabledByFeatureToggle FROM {Table(key)}").Should().Be(0);
        (await EnabledAsync(key)).Should().BeFalse("reauthentication is still required");
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Recovery_uses_current_link_and_preserves_credentials(string key)
    {
        var stale = await SeedAsync(key);
        var currentSite = new Site { Name = "new link", IsEnabled = false, ApiKey = "new-site-token", ExtraHeadersJson = "{\"fresh\":true}" };
        await _db.Client.Insertable(currentSite).ExecuteCommandAsync();
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET LinkedSiteId=@siteId, AccessToken='fresh-token', RefreshToken='fresh-refresh'", new { siteId = currentSite.Id });

        await Create(key).SetEnabledAsync(stale, true, "quota-recovered", default);

        (await EnabledAsync(key)).Should().BeTrue();
        var site = await _db.Sites.InSingleAsync(currentSite.Id);
        site.IsEnabled.Should().BeTrue();
        site.ApiKey.Should().Be("new-site-token");
        site.ExtraHeadersJson.Should().Be("{\"fresh\":true}");
        (await _db.Sites.InSingleAsync(stale.LinkedSiteId)).IsEnabled.Should().BeFalse();
        _db.Client.Ado.GetString($"SELECT AccessToken FROM {Table(key)}").Should().Be("fresh-token");
        _db.Client.Ado.GetString($"SELECT RefreshToken FROM {Table(key)}").Should().Be("fresh-refresh");
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Recovery_and_site_update_roll_back_together(string key)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_site BEFORE UPDATE OF IsEnabled ON Sites BEGIN SELECT RAISE(ABORT, 'injected site failure'); END;");
        Func<Task> action = () => Create(key).SetEnabledAsync(target, true, "quota-recovered", default);
        await action.Should().ThrowAsync<Exception>();
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Threshold_disable_schedules_with_persisted_last_success_and_is_idempotent(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        var provider = Create(key);
        await provider.SetEnabledAsync(target, false, "quota-threshold", default);
        await provider.SetEnabledAsync(target, false, "quota-threshold", default);
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        _schedule.Calls.Should().HaveCount(2);
        _schedule.Calls.Should().OnlyContain(c => c.Key == key && c.Id == target.AccountId);
        _schedule.Scheduled.Should().HaveCount(1, "store EnsureScheduled is idempotent");
        _schedule.Scheduled.Should().Contain((key, target.AccountId));
    }

    [Theory, MemberData(nameof(RecoveryBlockers))]
    public async Task Threshold_disable_does_not_schedule_other_disable_reasons(string key, string blocker)
    {
        if (blocker is "global-off" or "missing") return;
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET {blocker}=1");
        await Create(key).SetEnabledAsync(target, false, "quota-threshold", default);
        _schedule.Calls.Should().BeEmpty();
        _db.Client.Ado.GetInt($"SELECT {blocker} FROM {Table(key)}").Should().Be(1);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Non_threshold_reasons_never_schedule(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        var provider = Create(key);
        foreach (var reason in new[] { "manual", "feature-toggle-off", "upstream", "cooldown" })
            await provider.SetEnabledAsync(target, false, reason, default);
        _schedule.Calls.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Successful_queries_while_disabled_do_not_reschedule(string key)
    {
        var target = await SeedAsync(key);
        _network.Key = key;
        var provider = Create(key);
        (await provider.QueryAsync(target, true, default)).Success.Should().BeTrue();
        (await provider.QueryAsync(target, true, default)).Success.Should().BeTrue();
        _schedule.Calls.Should().BeEmpty();
        (await EnabledAsync(key)).Should().BeFalse();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Real_threshold_query_schedules_local_disable(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _network.Key = key;
        (await Create(key).QueryAsync(target, true, default)).Success.Should().BeTrue();
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        _schedule.Calls.Should().ContainSingle(c => c.Key == key && c.Id == target.AccountId);
        _schedule.Scheduled.Should().Contain((key, target.AccountId));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Query_threshold_after_manual_disable_preserves_manual_flag_and_tokens(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _network.Key = key;
        _network.BeforeReply = () => _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET IsEnabled=0, ManuallyDisabled=1, AccessToken='fresh-token'; UPDATE Sites SET IsEnabled=0, ApiKey='fresh-site-token';");
        (await Create(key).QueryAsync(target, true, default)).Success.Should().BeTrue();
        (await EnabledAsync(key)).Should().BeFalse();
        _db.Client.Ado.GetInt($"SELECT ManuallyDisabled FROM {Table(key)}").Should().Be(1);
        _db.Client.Ado.GetString($"SELECT AccessToken FROM {Table(key)}").Should().Be("fresh-token");
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).ApiKey.Should().Be("fresh-site-token");
        _schedule.Calls.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Concurrent_manual_disable_wins_without_clearing_manual_intent(string key)
    {
        var target = await SeedAsync(key);
        var provider = Create(key);
        using var start = new ManualResetEventSlim();
        var manual = Task.Run(() =>
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout=5000; BEGIN IMMEDIATE; UPDATE {Table(key)} SET IsEnabled=0, ManuallyDisabled=1; UPDATE Sites SET IsEnabled=0; COMMIT;";
            start.Wait();
            command.ExecuteNonQuery();
        });
        var recovery = Task.Run(async () =>
        {
            start.Wait();
            await provider.SetEnabledAsync(target, true, "quota-recovered", default);
        });
        start.Set();
        await Task.WhenAll(manual, recovery);
        (await EnabledAsync(key)).Should().BeFalse();
        _db.Client.Ado.GetInt($"SELECT ManuallyDisabled FROM {Table(key)}").Should().Be(1);
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Committed_disable_schedules_even_when_request_is_canceled(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        using var request = new CancellationTokenSource();
        _schedule.BeforeEnsure = token =>
        {
            // Store invocation is after account/site commit. A canceled HTTP request must not
            // cancel the independent, bounded schedule write.
            request.Cancel();
            token.IsCancellationRequested.Should().BeFalse();
            token.CanBeCanceled.Should().BeTrue();
            (EnabledAsync(key).GetAwaiter().GetResult()).Should().BeFalse();
            ( _db.Sites.InSingle(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        };
        await Create(key).SetEnabledAsync(target, false, "quota-threshold", request.Token);
        request.IsCancellationRequested.Should().BeTrue();
        _schedule.Scheduled.Should().ContainSingle();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Schedule_failure_does_not_undo_committed_disable(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _schedule.BeforeEnsure = _ => throw new InvalidOperationException("injected schedule failure");
        await Create(key).SetEnabledAsync(target, false, "quota-threshold", default);
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        _schedule.Scheduled.Should().BeEmpty("coordinator reconciliation repairs missing schedule");
    }

    [Fact]
    public async Task Token_ignoring_schedule_store_cannot_hold_committed_disable_open()
    {
        var target = await SeedAsync("codex", enabled: true);
        var unfinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _schedule.EnsureResult = unfinished.Task;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Create("codex").SetEnabledAsync(target, false, "quota-threshold", default)
            .WaitAsync(TimeSpan.FromSeconds(10));
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(9));
        elapsed.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4));
        unfinished.Task.IsCompleted.Should().BeFalse("store ignored cancellation and never finished");
        (await EnabledAsync("codex")).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        // A fault after timeout must be observed without changing the completed provider result.
        unfinished.SetException(new InvalidOperationException("late schedule failure"));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Forced_quota_401_evicts_previous_success_for_next_non_forced_query(string key)
    {
        // A disabled account avoids quota-threshold side effects while exercising the same cache.
        var target = await SeedAsync(key);
        var provider = Create(key);
        var cache = _services.GetRequiredService<IMemoryCache>();
        var cacheKey = $"{key}-quota-{target.AccountId:N}";
        _network.Key = key;

        (await provider.QueryAsync(target, true, default)).Success.Should().BeTrue();
        cache.TryGetValue(cacheKey, out object? _).Should().BeTrue();
        (await provider.QueryAsync(target, false, default)).Success.Should().BeTrue();

        _network.Status = HttpStatusCode.Unauthorized;
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true, AccessTokenUnauthorized = true };
        var rejected = await provider.QueryAsync(target, true, default);
        rejected.Success.Should().BeFalse();
        rejected.UpstreamUnauthorized.Should().BeTrue();
        cache.TryGetValue(cacheKey, out object? _).Should().BeFalse();

        (await provider.QueryAsync(target, false, default)).Success.Should().BeFalse("the previous successful result was evicted");
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    public async Task Quota_stalled_401_body_preserves_invalidation(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _network.Key = key;
        _network.Status = HttpStatusCode.Unauthorized;
        _network.StalledBody = true;
        var result = await Create(key).QueryAsync(target, true, default).WaitAsync(TimeSpan.FromSeconds(5));
        result.UpstreamUnauthorized.Should().BeTrue();
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Quota_unreadable_401_body_preserves_invalidation(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _network.Key = key;
        _network.Status = HttpStatusCode.Unauthorized;
        _network.BrokenBody = true;
        var result = await Create(key).QueryAsync(target, true, default);
        result.UpstreamUnauthorized.Should().BeTrue();
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("device-oauth")]
    public async Task Quota_real_401_disables_only_matching_credential(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _network.Key = key;
        _network.Status = HttpStatusCode.Unauthorized;
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true, AccessTokenUnauthorized = true };
        var result = await Create(key).QueryAsync(target, true, default);
        result.Success.Should().BeFalse();
        result.UpstreamUnauthorized.Should().BeTrue();
        _db.Client.Ado.GetInt($"SELECT RequiresReauth FROM {Table(key)}").Should().Be(1);
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        await Create(key).SetEnabledAsync(target, true, "quota-recovered", default);
        (await EnabledAsync(key)).Should().BeFalse();
        _schedule.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("device-oauth")]
    public async Task Quota_401_preserves_matching_credential_when_refresh_fails(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RefreshToken='refresh'");
        _network.Key = key;
        _network.Status = HttpStatusCode.Unauthorized;
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true, AccessTokenUnauthorized = true };
        var result = await Create(key).QueryAsync(target, true, default);
        result.UpstreamUnauthorized.Should().BeTrue();
        _db.Client.Ado.GetInt($"SELECT RequiresReauth FROM {Table(key)}").Should().Be(0);
        (await EnabledAsync(key)).Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    public static IEnumerable<object[]> RefreshFailures() =>
        from key in new[] { "codex", "google", "kimi", "device-oauth" }
        from failure in new[] { "refused", "server", "parse", "database" }
        select new object[] { key, failure };

    [Theory, MemberData(nameof(RefreshFailures))]
    public async Task Quota_refresh_only_invalidates_explicit_refusal(string key, string failure)
    {
        var target = await SeedAsync(key, enabled: true);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RefreshToken='refresh'");
        if (key == "device-oauth")
            _db.Client.Ado.ExecuteCommand("UPDATE Sites SET ManagedSource=@source", new { source = DeviceOAuthAccountProvisioner.ManagedSource });
        _realRefresh = true;
        _network.Key = key;
        _network.Status = HttpStatusCode.Unauthorized;
        _network.RefreshStatus = failure == "refused" ? HttpStatusCode.BadRequest : failure == "server" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
        _network.RefreshBody = failure == "refused" ? "{\"error\":\"invalid_grant\"}" : failure == "parse" ? "not-json" : "{\"access_token\":\"new-token\",\"refresh_token\":\"new-refresh\"}";
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true, AccessTokenUnauthorized = true };
        _vendor.RefreshError = failure switch
        {
            "refused" => new DeviceOAuthRefreshRefusedException("invalid_grant"),
            "server" => new InvalidOperationException("HTTP 503"),
            "parse" => new System.Text.Json.JsonException("bad JSON"),
            _ => null,
        };
        if (failure == "database")
            _db.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_rotation BEFORE UPDATE OF AccessToken ON {Table(key)} BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;");

        var result = await Create(key).QueryAsync(target, true, default);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        var refused = failure == "refused";
        _db.Client.Ado.GetInt($"SELECT RequiresReauth FROM {Table(key)}").Should().Be(refused ? 1 : 0);
        (await EnabledAsync(key)).Should().Be(!refused);
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().Be(!refused);
        _db.Client.Ado.GetString($"SELECT AccessToken FROM {Table(key)}").Should().Be("token");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}", true)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"refresh_token_reused\"}}", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"error\":\"invalid_grant\"}", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":\"invalid_grant\"}", false)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\"}", false)]
    [InlineData(HttpStatusCode.BadRequest, "not-json invalid_grant", false)]
    public void Refresh_rejection_requires_explicit_credential_error(HttpStatusCode status, string body, bool refused)
    {
        OAuthRefreshRefusedException.IsCredentialRejection(status, body).Should().Be(refused);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("device-oauth")]
    public async Task Quota_401_refresh_failure_cannot_disable_rotated_credential(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RefreshToken='refresh'");
        _network.Key = key;
        _network.Status = HttpStatusCode.Unauthorized;
        Action rotate = () => _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET AccessToken='new-token'");
        _network.BeforeReply = rotate;
        _vendor.BeforeReply = rotate;
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true, AccessTokenUnauthorized = true };
        var result = await Create(key).QueryAsync(target, true, default);
        result.UpstreamUnauthorized.Should().BeTrue();
        _db.Client.Ado.GetInt($"SELECT RequiresReauth FROM {Table(key)}").Should().Be(0);
        (await EnabledAsync(key)).Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("device-oauth")]
    public async Task Quota_old_401_cannot_disable_rotated_credentials(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _network.Key = key;
        _network.Status = HttpStatusCode.Unauthorized;
        Action rotate = () => _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RefreshToken='rotated'");
        _network.BeforeReply = rotate;
        _vendor.BeforeReply = rotate;
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true, AccessTokenUnauthorized = true };
        var result = await Create(key).QueryAsync(target, true, default);
        result.UpstreamUnauthorized.Should().BeTrue();
        _db.Client.Ado.GetInt($"SELECT RequiresReauth FROM {Table(key)}").Should().Be(0);
        (await EnabledAsync(key)).Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Quota_proxy_407_is_not_upstream_401(string key)
    {
        var target = await SeedAsync(key, enabled: true);
        _network.Key = key;
        _network.Status = HttpStatusCode.ProxyAuthenticationRequired;
        var result = await Create(key).QueryAsync(target, true, default);
        result.Success.Should().BeFalse();
        result.UpstreamUnauthorized.Should().BeFalse();
        _db.Client.Ado.GetInt($"SELECT RequiresReauth FROM {Table(key)}").Should().Be(0);
        (await EnabledAsync(key)).Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Device_quota_derived_credential_401_does_not_disable_access_token()
    {
        var target = await SeedAsync("device-oauth", enabled: true);
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true };
        var result = await Create("device-oauth").QueryAsync(target, true, default);
        result.UpstreamUnauthorized.Should().BeTrue();
        _db.Client.Ado.GetInt("SELECT RequiresReauth FROM DeviceOAuthAccounts").Should().Be(0);
        (await EnabledAsync("device-oauth")).Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Device_quota_derived_credential_401_with_refused_refresh_invalidates_snapshot()
    {
        var target = await SeedAsync("device-oauth", enabled: true);
        _db.Client.Ado.ExecuteCommand("UPDATE DeviceOAuthAccounts SET RefreshToken='refresh'");
        _db.Client.Ado.ExecuteCommand("UPDATE Sites SET ManagedSource=@source", new { source = DeviceOAuthAccountProvisioner.ManagedSource });
        _realRefresh = true;
        _vendor.Result = new DeviceOAuthQuotaResult { CredentialInvalid = true, UpstreamUnauthorized = true };
        _vendor.RefreshError = new DeviceOAuthRefreshRefusedException("invalid_grant");
        var result = await Create("device-oauth").QueryAsync(target, true, default);
        result.UpstreamUnauthorized.Should().BeTrue();
        _db.Client.Ado.GetInt("SELECT RequiresReauth FROM DeviceOAuthAccounts").Should().Be(1);
        (await EnabledAsync("device-oauth")).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Xai_quota_401_disables_account_and_linked_site_with_relogin_message()
    {
        var target = await SeedAsync("xai", enabled: true);
        _network.Key = "xai";
        _network.Status = HttpStatusCode.Unauthorized;
        var provider = Create("xai");
        var result = await provider.QueryAsync(target, true, default);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("重新登录");
        var saved = await _db.XaiAccounts.InSingleAsync(target.AccountId);
        saved.RequiresReauth.Should().BeTrue();
        saved.IsEnabled.Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
        (await provider.GetAccountsAsync(default)).Single().DisabledByUpstream.Should().BeTrue();
        await provider.SetEnabledAsync(target, true, "quota-recovered", default);
        (await EnabledAsync("xai")).Should().BeFalse();
        _schedule.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Xai_quota_non_401_does_not_disable(HttpStatusCode status)
    {
        var target = await SeedAsync("xai", enabled: true);
        _network.Key = "xai";
        _network.Status = status;
        var result = await Create("xai").QueryAsync(target, true, default);
        result.Success.Should().BeFalse();
        var saved = await _db.XaiAccounts.InSingleAsync(target.AccountId);
        saved.RequiresReauth.Should().BeFalse();
        saved.IsEnabled.Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Xai_quota_old_401_cannot_disable_new_credentials()
    {
        var target = await SeedAsync("xai", enabled: true);
        _network.Key = "xai";
        _network.Status = HttpStatusCode.Unauthorized;
        _network.BeforeReply = () => _db.Client.Ado.ExecuteCommand("UPDATE XaiAccounts SET AccessToken='new-token', RefreshToken='new-refresh'; UPDATE Sites SET ApiKey='new-token';");
        var result = await Create("xai").QueryAsync(target, true, default);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("凭证已更新");
        var saved = await _db.XaiAccounts.InSingleAsync(target.AccountId);
        saved.AccessToken.Should().Be("new-token");
        saved.RequiresReauth.Should().BeFalse();
        saved.IsEnabled.Should().BeTrue();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("xai")]
    [InlineData("device-oauth")]
    public async Task Reauthentication_flag_is_not_eligible_for_quota_recovery(string key)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RequiresReauth=1, DisabledByFeatureToggle=1");
        var provider = Create(key);
        (await provider.FindByLinkedSiteIdsAsync([target.LinkedSiteId], default)).Single().DisabledByUpstream.Should().BeTrue();
        await provider.SetEnabledAsync(target, true, "feature-toggle-on", default);
        await provider.ApplyFeatureToggleAsync(true, default);
        (await EnabledAsync(key)).Should().BeFalse();
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    public static IEnumerable<object[]> EnablePaths() =>
        from provider in Providers()
        from path in new[] { "manual", "feature-toggle-on", "feature-bulk" }
        select new object[] { provider[0], path };

    [Theory, MemberData(nameof(EnablePaths))]
    public async Task Enable_does_not_restore_reauthentication_required_account(string key, string path)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RequiresReauth=1, DisabledByFeatureToggle=1");
        await EnableAsync(key, path, target);
        (await EnabledAsync(key)).Should().BeFalse();
        _db.Client.Ado.GetInt($"SELECT DisabledByFeatureToggle FROM {Table(key)}").Should().Be(path == "feature-bulk" ? 0 : 1);
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Reauthentication_is_exposed_as_upstream_disabled(string key)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET RequiresReauth=1");
        (await Create(key).FindByLinkedSiteIdsAsync([target.LinkedSiteId], default)).Single()
            .DisabledByUpstream.Should().BeTrue();
    }

    [Theory, MemberData(nameof(EnablePaths))]
    public async Task Enable_and_linked_site_roll_back_together(string key, string path)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET DisabledByFeatureToggle=1");
        _db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_enable_site BEFORE UPDATE OF IsEnabled ON Sites BEGIN SELECT RAISE(ABORT, 'injected site failure'); END;");
        Func<Task> action = () => EnableAsync(key, path, target);
        await action.Should().ThrowAsync<Exception>();
        (await EnabledAsync(key)).Should().BeFalse();
        _db.Client.Ado.GetInt($"SELECT DisabledByFeatureToggle FROM {Table(key)}").Should().Be(1);
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory, MemberData(nameof(EnablePaths))]
    public async Task Enable_serializes_with_inflight_invalidation(string key, string path)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET DisabledByFeatureToggle=1");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout=5000; BEGIN IMMEDIATE; UPDATE {Table(key)} SET RequiresReauth=1, IsEnabled=0; UPDATE Sites SET IsEnabled=0;";
        command.ExecuteNonQuery();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enable = Task.Run(async () =>
        {
            started.SetResult();
            await EnableAsync(key, path, target);
        });
        try
        {
            await started.Task;
            await Task.Delay(200);
        }
        finally
        {
            command.CommandText = "COMMIT";
            command.ExecuteNonQuery();
        }
        await enable;
        (await EnabledAsync(key)).Should().BeFalse();
        _db.Client.Ado.GetInt($"SELECT RequiresReauth FROM {Table(key)}").Should().Be(1);
        _db.Client.Ado.GetInt($"SELECT DisabledByFeatureToggle FROM {Table(key)}").Should().Be(path == "feature-bulk" ? 0 : 1);
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().BeFalse();
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Feature_restore_preserves_provider_specific_manual_and_upstream_rules(string key)
    {
        var target = await SeedAsync(key);
        _db.Client.Ado.ExecuteCommand($"UPDATE {Table(key)} SET DisabledByFeatureToggle=1, ManuallyDisabled=1" +
            (key == "google" ? ", DisabledByUpstream=1" : ""));
        await Create(key).ApplyFeatureToggleAsync(true, default);
        var restored = key == "codex";
        (await EnabledAsync(key)).Should().Be(restored);
        (await _db.Sites.InSingleAsync(target.LinkedSiteId)).IsEnabled.Should().Be(restored);
        _db.Client.Ado.GetInt($"SELECT ManuallyDisabled FROM {Table(key)}").Should().Be(1);
        _db.Client.Ado.GetInt($"SELECT DisabledByFeatureToggle FROM {Table(key)}").Should().Be(restored ? 0 : 1);
    }

    private Task EnableAsync(string key, string path, AccountQuotaTarget target) => path == "feature-bulk"
        ? Create(key).ApplyFeatureToggleAsync(true, default)
        : Create(key).SetEnabledAsync(target, true, path, default);

    private IAccountQuotaProvider Create(string key)
    {
        var cache = _services.GetRequiredService<IMemoryCache>();
        return key switch
        {
            "codex" => new CodexQuotaService(_http, _db, _metadata, cache, _realRefresh ? new CodexCredentialRefreshService(_db, new AITool.Infrastructure.Codex.CodexOAuthClient(_http), _metadata, NullLogger<CodexCredentialRefreshService>.Instance) : null!, new FakeVersion(), NullLogger<CodexQuotaService>.Instance, _schedule),
            "google" => new GoogleAccountQuotaService(_http, _db, _metadata, cache, _realRefresh ? new GoogleCredentialRefreshService(_db, new AITool.Infrastructure.Google.GoogleOAuthClient(_http), _metadata, NullLogger<GoogleCredentialRefreshService>.Instance) : null!, NullLogger<GoogleAccountQuotaService>.Instance, _schedule),
            "kimi" => new KimiQuotaService(_http, _db, _metadata, cache, _realRefresh ? new KimiCredentialRefreshService(_db, new AITool.Infrastructure.Kimi.KimiOAuthClient(_http, NullLogger<AITool.Infrastructure.Kimi.KimiOAuthClient>.Instance), _metadata, NullLogger<KimiCredentialRefreshService>.Instance) : null!, NullLogger<KimiQuotaService>.Instance, _schedule),
            "xai" => new XaiQuotaService(_http, _db, _metadata, cache, null!, NullLogger<XaiQuotaService>.Instance, _schedule),
            "device-oauth" => new DeviceOAuthQuotaService(_db, new DeviceOAuthVendorRegistry([_vendor]), _metadata, cache, _realRefresh ? new DeviceOAuthCredentialRefreshService(_db, new DeviceOAuthVendorRegistry([_vendor]), _metadata, NullLogger<DeviceOAuthCredentialRefreshService>.Instance) : null!, NullLogger<DeviceOAuthQuotaService>.Instance, _schedule),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }

    private async Task<AccountQuotaTarget> SeedAsync(string key, bool enabled = false)
    {
        var site = new Site { Name = "quota site", IsEnabled = enabled, ApiKey = "site-token" };
        await _db.Client.Insertable(site).ExecuteCommandAsync();
        var id = Guid.NewGuid();
        var last = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        switch (key)
        {
            case "codex": await _db.Client.Insertable(new CodexAccount { Id = id, LinkedSiteId = site.Id, IsEnabled = enabled, AccessToken = "token", LastQuotaCheckedAt = last }).ExecuteCommandAsync(); break;
            case "google": await _db.Client.Insertable(new GoogleAccount { Id = id, LinkedSiteId = site.Id, IsEnabled = enabled, AccessToken = "token", LastQuotaCheckedAt = last }).ExecuteCommandAsync(); break;
            case "kimi": await _db.Client.Insertable(new KimiAccount { Id = id, LinkedSiteId = site.Id, IsEnabled = enabled, AccessToken = "token", LastQuotaCheckedAt = last }).ExecuteCommandAsync(); break;
            case "xai": await _db.Client.Insertable(new XaiAccount { Id = id, LinkedSiteId = site.Id, IsEnabled = enabled, AccessToken = "token", LastQuotaCheckedAt = last }).ExecuteCommandAsync(); break;
            case "device-oauth": await _db.Client.Insertable(new DeviceOAuthAccount { Id = id, VendorKey = "fake", LinkedSiteId = site.Id, IsEnabled = enabled, AccessToken = "token", LastQuotaCheckedAt = last }).ExecuteCommandAsync(); break;
        }
        return (await Create(key).GetAccountsAsync(default)).Single(a => a.AccountId == id);
    }

    private static string Table(string key) => key switch { "codex" => "CodexAccounts", "google" => "GoogleAccounts", "kimi" => "KimiAccounts", "xai" => "XaiAccounts", "device-oauth" => "DeviceOAuthAccounts", _ => throw new ArgumentOutOfRangeException(nameof(key)) };
    private Task<bool> EnabledAsync(string key) => Task.FromResult(_db.Client.Ado.GetInt($"SELECT IsEnabled FROM {Table(key)}") == 1);

    public void Dispose()
    {
        _http.Dispose();
        _scope.Dispose();
        _services.Dispose();
        try { File.Delete(_path); File.Delete(_path + "-wal"); File.Delete(_path + "-shm"); } catch (IOException) { }
    }

    private sealed class ScheduleSpy : IQuotaRecheckScheduleStore
    {
        public List<(string Key, Guid Id)> Calls { get; } = [];
        public HashSet<(string Key, Guid Id)> Scheduled { get; } = [];
        public Action<CancellationToken>? BeforeEnsure { get; set; }
        public Task? EnsureResult { get; set; }
        public Task EnsureScheduledAsync(string providerKey, Guid accountId, DateTimeOffset? lastQuotaCheckedAt, CancellationToken cancellationToken)
        {
            BeforeEnsure?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (EnsureResult is not null) return EnsureResult;
            Calls.Add((providerKey, accountId)); Scheduled.Add((providerKey, accountId)); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<QuotaRecheckScheduleInfo>> ReconcileAsync(string providerKey, IReadOnlyList<AccountQuotaTarget> eligibleAccounts, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuotaRecheckLease?> TryAcquireAsync(string providerKey, Guid accountId, CancellationToken cancellationToken, bool force = false) => throw new NotSupportedException();
        public Task CompleteAsync(QuotaRecheckLease lease, bool keepScheduled, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeVersion : ICodexClientVersionResolver
    {
        public Task<CodexClientVersionInfo> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CodexClientVersionInfo { ClientVersion = "1.0.0", UserAgent = "test/1.0.0" });
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(new IOException("broken response body"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class StalledContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.Infinite);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class FakeNetwork : HttpMessageHandler
    {
        public string Key { get; set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool BrokenBody { get; set; }
        public bool StalledBody { get; set; }
        public Action? BeforeReply { get; set; }
        public HttpStatusCode RefreshStatus { get; set; } = HttpStatusCode.OK;
        public string RefreshBody { get; set; } = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("token", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(RefreshStatus) { Content = new StringContent(RefreshBody) });
            BeforeReply?.Invoke();
            if (StalledBody)
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StalledContent() });
            if (BrokenBody)
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new BrokenContent() });
            HttpContent content = Key switch
            {
                "codex" => new StringContent("""{"rate_limit":{"primary_window":{"used_percent":99,"limit_window_seconds":18000}}}"""),
                "google" => new StringContent("""{"models":{"gemini-test":{"quotaInfo":{"remainingFraction":0.01}}}}"""),
                "kimi" => new StringContent("""{"usage":{"limit":"100","used":"99"}}"""),
                "xai" => new ByteArrayContent([0, 0, 0, 0, 7, 10, 5, 13, .. BitConverter.GetBytes(99f)]),
                _ => throw new InvalidOperationException("Unexpected fake network request"),
            };
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
        }
    }

    private sealed class FakeVendor : IDeviceOAuthVendor
    {
        public string VendorKey => "fake";
        public string Label => "Fake";
        public DeviceOAuthChatSiteConfig? ChatSiteConfig => null;
        public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => [];
        public DeviceOAuthQuotaResult? Result { get; set; }
        public Exception? RefreshError { get; set; }
        public Action? BeforeReply { get; set; }
        public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
        {
            BeforeReply?.Invoke();
            return Task.FromResult(Result ?? new DeviceOAuthQuotaResult { Success = true, RawJson = "{}", Windows = [new AccountQuotaWindow { Id = "test", UsedPercent = 99 }] });
        }
        public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => null;
        public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
            RefreshError is null ? Task.FromResult(new DeviceOAuthTokenSet { AccessToken = "new-token", RefreshToken = "new-refresh" }) : Task.FromException<DeviceOAuthTokenSet>(RefreshError);
    }
}
