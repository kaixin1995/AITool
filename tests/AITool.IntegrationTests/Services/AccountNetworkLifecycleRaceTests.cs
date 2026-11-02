using System.Collections;
using System.Reflection;
using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Networking;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// Exercises the real credential entrypoints and account/cascade/policy transaction boundary.
/// Every case owns its database and account IDs; OAuth fakes cannot open a network connection.
/// </summary>
[Collection(AccountNetworkLifecycleRaceCollection.Name)]
public sealed class AccountNetworkLifecycleRaceTests : IDisposable
{
    private const string OldAccess = "old-access-token";
    private const string OldRefresh = "old-refresh-token";
    private const string NewAccess = "new-access-token";
    private const string NewRefresh = "new-refresh-token";
    private const string DeleteFault = "lifecycle-account-delete-fault";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-lifecycle-{Guid.NewGuid():N}.db");
    private readonly RecordingMemoryCache _memory = new();
    private readonly ServiceProvider _services;
    private readonly AccountNetworkPolicyStore _policies;

    public AccountNetworkLifecycleRaceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryCache>(_memory);
        services.AddSqlSugar($"Data Source={_databasePath};Pooling=False");
        services.AddSingleton<ProxyRequestMetadataCache>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _policies = new AccountNetworkPolicyStore(_services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, new ProxyProfileSecretService(new EphemeralDataProtectionProvider()));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    public async Task Linked_site_and_account_refresh_share_single_flight_and_return_persisted_token(string provider)
    {
        var seed = await SeedAsync(provider);
        using var siteScope = _services.CreateScope();
        using var accountScope = _services.CreateScope();
        var siteDb = siteScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accountDb = accountScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var oauth = new BlockingOAuthClient();
        var siteRefresh = Refreshers(provider, siteDb, oauth);
        var accountRefresh = Refreshers(provider, accountDb, oauth);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var first = siteRefresh.BySite(seed.SiteId, OldAccess, deadline.Token);
        Task<string?>? second = null;
        try
        {
            await oauth.Started.Task.WaitAsync(Timeout);
            Assert.Equal(1, oauth.RefreshCalls);
            Assert.Equal(OldAccess, (await siteDb.Sites.InSingleAsync(seed.SiteId)).ApiKey);

            // A separate service/scope enters the OTHER public API while the vendor is held.
            second = accountRefresh.ByAccount(seed.AccountId, OldAccess, deadline.Token);
            Assert.False(second.IsCompleted);
            // Read the lock's waiter state synchronously: do not release the vendor merely
            // because a task was scheduled or a SQL read started. This fails deterministically
            // if the two entrypoints accidentally acquire different keys or different locks.
            Assert.Equal(2, CredentialLockReferences(provider, seed.AccountId));
            Assert.Equal(1, oauth.RefreshCalls);
        }
        finally
        {
            oauth.Release.TrySetResult(true);
            // Drain even on an assertion failure so no test leaks a held process-wide lease.
            // Await the operations themselves, not timeout wrappers that would leave work
            // running against a disposed fixture. Their deadline/vendor safety tokens bound
            // cancellation, and the production services retain ownership through persistence.
            if (second is null) await first;
            else await Task.WhenAll(first, second);
        }

        Assert.Equal(NewAccess, await first);
        Assert.Equal(NewAccess, await second!);
        Assert.Equal(1, oauth.RefreshCalls);
        using var verification = _services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var credentials = await CredentialsAsync(db, provider, seed.AccountId);
        Assert.Equal(NewAccess, credentials.Access);
        Assert.Equal(NewRefresh, credentials.Refresh);
        Assert.Equal(NewAccess, (await db.Sites.InSingleAsync(seed.SiteId)).ApiKey);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Account_delete_failure_rolls_back_policy_account_and_entire_site_graph_without_invalidating_cache(string provider)
    {
        var owned = await SeedAsync(provider);
        var unrelated = await SeedAsync(provider);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = _services.GetRequiredService<ProxyRequestMetadataCache>();
        await WarmCachesAsync(cache, provider);
        var before = await SnapshotAsync(db, provider);
        _memory.Removed.Clear();
        _memory.InTransaction = () => db.Client.Ado.Transaction is not null;

        // The account DELETE is deliberately the last failing statement. The trigger verifies
        // that policy + graph deletion really happened on THIS transaction before aborting.
        await db.Client.Ado.ExecuteCommandAsync($"""
            CREATE TRIGGER reject_lifecycle_account_delete BEFORE DELETE ON {AccountTable(provider)}
            BEGIN
                SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM Sites WHERE Id = OLD.LinkedSiteId) OR
                    EXISTS (SELECT 1 FROM SiteModelMappings WHERE SiteId = OLD.LinkedSiteId) OR
                    EXISTS (SELECT 1 FROM ProxyRouteRules WHERE SiteId = OLD.LinkedSiteId) OR
                    EXISTS (SELECT 1 FROM ProxyRouteEntries WHERE EntryName = '{owned.EntryName}') OR
                    EXISTS (SELECT 1 FROM AccountNetworkPolicies WHERE ProviderKey = '{provider}' AND AccountId = OLD.Id)
                THEN RAISE(ABORT, 'graph-or-policy-not-deleted-before-account')
                ELSE RAISE(ABORT, '{DeleteFault}') END;
            END;
            """);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => DeleteAsync(db, cache, provider, owned.AccountId));

        Assert.Contains(DeleteFault, exception.ToString());
        Assert.Null(db.Client.Ado.Transaction);
        // Query on a fresh connection, not an ORM snapshot or an uncommitted transaction.
        using var verification = _services.CreateScope();
        var persisted = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(before, await SnapshotAsync(persisted, provider));
        await AssertGraphAsync(persisted, provider, owned, exists: true);
        await AssertGraphAsync(persisted, provider, unrelated, exists: true);
        Assert.Empty(_memory.Removed);
        Assert.Equal(2, (await cache.GetRouteEntriesAsync(default)).Count);
        Assert.Equal(2, (await cache.GetRouteSiteInstancesAsync(default)).Count);
        await AssertAccountCacheAsync(cache, provider, owned.AccountId, unrelated.AccountId);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Account_delete_commits_only_owned_policy_account_and_graph_then_invalidates_metadata(string provider)
    {
        var owned = await SeedAsync(provider);
        var unrelated = await SeedAsync(provider);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = _services.GetRequiredService<ProxyRequestMetadataCache>();
        await WarmCachesAsync(cache, provider);
        _memory.Removed.Clear();
        _memory.InTransaction = () => db.Client.Ado.Transaction is not null;

        await DeleteAsync(db, cache, provider, owned.AccountId);

        Assert.Null(db.Client.Ado.Transaction);
        using var verification = _services.CreateScope();
        var persisted = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        await AssertGraphAsync(persisted, provider, owned, exists: false);
        await AssertGraphAsync(persisted, provider, unrelated, exists: true);
        Assert.Equal(1, await persisted.Sites.CountAsync());
        Assert.Equal(1, await persisted.SiteModelMappings.CountAsync());
        Assert.Equal(1, await persisted.ProxyRouteRules.CountAsync());
        Assert.Equal(1, await persisted.ProxyRouteEntries.CountAsync());
        Assert.Equal(1, await persisted.AccountNetworkPolicies.CountAsync());
        // Model library items are shared metadata, not account-owned cascade targets.
        Assert.Equal(2, await persisted.ModelLibraryItems.CountAsync());
        Assert.NotEmpty(_memory.Removed);
        Assert.All(_memory.Removed, removal => Assert.False(removal.InTransaction,
            $"Cache key {removal.Key} was invalidated before commit"));
        Assert.Contains(_memory.Removed, r => r.Key == "admin-route-entries");
        Assert.Contains(_memory.Removed, r => r.Key == "admin-route-site-instances");
        Assert.Contains(_memory.Removed, r => r.Key == "chat-models");
        if (provider != "xai") Assert.Contains(_memory.Removed, r => r.Key == $"{provider}-accounts");
        Assert.Equal(unrelated.EntryName, Assert.Single(await cache.GetRouteEntriesAsync(default)).EntryName);
        Assert.Equal(unrelated.SiteId, Assert.Single(await cache.GetRouteSiteInstancesAsync(default)).SiteId);
        await AssertAccountCacheAsync(cache, provider, unrelated.AccountId);
    }

    private async Task<Seed> SeedAsync(string provider)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accountId = Guid.NewGuid();
        var site = new Site { Name = $"lifecycle-{accountId:N}", BaseUrl = "https://example.invalid", ApiKey = OldAccess, IsEnabled = true };
        var model = new ModelLibraryItem { ModelName = $"model-{accountId:N}", DisplayName = "Lifecycle model", IsEnabled = true };
        var mapping = new SiteModelMapping { SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = model.ModelName, IsEnabled = true };
        var entry = new ProxyRouteEntry { EntryName = model.ModelName };
        var rule = new ProxyRouteRule
        {
            ExternalModelName = entry.EntryName, UpstreamModelName = model.ModelName,
            TargetKind = "Site", SiteId = site.Id, SiteModelName = mapping.RemoteModelName, IsEnabled = true
        };
        await db.Client.Insertable(site).ExecuteCommandAsync();
        await db.Client.Insertable(model).ExecuteCommandAsync();
        await db.Client.Insertable(mapping).ExecuteCommandAsync();
        await db.Client.Insertable(entry).ExecuteCommandAsync();
        await db.Client.Insertable(rule).ExecuteCommandAsync();
        switch (provider)
        {
            case "codex":
                await db.Client.Insertable(new CodexAccount { Id = accountId, DisplayName = site.Name, LinkedSiteId = site.Id, AccessToken = OldAccess, RefreshToken = OldRefresh }).ExecuteCommandAsync();
                break;
            case "google":
                await db.Client.Insertable(new GoogleAccount { Id = accountId, DisplayName = site.Name, LinkedSiteId = site.Id, AccessToken = OldAccess, RefreshToken = OldRefresh, ProjectId = "fake-project", SubscriptionTier = "pro" }).ExecuteCommandAsync();
                break;
            case "kimi":
                await db.Client.Insertable(new KimiAccount { Id = accountId, DisplayName = site.Name, LinkedSiteId = site.Id, AccessToken = OldAccess, RefreshToken = OldRefresh, DeviceId = accountId.ToString("D") }).ExecuteCommandAsync();
                break;
            case "xai":
                await db.Client.Insertable(new XaiAccount { Id = accountId, DisplayName = site.Name, LinkedSiteId = site.Id, AccessToken = OldAccess, RefreshToken = OldRefresh }).ExecuteCommandAsync();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
        await _policies.SetAsync(new AccountIdentity(provider, accountId), new AccountNetworkSelection(NetworkMode.Direct));
        return new Seed(accountId, site.Id, mapping.Id, entry.Id, rule.Id, entry.EntryName);
    }

    private (Func<Guid, string, CancellationToken, Task<string?>> BySite,
        Func<Guid, string, CancellationToken, Task<string?>> ByAccount) Refreshers(string provider, AppDbContext db, BlockingOAuthClient oauth)
    {
        var cache = _services.GetRequiredService<ProxyRequestMetadataCache>();
        // Intentionally omit the optional network factory: the real services must still share
        // their canonical static account lock when invoked by existing manual-constructor hosts.
        switch (provider)
        {
            case "codex":
                var codex = new CodexCredentialRefreshService(db, oauth, cache, NullLogger<CodexCredentialRefreshService>.Instance);
                return (codex.RefreshAsync, codex.RefreshForAccountAsync);
            case "google":
                var google = new GoogleCredentialRefreshService(db, oauth, cache, NullLogger<GoogleCredentialRefreshService>.Instance);
                return (google.RefreshAsync, google.RefreshForAccountAsync);
            case "kimi":
                var kimi = new KimiCredentialRefreshService(db, oauth, cache, NullLogger<KimiCredentialRefreshService>.Instance);
                return (kimi.RefreshAsync, kimi.RefreshForAccountAsync);
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private async Task DeleteAsync(AppDbContext db, ProxyRequestMetadataCache cache, string provider, Guid accountId)
    {
        // Cancel the actual gate wait/DB mutation, not just this test's wait for its task.
        // Always observe completion (including rollback and lease release) before disposing DB.
        using var deadline = new CancellationTokenSource(Timeout);
        var ct = deadline.Token;
        var cascade = new SiteCascadeDeleter(db, cache);
        // Model catalogs/fetchers and credential refreshers are not used by deletion. No real
        // vendor client or network factory is constructed, but the policy store is the real one.
        var deletion = provider switch
        {
            "codex" => new CodexAccountProvisioner(db, cache, null!, cascade,
                NullLogger<CodexAccountProvisioner>.Instance, networkPolicies: _policies).DeprovisionAsync(accountId, ct),
            "google" => new GoogleAccountProvisioner(db, cache, null!, null!, cascade,
                NullLogger<GoogleAccountProvisioner>.Instance, networkPolicies: _policies).DeprovisionAsync(accountId, ct),
            "kimi" => new KimiAccountProvisioner(db, cache, null!, null!, cascade,
                NullLogger<KimiAccountProvisioner>.Instance, networkPolicies: _policies).DeleteAsync(accountId, ct),
            "xai" => new XaiAccountProvisioner(db, cache, null!, cascade,
                NullLogger<XaiAccountProvisioner>.Instance, networkPolicies: _policies).DeleteAsync(accountId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        await deletion;
        // Even if a provider ignores cancellation, exceeding the same deadline is a failure.
        ct.ThrowIfCancellationRequested();
    }

    private static async Task WarmCachesAsync(ProxyRequestMetadataCache cache, string provider)
    {
        Assert.Equal(2, (await cache.GetRouteEntriesAsync(default)).Count);
        Assert.Equal(2, (await cache.GetRouteSiteInstancesAsync(default)).Count);
        if (provider == "codex") Assert.Equal(2, (await cache.GetCodexAccountsAsync(default)).Count);
        if (provider == "google") Assert.Equal(2, (await cache.GetGoogleAccountsAsync(default)).Count);
        if (provider == "kimi") Assert.Equal(2, (await cache.GetKimiAccountsAsync(default)).Count);
    }

    private static async Task AssertAccountCacheAsync(ProxyRequestMetadataCache cache, string provider, params Guid[] expected)
    {
        IEnumerable<Guid>? actual = provider switch
        {
            "codex" => (await cache.GetCodexAccountsAsync(default)).Select(a => a.Id),
            "google" => (await cache.GetGoogleAccountsAsync(default)).Select(a => a.Id),
            "kimi" => (await cache.GetKimiAccountsAsync(default)).Select(a => a.Id),
            _ => null // xAI has no dedicated metadata account-list cache.
        };
        if (actual is not null) Assert.Equal(expected.OrderBy(id => id), actual.OrderBy(id => id));
    }

    private static async Task AssertGraphAsync(AppDbContext db, string provider, Seed seed, bool exists)
    {
        Assert.Equal(exists, await db.Sites.AnyAsync(s => s.Id == seed.SiteId));
        Assert.Equal(exists, await db.SiteModelMappings.AnyAsync(m => m.Id == seed.MappingId));
        Assert.Equal(exists, await db.ProxyRouteRules.AnyAsync(r => r.Id == seed.RuleId));
        Assert.Equal(exists, await db.ProxyRouteEntries.AnyAsync(e => e.Id == seed.EntryId));
        Assert.Equal(exists, await db.AccountNetworkPolicies.AnyAsync(p => p.ProviderKey == provider && p.AccountId == seed.AccountId));
        var accountExists = provider switch
        {
            "codex" => await db.CodexAccounts.AnyAsync(a => a.Id == seed.AccountId),
            "google" => await db.GoogleAccounts.AnyAsync(a => a.Id == seed.AccountId),
            "kimi" => await db.KimiAccounts.AnyAsync(a => a.Id == seed.AccountId),
            "xai" => await db.XaiAccounts.AnyAsync(a => a.Id == seed.AccountId),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        Assert.Equal(exists, accountExists);
    }

    private static async Task<(string? Access, string? Refresh)> CredentialsAsync(AppDbContext db, string provider, Guid accountId)
    {
        switch (provider)
        {
            case "codex":
                var codex = await db.CodexAccounts.InSingleAsync(accountId);
                return (codex.AccessToken, codex.RefreshToken);
            case "google":
                var google = await db.GoogleAccounts.InSingleAsync(accountId);
                return (google.AccessToken, google.RefreshToken);
            case "kimi":
                var kimi = await db.KimiAccounts.InSingleAsync(accountId);
                return (kimi.AccessToken, kimi.RefreshToken);
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private static async Task<string> SnapshotAsync(AppDbContext db, string provider)
    {
        static string Rows<T>(IEnumerable<T> rows) => string.Join("\n", rows.Select(row => JsonSerializer.Serialize(row)).OrderBy(row => row, StringComparer.Ordinal));
        var accounts = provider switch
        {
            "codex" => Rows(await db.CodexAccounts.ToListAsync()),
            "google" => Rows(await db.GoogleAccounts.ToListAsync()),
            "kimi" => Rows(await db.KimiAccounts.ToListAsync()),
            "xai" => Rows(await db.XaiAccounts.ToListAsync()),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        return string.Join("\n---\n", accounts, Rows(await db.Sites.ToListAsync()),
            Rows(await db.SiteModelMappings.ToListAsync()), Rows(await db.ProxyRouteRules.ToListAsync()),
            Rows(await db.ProxyRouteEntries.ToListAsync()), Rows(await db.AccountNetworkPolicies.ToListAsync()),
            Rows(await db.ModelLibraryItems.ToListAsync()));
    }

    private static string AccountTable(string provider) => provider switch
    {
        "codex" => "CodexAccounts", "google" => "GoogleAccounts", "kimi" => "KimiAccounts", "xai" => "XaiAccounts",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static int CredentialLockReferences(string provider, Guid accountId)
    {
        // Instrumentation only: both actual public entrypoints above acquire/release the lock.
        // Taking its monitor makes observing the waiter count race-free without sleeps/polling.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var locks = typeof(NetworkBoundOAuthClientFactory).GetField("CredentialLocks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var sync = locks.GetType().GetField("_syncRoot", flags)!.GetValue(locks)!;
        lock (sync)
        {
            var entries = (IDictionary)locks.GetType().GetField("_entries", flags)!.GetValue(locks)!;
            var entry = entries[$"{provider}:{accountId:D}"];
            Assert.NotNull(entry);
            return (int)entry.GetType().GetProperty("ReferenceCount")!.GetValue(entry)!;
        }
    }

    public void Dispose()
    {
        _services.Dispose();
        _memory.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Seed(Guid AccountId, Guid SiteId, Guid MappingId, Guid EntryId, Guid RuleId, string EntryName);

    private sealed class RecordingMemoryCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(Options.Create(new MemoryCacheOptions()));
        public Func<bool> InTransaction { get; set; } = () => false;
        public List<(string Key, bool InTransaction)> Removed { get; } = [];
        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);
        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);
        public void Remove(object key)
        {
            Removed.Add((key.ToString()!, InTransaction()));
            _inner.Remove(key);
        }
        public void Dispose() => _inner.Dispose();
    }

    private sealed class BlockingOAuthClient : ICodexOAuthClient, IGoogleOAuthClient, IKimiOAuthClient
    {
        private int _refreshCalls;
        public int RefreshCalls => Volatile.Read(ref _refreshCalls);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task WaitForReleaseAsync(string refreshToken, CancellationToken ct)
        {
            Assert.Equal(OldRefresh, refreshToken);
            Interlocked.Increment(ref _refreshCalls);
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(Timeout, ct);
        }

        public async Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        {
            await WaitForReleaseAsync(refreshToken, cancellationToken);
            return new CodexTokenSet { AccessToken = NewAccess, RefreshToken = NewRefresh, ExpiresIn = 3600 };
        }

        public async Task<GoogleTokenSet> RefreshTokenAsync(string accountKind, string refreshToken, CancellationToken ct)
        {
            Assert.Equal("Antigravity", accountKind);
            await WaitForReleaseAsync(refreshToken, ct);
            return new GoogleTokenSet { AccessToken = NewAccess, RefreshToken = NewRefresh, ExpiresIn = 3600 };
        }

        async Task<KimiTokenSet> IKimiOAuthClient.RefreshTokenAsync(string refreshToken, string? deviceId, CancellationToken ct)
        {
            Assert.False(string.IsNullOrWhiteSpace(deviceId));
            await WaitForReleaseAsync(refreshToken, ct);
            return new KimiTokenSet { AccessToken = NewAccess, RefreshToken = NewRefresh, ExpiresIn = 3600, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }

        public (string State, string Verifier) CreateOAuthSession() => ("state", "verifier");
        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default) => Task.FromResult("https://example.invalid/oauth");
        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public GoogleOAuthSession CreateSession() => new("state");
        public string BuildAuthorizeUrl(string accountKind, GoogleOAuthSession session) => "https://example.invalid/oauth";
        Task<GoogleTokenSet> IGoogleOAuthClient.ExchangeCodeAsync(string accountKind, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserEmailAsync(string accessToken, CancellationToken ct) => Task.FromResult<string?>("lifecycle@example.invalid");
        public Task<IReadOnlyList<string>> GetUserProjectsAsync(string accessToken, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["fake-project"]);
        public Task<GoogleCodeAssistProfile> LoadCodeAssistProfileAsync(string accountKind, string accessToken, CancellationToken ct) => Task.FromResult(new GoogleCodeAssistProfile { ProjectId = "fake-project", Tier = "pro" });
        public Task<KimiDeviceCodeResponse> StartDeviceFlowAsync(string? deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<KimiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, string? deviceId, CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>
/// Unique databases do not isolate the production process-wide RouteGraphMutationGate.
/// Other integration classes deliberately hold SQLite writers while a route mutation owns
/// that gate until its busy timeout. Running these bounded lifecycle checks alongside those
/// classes measures unrelated gate queue time, not a lifecycle deadlock. xUnit runs this
/// collection without other collections; concurrency INSIDE each refresh test remains real.
/// </summary>
[CollectionDefinition(AccountNetworkLifecycleRaceCollection.Name, DisableParallelization = true)]
public sealed class AccountNetworkLifecycleRaceCollection
{
    public const string Name = "Account lifecycle process-global graph gate";
}
