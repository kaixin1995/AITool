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
    public static IEnumerable<object[]> RecoveryBlockers()
    {
        foreach (var row in Providers())
        {
            var p = (string)row[0];
            foreach (var flag in new[] { "ManuallyDisabled", "DisabledByFeatureToggle", "global-off", "missing" }) yield return new object[] { p, flag };
            if (p is "codex" or "google") yield return new object[] { p, "IsQuotaCooling" };
            if (p == "google") yield return new object[] { p, "DisabledByUpstream" };
            if (p is "kimi" or "xai" or "device-oauth") yield return new object[] { p, "IsDeleted" };
            if (p is "xai" or "device-oauth") yield return new object[] { p, "RequiresReauth" };
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
        _schedule.Calls.Should().OnlyContain(c => c.Key == key && c.Id == target.AccountId && c.Last == target.LastQuotaCheckedAt);
        _schedule.Scheduled.Should().HaveCount(1, "store EnsureScheduled is idempotent");
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
        _schedule.Calls.Should().ContainSingle(c => c.Key == key && c.Id == target.AccountId && c.Last > target.LastQuotaCheckedAt);
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

    private IAccountQuotaProvider Create(string key)
    {
        var cache = _services.GetRequiredService<IMemoryCache>();
        return key switch
        {
            "codex" => new CodexQuotaService(_http, _db, _metadata, cache, null!, new FakeVersion(), NullLogger<CodexQuotaService>.Instance, _schedule),
            "google" => new GoogleAccountQuotaService(_http, _db, _metadata, cache, null!, NullLogger<GoogleAccountQuotaService>.Instance, _schedule),
            "kimi" => new KimiQuotaService(_http, _db, _metadata, cache, null!, NullLogger<KimiQuotaService>.Instance, _schedule),
            "xai" => new XaiQuotaService(_http, _db, _metadata, cache, null!, NullLogger<XaiQuotaService>.Instance, _schedule),
            "device-oauth" => new DeviceOAuthQuotaService(_db, new DeviceOAuthVendorRegistry([_vendor]), _metadata, cache, null!, NullLogger<DeviceOAuthQuotaService>.Instance, _schedule),
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
        public List<(string Key, Guid Id, DateTimeOffset? Last)> Calls { get; } = [];
        public HashSet<(string Key, Guid Id)> Scheduled { get; } = [];
        public Action<CancellationToken>? BeforeEnsure { get; set; }
        public Task? EnsureResult { get; set; }
        public Task EnsureScheduledAsync(string providerKey, Guid accountId, DateTimeOffset? lastQuotaCheckedAt, CancellationToken cancellationToken)
        {
            BeforeEnsure?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (EnsureResult is not null) return EnsureResult;
            Calls.Add((providerKey, accountId, lastQuotaCheckedAt)); Scheduled.Add((providerKey, accountId)); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<QuotaRecheckScheduleInfo>> ReconcileAsync(string providerKey, IReadOnlyList<AccountQuotaTarget> eligibleAccounts, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuotaRecheckLease?> TryAcquireAsync(string providerKey, Guid accountId, CancellationToken cancellationToken, bool force = false) => throw new NotSupportedException();
        public Task CompleteAsync(QuotaRecheckLease lease, bool keepScheduled, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeVersion : ICodexClientVersionResolver
    {
        public Task<CodexClientVersionInfo> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CodexClientVersionInfo { ClientVersion = "1.0.0", UserAgent = "test/1.0.0" });
    }

    private sealed class FakeNetwork : HttpMessageHandler
    {
        public string Key { get; set; } = "";
        public Action? BeforeReply { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BeforeReply?.Invoke();
            HttpContent content = Key switch
            {
                "codex" => new StringContent("""{"rate_limit":{"primary_window":{"used_percent":99,"limit_window_seconds":18000}}}"""),
                "google" => new StringContent("""{"models":{"gemini-test":{"quotaInfo":{"remainingFraction":0.01}}}}"""),
                "kimi" => new StringContent("""{"usage":{"limit":"100","used":"99"}}"""),
                "xai" => new ByteArrayContent([0, 0, 0, 0, 7, 10, 5, 13, .. BitConverter.GetBytes(99f)]),
                _ => throw new InvalidOperationException("Unexpected fake network request"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FakeVendor : IDeviceOAuthVendor
    {
        public string VendorKey => "fake";
        public string Label => "Fake";
        public DeviceOAuthChatSiteConfig? ChatSiteConfig => null;
        public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => [];
        public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken) => Task.FromResult(new DeviceOAuthQuotaResult { Success = true, RawJson = "{}", Windows = [new AccountQuotaWindow { Id = "test", UsedPercent = 99 }] });
        public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => null;
        public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
