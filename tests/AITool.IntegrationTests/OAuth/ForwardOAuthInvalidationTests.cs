using System.Net;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.OAuth;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.OAuth;

/// <summary>Exercises real resolver snapshots and persistence against a private SQLite file, without network access.</summary>
public sealed class ForwardOAuthInvalidationTests : IDisposable
{
    private const string AccessToken = "original-access";
    private const string RefreshToken = "original-refresh";
    private static readonly DateTimeOffset RefreshedAt = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-forward-invalidation-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly ForwardCredentialResolver _resolver;
    private readonly ForwardOAuthInvalidationService _invalidation;

    public ForwardOAuthInvalidationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_path}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddScoped<ForwardOAuthInvalidationService>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _resolver = new ForwardCredentialResolver(_services.GetRequiredService<IServiceScopeFactory>());
        _invalidation = new ForwardOAuthInvalidationService(_db, _scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>());
    }

    public static IEnumerable<object[]> Providers() =>
        new[] { "codex", "google", "kimi", "xai", "device-oauth" }.Select(provider => new object[] { provider });

    public static IEnumerable<object[]> NullableCredentials()
    {
        foreach (var row in Providers())
        foreach (var nullRefresh in new[] { false, true })
        foreach (var nullLastRefresh in new[] { false, true })
            yield return new object[] { row[0], nullRefresh, nullLastRefresh };
    }

    public static IEnumerable<object[]> ChangedCredentials()
    {
        foreach (var row in Providers())
        foreach (var field in new[] { "AccessToken", "RefreshToken", "LastRefreshAt" })
        foreach (var transition in field == "AccessToken" ? new[] { "replace" } : new[] { "replace", "from-null", "to-null" })
            yield return new object[] { row[0], field, transition };
    }

    [Theory]
    [MemberData(nameof(NullableCredentials))]
    public async Task Matching_real_snapshot_disables_only_its_account_and_site(string provider, bool nullRefresh, bool nullLastRefresh)
    {
        var target = await SeedAsync(provider, nullRefresh, nullLastRefresh);
        var other = await SeedAsync(provider, false, false);
        var snapshot = await ResolveSnapshotAsync(target);

        await _invalidation.InvalidateAsync(AccessToken, snapshot, CancellationToken.None);

        await AssertStateAsync(target, enabled: false, requiresReauth: true, siteEnabled: false);
        await AssertStateAsync(other, enabled: true, requiresReauth: false, siteEnabled: true);
        var persisted = await ReadAccountAsync(target);
        persisted.AccessToken.Should().Be(AccessToken);
        persisted.RefreshToken.Should().Be(nullRefresh ? null : RefreshToken);
        persisted.LastRefreshAt.Should().Be(nullLastRefresh ? null : RefreshedAt);
    }

    [Theory]
    [MemberData(nameof(ChangedCredentials))]
    public async Task Changed_credential_after_snapshot_cannot_disable_account_or_site(string provider, string field, string transition)
    {
        var target = await SeedAsync(provider, transition == "from-null" && field == "RefreshToken", transition == "from-null" && field == "LastRefreshAt");
        var snapshot = await ResolveSnapshotAsync(target);
        object? replacement = transition == "to-null" ? null : field switch
        {
            "AccessToken" => "new-access",
            "RefreshToken" => "new-refresh",
            _ => RefreshedAt.AddHours(1)
        };
        await ChangeCredentialAsync(target, field, replacement);

        await _invalidation.InvalidateAsync(AccessToken, snapshot, CancellationToken.None);

        await AssertStateAsync(target, enabled: true, requiresReauth: false, siteEnabled: true);
        var persisted = await ReadAccountAsync(target);
        switch (field)
        {
            case "AccessToken": persisted.AccessToken.Should().Be((string?)replacement); break;
            case "RefreshToken": persisted.RefreshToken.Should().Be((string?)replacement); break;
            default: persisted.LastRefreshAt.Should().Be((DateTimeOffset?)replacement); break;
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Different_sent_token_or_missing_snapshot_cannot_disable_account_or_site(string provider)
    {
        var target = await SeedAsync(provider, false, false);
        var snapshot = await ResolveSnapshotAsync(target);

        await _invalidation.InvalidateAsync("different-access", snapshot, CancellationToken.None);
        await _invalidation.InvalidateAsync(AccessToken, null, CancellationToken.None);
        await AssertStateAsync(target, enabled: true, requiresReauth: false, siteEnabled: true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Null_refresh_after_real_forward_401_disables_only_unchanged_credential(string provider)
    {
        var target = await SeedAsync(provider, false, false);
        var handler = new UnauthorizedHandler();
        var forward = new ProxyForwardService(new HttpClient(handler), NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new DirectPolicy(), credentialResolver: _resolver);
        var result = await forward.ForwardAsync(new ProxyForwardRequest
        {
            SiteId = target.SiteId, ManagedSource = target.Source,
            AccountIdentity = new AccountIdentity(provider, target.AccountId), TargetBaseUrl = target.BaseUrl,
            TargetApiKey = AccessToken, RequestBody = "{}", RequestTimeoutSeconds = 10,
            RefreshTargetApiKeyAsync = (_, _) => Task.FromResult<string?>(null),
            InvalidateTargetCredentialAsync = _invalidation.InvalidateAsync
        });
        result.StatusCode.Should().Be(401);
        handler.Calls.Should().Be(1);
        await AssertStateAsync(target, false, true, false);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Credential_rotation_during_null_refresh_preserves_account(string provider)
    {
        var target = await SeedAsync(provider, false, false);
        var forward = new ProxyForwardService(new HttpClient(new UnauthorizedHandler()), NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new DirectPolicy(), credentialResolver: _resolver);
        var result = await forward.ForwardAsync(new ProxyForwardRequest
        {
            SiteId = target.SiteId, ManagedSource = target.Source,
            AccountIdentity = new AccountIdentity(provider, target.AccountId), TargetBaseUrl = target.BaseUrl,
            TargetApiKey = AccessToken, RequestBody = "{}", RequestTimeoutSeconds = 10,
            RefreshTargetApiKeyAsync = async (_, _) => { await ChangeCredentialAsync(target, "RefreshToken", "rotated"); return null; },
            InvalidateTargetCredentialAsync = _invalidation.InvalidateAsync
        });
        result.StatusCode.Should().Be(401);
        await AssertStateAsync(target, true, false, true);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Health_probe_real_401_invalidates_only_matching_managed_account(string provider)
    {
        var target = await SeedAsync(provider, false, false);
        var other = await SeedAsync(provider, false, false);
        var model = new ModelLibraryItem { ModelName = "probe-model" };
        await _db.InsertAsync(model);
        var mapping = new SiteModelMapping { SiteId = target.SiteId, ModelLibraryItemId = model.Id, RemoteModelName = "probe-model" };
        await _db.InsertAsync(mapping);
        await _db.InsertAsync(new AITool.Domain.Operations.SystemRuntimeSettings { Id = 1, DetectionRetryCount = 0 });
        var handler = new UnauthorizedHandler();
        using var client = new HttpClient(handler);
        var forward = new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new DirectPolicy(), credentialResolver: _resolver);
        var callbacks = new OfflineHealthCallbacks(new HealthCredentialCallbacks(_scope.ServiceProvider));
        var health = new ModelHealthRequestService(_db, forward, new NoopUsage(),
            new SiteKeySelector(_db, new OwnerStore(target)), sessionPreparation: new NoopSession(), credentialCallbacks: callbacks);

        var result = await health.ProbeMappingAsync(mapping.Id, "test", CancellationToken.None);

        result.Status.Should().Be("fail");
        handler.Calls.Should().Be(1);
        await AssertStateAsync(target, false, true, false);
        await AssertStateAsync(other, true, false, true);
    }

    private sealed class OfflineHealthCallbacks(HealthCredentialCallbacks inner) : IHealthCredentialCallbacks
    {
        public void Configure(ProxyForwardRequest request)
        {
            inner.Configure(request);
            if (request.RefreshTargetApiKeyAsync is not null)
                request.RefreshTargetApiKeyAsync = (_, _) => Task.FromResult<string?>(null);
        }
    }

    private sealed class NoopUsage : IUsageLogService
    {
        public Task LogAsync(UsageLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopSession : IAuxiliarySessionPreparation
    {
        public Task<AuxiliaryPreparedSession> PrepareAsync(string scope, AccountIdentity account, string protocol,
            bool antigravity, string original, string prepared, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
            => Task.FromResult(new AuxiliaryPreparedSession(prepared, new Dictionary<string, string>(headers)));
    }

    private sealed class OwnerStore(Target target) : IAccountNetworkPolicyStore
    {
        public Task<IReadOnlyDictionary<Guid, AccountIdentity>> GetLinkedSiteOwnersAsync(IReadOnlyCollection<Guid> sites, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, AccountIdentity>>(new Dictionary<Guid, AccountIdentity> { [target.SiteId] = new(target.Provider, target.AccountId) });
        public Task<AccountNetworkPolicyState> GetAsync(AccountIdentity account, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccountNetworkPolicyState> SetAsync(AccountIdentity account, AccountNetworkSelection selection, string? revision = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(AccountIdentity account, string? revision = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreadable_401_body_still_invalidates_real_sent_credential(bool streaming)
    {
        var target = await SeedAsync("xai", false, false);
        using var client = new HttpClient(new BrokenBodyHandler());
        var forward = new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new DirectPolicy(), credentialResolver: _resolver);
        var request = new ProxyForwardRequest
        {
            SiteId = target.SiteId, ManagedSource = target.Source,
            AccountIdentity = new AccountIdentity("xai", target.AccountId), TargetBaseUrl = target.BaseUrl,
            TargetApiKey = AccessToken, RequestBody = "{}", RequestTimeoutSeconds = 10,
            InvalidateTargetCredentialAsync = _invalidation.InvalidateAsync
        };
        var result = streaming
            ? await forward.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask)
            : await forward.ForwardAsync(request);
        result.StatusCode.Should().Be(401);
        await AssertStateAsync(target, false, true, false);
    }

    private sealed class BrokenBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new BrokenBody() });
    }

    private sealed class BrokenBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(new IOException("broken response body"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("rejected") });
        }
    }

    private sealed class DirectPolicy : IForwardNetworkPolicyResolver
    {
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Legacy, "test", true));
    }

    private async Task<Target> SeedAsync(string provider, bool nullRefresh, bool nullLastRefresh)
    {
        var source = provider switch
        {
            "codex" => "Codex", "google" => "Google", "kimi" => "kimi_oauth",
            "xai" => "xai_oauth", "device-oauth" => "device_oauth",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var site = new Site { Name = "test", BaseUrl = "https://example.test/v1", ApiKey = AccessToken, ManagedSource = source };
        await _db.InsertAsync(site);
        var id = Guid.NewGuid();
        switch (provider)
        {
            case "codex": await InsertAccountAsync<CodexAccount>(); break;
            case "google": await InsertAccountAsync<GoogleAccount>(); break;
            case "kimi": await InsertAccountAsync<KimiAccount>(); break;
            case "xai": await InsertAccountAsync<XaiAccount>(); break;
            case "device-oauth": await InsertAccountAsync<DeviceOAuthAccount>(); break;
        }
        return new Target(provider, source, id, site.Id, site.BaseUrl);

        async Task InsertAccountAsync<T>() where T : class, new()
        {
            var account = new T();
            Set(account, "Id", id);
            Set(account, "LinkedSiteId", site.Id);
            Set(account, "AccessToken", AccessToken);
            Set(account, "RefreshToken", nullRefresh ? null : RefreshToken);
            Set(account, "LastRefreshAt", nullLastRefresh ? null : RefreshedAt);
            if (account is DeviceOAuthAccount device) device.VendorKey = "test";
            await _db.InsertAsync(account);
        }
    }

    private async Task<object> ResolveSnapshotAsync(Target target)
    {
        using var lease = await _resolver.ResolveAsync(new ProxyForwardRequest
        {
            SiteId = target.SiteId, ManagedSource = target.Source,
            AccountIdentity = new AccountIdentity(target.Provider, target.AccountId),
            TargetBaseUrl = target.BaseUrl, TargetApiKey = AccessToken, RequestBody = "{}"
        });
        lease.ApiKey.Should().Be(AccessToken);
        lease.CredentialSnapshot.Should().NotBeNull();
        return lease.CredentialSnapshot!;
    }

    private async Task ChangeCredentialAsync(Target target, string field, object? value)
    {
        switch (target.Provider)
        {
            case "codex": await UpdateAsync<CodexAccount>(); break;
            case "google": await UpdateAsync<GoogleAccount>(); break;
            case "kimi": await UpdateAsync<KimiAccount>(); break;
            case "xai": await UpdateAsync<XaiAccount>(); break;
            case "device-oauth": await UpdateAsync<DeviceOAuthAccount>(); break;
        }
        async Task UpdateAsync<T>() where T : class, new()
        {
            var account = await _db.Client.Queryable<T>().InSingleAsync(target.AccountId);
            account.Should().NotBeNull();
            Set(account, field, value);
            await _db.UpdateAsync(account);
        }
    }

    private async Task<AccountState> ReadAccountAsync(Target target) => target.Provider switch
    {
        "codex" => await ReadAsync<CodexAccount>(target.AccountId),
        "google" => await ReadAsync<GoogleAccount>(target.AccountId),
        "kimi" => await ReadAsync<KimiAccount>(target.AccountId),
        "xai" => await ReadAsync<XaiAccount>(target.AccountId),
        "device-oauth" => await ReadAsync<DeviceOAuthAccount>(target.AccountId),
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private async Task<AccountState> ReadAsync<T>(Guid id) where T : class, new()
    {
        var account = await _db.Client.Queryable<T>().InSingleAsync(id);
        account.Should().NotBeNull();
        return new AccountState((bool)Get(account, "IsEnabled")!, (bool)Get(account, "RequiresReauth")!,
            (string?)Get(account, "AccessToken"), (string?)Get(account, "RefreshToken"), (DateTimeOffset?)Get(account, "LastRefreshAt"));
    }

    private async Task AssertStateAsync(Target target, bool enabled, bool requiresReauth, bool siteEnabled)
    {
        var account = await ReadAccountAsync(target);
        account.IsEnabled.Should().Be(enabled);
        account.RequiresReauth.Should().Be(requiresReauth);
        var site = await _db.Sites.InSingleAsync(target.SiteId);
        site.Should().NotBeNull();
        site.IsEnabled.Should().Be(siteEnabled);
    }

    private static void Set<T>(T entity, string property, object? value) => typeof(T).GetProperty(property)!.SetValue(entity, value);
    private static object? Get<T>(T entity, string property) => typeof(T).GetProperty(property)!.GetValue(entity);
    private sealed record Target(string Provider, string Source, Guid AccountId, Guid SiteId, string BaseUrl);
    private sealed record AccountState(bool IsEnabled, bool RequiresReauth, string? AccessToken, string? RefreshToken, DateTimeOffset? LastRefreshAt);

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch { }
    }
}
