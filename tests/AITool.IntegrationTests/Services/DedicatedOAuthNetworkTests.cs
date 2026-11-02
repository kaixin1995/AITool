using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.Codex;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Networking;
using AITool.Application.Xai;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Xai;
using AITool.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// Exercises production services and concrete vendor clients through the real network-bound factory.
/// The only HTTP handlers are recording fakes; even the legacy/fallback client cannot open a socket.
/// Each test owns its SQLite database and transports, including process-wide refresh-lock keys.
/// </summary>
public sealed class DedicatedOAuthNetworkTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-dedicated-network-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly RecordingResolver _resolver = new();
    private readonly RecordingTransport _transport = new();
    private readonly RejectFallbackHandler _fallbackHandler = new();
    private readonly HttpClient _fallback;
    private readonly NetworkBoundOAuthClientFactory _factory;

    public DedicatedOAuthNetworkTests()
    {
        _fallback = new HttpClient(_fallbackHandler);
        _factory = new NetworkBoundOAuthClientFactory(
            new AccountNetworkClientFactory(_resolver, _transport),
            NullLoggerFactory.Instance, new FixedVersion(), _resolver);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddSingleton(_factory);
        // These concrete fallback clients must never be used when the factory is supplied.
        services.AddSingleton<ICodexOAuthClient>(new CodexOAuthClient(_fallback));
        services.AddSingleton<IGoogleOAuthClient>(new GoogleOAuthClient(_fallback));
        services.AddSingleton<IKimiOAuthClient>(new KimiOAuthClient(_fallback, NullLogger<KimiOAuthClient>.Instance));
        services.AddSingleton<IXaiOAuthClient>(new XaiOAuthClient(_fallback, NullLogger<XaiOAuthClient>.Instance));
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.Insertable(new SystemRuntimeSettings
        {
            Id = 1, OAuthFeaturesEnabled = true, OAuthAutoDisableThresholdPercent = 100
        }).ExecuteCommand();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Quota_services_send_real_requests_with_each_accounts_guid_and_selected_proxy(string provider)
    {
        var first = await SeedAsync(provider);
        var second = await SeedAsync(provider);
        using var overlap = new TwoRequestBarrier();
        _transport.BeforeResponse = (_, ct) => overlap.EnterAsync(ct);

        var results = await Task.WhenAll(QueryQuotaAsync(first), QueryQuotaAsync(second));

        Assert.All(results, result =>
        {
            Assert.True(result.Success, result.Error);
            Assert.NotEmpty(result.Windows);
        });
        Assert.Equal(2, _transport.Requests.Count);
        foreach (var account in new[] { first, second })
        {
            var request = Assert.Single(_transport.Requests, r => r.Scope == Scope(account.Identity));
            Assert.Equal(QuotaUrl(provider), request.Uri.AbsoluteUri);
            Assert.Equal(provider is "google" or "xai" ? HttpMethod.Post : HttpMethod.Get, request.Method);
            Assert.Equal($"Bearer {account.AccessToken}", request.Header("Authorization"));
            Assert.Null(request.Header("Cookie"));
            if (provider == "codex") Assert.Equal(account.ExternalId, request.Header("Chatgpt-Account-Id"));
            if (provider == "kimi") Assert.Equal(account.DeviceId, request.Header("X-Msh-Device-Id"));
            AssertRouting(account, "quota");
            var state = await ReadStateAsync(account);
            Assert.Equal(account.AccessToken, state.AccessToken);
            Assert.Equal(account.RefreshToken, state.RefreshToken);
        }
        AssertNoFallbackOrSharedHeaders();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Concrete_oauth_clients_refresh_concurrently_without_mixing_headers_cookies_or_tokens(string provider)
    {
        var first = await SeedAsync(provider);
        var second = await SeedAsync(provider);
        using var overlap = new TwoRequestBarrier();
        _transport.BeforeResponse = (request, ct) => request.Uri.AbsoluteUri == TokenUrl(provider)
            ? overlap.EnterAsync(ct) : Task.CompletedTask;

        async Task<string> RefreshAsync(SeededAccount account)
        {
            using var bound = _factory.CreateForAccount(account.Identity, "isolation-refresh");
            // Deliberately give the operation-local clients conflicting defaults. Neither may reach
            // the other operation or mutate the transport-owned pooled clients' default headers.
            bound.HttpClient.DefaultRequestHeaders.Add("Cookie", $"session={account.Identity.AccountId:N}");
            bound.HttpClient.DefaultRequestHeaders.Add("X-Test-Account", account.Identity.AccountId.ToString("D"));
            return provider switch
            {
                "codex" => (await bound.CodexOAuth.RefreshTokenAsync(account.RefreshToken, default)).AccessToken,
                "google" => (await bound.GoogleOAuth.RefreshTokenAsync("Antigravity", account.RefreshToken, default)).AccessToken,
                "kimi" => (await bound.KimiOAuth.RefreshTokenAsync(account.RefreshToken, account.DeviceId, default)).AccessToken,
                "xai" => (await bound.XaiOAuth.RefreshTokenAsync(account.RefreshToken, default)).AccessToken,
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
        }

        var tokens = await Task.WhenAll(RefreshAsync(first), RefreshAsync(second));

        Assert.Equal(new[] { NewAccess(first.Identity), NewAccess(second.Identity) }, tokens);
        foreach (var account in new[] { first, second })
        {
            var requests = _transport.Requests.Where(r => r.Scope == Scope(account.Identity)).ToArray();
            Assert.Equal(provider == "xai" ? 2 : 1, requests.Length);
            Assert.All(requests, request =>
            {
                Assert.Equal($"session={account.Identity.AccountId:N}", request.Header("Cookie"));
                Assert.Equal(account.Identity.AccountId.ToString("D"), request.Header("X-Test-Account"));
                Assert.Null(request.Header("Authorization"));
            });
            var tokenRequest = Assert.Single(requests, r => r.Uri.AbsoluteUri == TokenUrl(provider));
            Assert.Equal(HttpMethod.Post, tokenRequest.Method);
            Assert.Contains($"refresh_token={account.RefreshToken}", tokenRequest.Body);
            Assert.DoesNotContain(account == first ? second.RefreshToken : first.RefreshToken, tokenRequest.Body);
            if (provider == "kimi") Assert.Equal(account.DeviceId, tokenRequest.Header("X-Msh-Device-Id"));
            if (provider == "xai") Assert.Contains(requests, r => r.Uri.AbsoluteUri == XaiConstants.DiscoveryUrl);
            AssertRouting(account, "isolation-refresh");
        }
        AssertNoFallbackOrSharedHeaders();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Background_refresh_uses_account_policy_and_persists_rotated_tokens_and_site_key(string provider)
    {
        var account = await SeedAsync(provider);

        await RunBackgroundRefreshAsync(provider);

        var state = await ReadStateAsync(account);
        Assert.Equal(NewAccess(account.Identity), state.AccessToken);
        Assert.Equal(NewRefresh(account.Identity), state.RefreshToken);
        Assert.Equal(NewAccess(account.Identity), state.SiteApiKey);
        Assert.False(state.RequiresReauth);
        Assert.False(state.IsEnabled, "refreshing credentials must not re-enable a disabled account");
        Assert.False(state.SiteIsEnabled);
        Assert.NotNull(state.LastRefreshAt);
        var request = Assert.Single(_transport.Requests, r => r.Uri.AbsoluteUri == TokenUrl(provider));
        Assert.Equal(Scope(account.Identity), request.Scope);
        Assert.Contains($"refresh_token={account.RefreshToken}", request.Body);
        AssertRouting(account, "background-token-refresh");
        AssertNoFallbackOrSharedHeaders();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    public async Task Quota_401_refreshes_credentials_and_retries_through_the_same_account_policy(string provider)
    {
        var account = await SeedAsync(provider);
        _transport.RejectOldQuotaToken = true;

        var result = await QueryQuotaAsync(account);

        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(result.Windows);
        var requests = _transport.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(new[] { QuotaUrl(provider), TokenUrl(provider), QuotaUrl(provider) }, requests.Select(r => r.Uri.AbsoluteUri));
        Assert.Equal($"Bearer {account.AccessToken}", requests[0].Header("Authorization"));
        Assert.Contains($"refresh_token={account.RefreshToken}", requests[1].Body);
        Assert.Equal($"Bearer {NewAccess(account.Identity)}", requests[2].Header("Authorization"));
        Assert.All(requests, request => Assert.Equal(Scope(account.Identity), request.Scope));
        Assert.Equal(new[] { "quota", "credential-refresh", "quota" }, _transport.Acquisitions.Select(a => a.Purpose));
        AssertRouting(account, "quota", "credential-refresh");
        var state = await ReadStateAsync(account);
        Assert.Equal(NewAccess(account.Identity), state.AccessToken);
        Assert.Equal(NewRefresh(account.Identity), state.RefreshToken);
        Assert.Equal(NewAccess(account.Identity), state.SiteApiKey);
        AssertNoFallbackOrSharedHeaders();
    }

    [Fact]
    public async Task Codex_reset_credit_query_and_consume_use_the_selected_account_proxy()
    {
        var account = await SeedAsync("codex");
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.CodexAccounts.SingleAsync(a => a.Id == account.Identity.AccountId);
        var service = new CodexResetCreditsService(_fallback, NullLogger<CodexResetCreditsService>.Instance, _factory);

        var result = await service.QueryResetCreditsAsync(stored, default);
        var consumed = await service.ConsumeResetCreditAsync(stored, "redeem-isolated", default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.AvailableCount);
        Assert.Single(result.Credits);
        Assert.True(consumed.Success, consumed.Error);
        var requests = _transport.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal(CreditsUrl, requests[0].Uri.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal(CreditsUrl + "/consume", requests[1].Uri.AbsoluteUri);
        using var payload = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("redeem-isolated", payload.RootElement.GetProperty("redeem_request_id").GetString());
        Assert.All(requests, request =>
        {
            Assert.Equal($"Bearer {account.AccessToken}", request.Header("Authorization"));
            Assert.Equal(account.ExternalId, request.Header("Chatgpt-Account-Id"));
        });
        AssertRouting(account, "reset-credits");
        AssertNoFallbackOrSharedHeaders();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Proxy_policy_failure_during_background_refresh_preserves_credentials_and_reauth_state(string provider)
    {
        var account = await SeedAsync(provider);
        var before = await ReadStateAsync(account);
        // Intentionally resembles an auth error: a local proxy policy error is not invalid_grant.
        _resolver.Failure = new NetworkPolicyException("proxy_disabled", "Selected proxy unavailable (403 Forbidden).");

        await RunBackgroundRefreshAsync(provider);

        Assert.Equal(before, await ReadStateAsync(account));
        Assert.Contains(account.Identity, _resolver.Accounts);
        Assert.Empty(_transport.Acquisitions);
        Assert.Empty(_transport.Requests);
        AssertNoFallbackOrSharedHeaders();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Proxy_policy_failure_during_quota_does_not_mutate_credentials_or_mark_reauth(string provider)
    {
        var account = await SeedAsync(provider);
        var before = await ReadStateAsync(account);
        _resolver.Failure = new NetworkPolicyException("proxy_disabled", "Selected proxy unavailable (403 Forbidden).");

        var result = await QueryQuotaAsync(account);

        Assert.False(result.Success);
        Assert.Contains("Selected proxy unavailable", result.Error ?? "");
        Assert.Equal(before, await ReadStateAsync(account));
        Assert.Contains(account.Identity, _resolver.Accounts);
        Assert.Empty(_transport.Acquisitions);
        Assert.Empty(_transport.Requests);
        AssertNoFallbackOrSharedHeaders();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    public async Task Proxy_policy_failure_during_credential_recovery_does_not_disable_or_replace_credentials(string provider)
    {
        // Begin enabled, so an incorrect classification as an upstream 403 is observable.
        var account = await SeedAsync(provider, enabled: true);
        var before = await ReadStateAsync(account);
        _resolver.Failure = new NetworkPolicyException("proxy_disabled", "Selected proxy unavailable (403 Forbidden).");
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var metadata = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        // Policy failures must propagate to the caller, not become a null refresh result or
        // an upstream 403 that disables the account. Assert the original policy code survives.
        var exception = await Assert.ThrowsAsync<NetworkPolicyException>(() => provider switch
        {
            "codex" => CodexCredentials(db, metadata).RefreshAsync(account.SiteId, account.AccessToken, default),
            "google" => GoogleCredentials(db, metadata).RefreshAsync(account.SiteId, account.AccessToken, default),
            "kimi" => KimiCredentials(db, metadata).RefreshAsync(account.SiteId, account.AccessToken, default),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        });

        Assert.Same(_resolver.Failure, exception);
        Assert.Equal("proxy_disabled", exception.Code);
        Assert.Equal(before, await ReadStateAsync(account));
        Assert.Contains(account.Identity, _resolver.Accounts);
        Assert.Empty(_transport.Acquisitions);
        Assert.Empty(_transport.Requests);
        AssertNoFallbackOrSharedHeaders();
    }

    private const string CreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private static string QuotaUrl(string provider) => provider switch
    {
        "codex" => "https://chatgpt.com/backend-api/wham/usage",
        "google" => "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
        "kimi" => "https://api.kimi.com/coding/v1/usages",
        "xai" => "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static string TokenUrl(string provider) => provider switch
    {
        "codex" => "https://auth.openai.com/oauth/token",
        "google" => "https://oauth2.googleapis.com/token",
        "kimi" => "https://auth.kimi.com/api/oauth/token",
        "xai" => "https://auth.x.ai/oauth/token",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static string Scope(AccountIdentity account) => $"{account.ProviderKey}:{account.AccountId:D}";
    private static string NewAccess(AccountIdentity account) => $"new-access-{account.AccountId:N}";
    private static string NewRefresh(AccountIdentity account) => $"new-refresh-{account.AccountId:N}";

    private async Task<SeededAccount> SeedAsync(string provider, bool enabled = false)
    {
        var id = Guid.NewGuid();
        var account = new SeededAccount(new AccountIdentity(provider, id), Guid.NewGuid(),
            $"old-access-{id:N}", $"refresh-{id:N}", $"external-{id:N}", $"device-{id:N}");
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Client.Insertable(new Site
        {
            Id = account.SiteId, Name = $"network-{id:N}", ApiKey = account.AccessToken, IsEnabled = enabled
        }).ExecuteCommandAsync();
        var expired = DateTimeOffset.UtcNow.AddMinutes(-5);
        switch (provider)
        {
            case "codex":
                await db.Client.Insertable(new CodexAccount { Id = id, AccountId = account.ExternalId, LinkedSiteId = account.SiteId,
                    AccessToken = account.AccessToken, RefreshToken = account.RefreshToken, TokenExpiresAt = expired, IsEnabled = enabled }).ExecuteCommandAsync();
                break;
            case "google":
                await db.Client.Insertable(new GoogleAccount { Id = id, AccountKind = "Antigravity", LinkedSiteId = account.SiteId,
                    AccessToken = account.AccessToken, RefreshToken = account.RefreshToken, TokenExpiresAt = expired, IsEnabled = enabled }).ExecuteCommandAsync();
                break;
            case "kimi":
                await db.Client.Insertable(new KimiAccount { Id = id, DeviceId = account.DeviceId, LinkedSiteId = account.SiteId,
                    AccessToken = account.AccessToken, RefreshToken = account.RefreshToken, TokenExpiresAt = expired, IsEnabled = enabled }).ExecuteCommandAsync();
                break;
            case "xai":
                await db.Client.Insertable(new XaiAccount { Id = id, LinkedSiteId = account.SiteId,
                    AccessToken = account.AccessToken, RefreshToken = account.RefreshToken, TokenExpiresAt = expired, IsEnabled = enabled }).ExecuteCommandAsync();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
        _resolver.Configurations[account.Identity] = new ResolvedNetworkConfiguration
        {
            Mode = NetworkMode.ProxyProfile, ProxyProfileId = Guid.NewGuid(), ProfileRevision = 7,
            ProxyEndpoint = new Uri($"http://proxy-{id:N}.invalid:8123"),
            Credentials = new NetworkCredential($"proxy-user-{id:N}", $"proxy-password-{id:N}")
        };
        return account;
    }

    private async Task<AccountQuotaSnapshot> QueryQuotaAsync(SeededAccount account)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var metadata = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var cache = _services.GetRequiredService<IMemoryCache>();
        IAccountQuotaProvider service = account.Identity.ProviderKey switch
        {
            "codex" => new CodexQuotaService(_fallback, db, metadata, cache, CodexCredentials(db, metadata),
                new FixedVersion(), NullLogger<CodexQuotaService>.Instance, networkFactory: _factory),
            "google" => new GoogleAccountQuotaService(_fallback, db, metadata, cache, GoogleCredentials(db, metadata),
                NullLogger<GoogleAccountQuotaService>.Instance, networkFactory: _factory),
            "kimi" => new KimiQuotaService(_fallback, db, metadata, cache, KimiCredentials(db, metadata),
                NullLogger<KimiQuotaService>.Instance, networkFactory: _factory),
            "xai" => new XaiQuotaService(_fallback, db, metadata, cache, _services.GetRequiredService<IXaiOAuthClient>(),
                NullLogger<XaiQuotaService>.Instance, networkFactory: _factory),
            _ => throw new ArgumentOutOfRangeException(nameof(account))
        };
        return await service.QueryAsync(new AccountQuotaTarget
        {
            ProviderKey = account.Identity.ProviderKey, AccountId = account.Identity.AccountId, LinkedSiteId = account.SiteId
        }, true, default);
    }

    private CodexCredentialRefreshService CodexCredentials(AppDbContext db, ProxyRequestMetadataCache metadata) =>
        new(db, _services.GetRequiredService<ICodexOAuthClient>(), metadata, NullLogger<CodexCredentialRefreshService>.Instance, _factory);
    private GoogleCredentialRefreshService GoogleCredentials(AppDbContext db, ProxyRequestMetadataCache metadata) =>
        new(db, _services.GetRequiredService<IGoogleOAuthClient>(), metadata, NullLogger<GoogleCredentialRefreshService>.Instance, _factory);
    private KimiCredentialRefreshService KimiCredentials(AppDbContext db, ProxyRequestMetadataCache metadata) =>
        new(db, _services.GetRequiredService<IKimiOAuthClient>(), metadata, NullLogger<KimiCredentialRefreshService>.Instance, _factory);

    private async Task RunBackgroundRefreshAsync(string provider)
    {
        var environment = new TestingEnvironment();
        switch (provider)
        {
            case "codex":
                using (var service = new CodexTokenRefreshService(_services, NullLogger<CodexTokenRefreshService>.Instance, environment))
                    await service.RefreshDueAccountsAsync(default);
                break;
            case "google":
                using (var service = new GoogleTokenRefreshService(_services, NullLogger<GoogleTokenRefreshService>.Instance, environment))
                    await service.RefreshDueAccountsAsync(default);
                break;
            case "kimi":
                using (var service = new KimiTokenRefreshService(_services, NullLogger<KimiTokenRefreshService>.Instance, environment))
                    await service.RefreshDueAccountsAsync(default);
                break;
            case "xai":
                using (var service = new XaiTokenRefreshService(_services, NullLogger<XaiTokenRefreshService>.Instance, environment))
                    await service.RefreshDueAccountsAsync(default);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private async Task<CredentialState> ReadStateAsync(SeededAccount account)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = await db.Sites.InSingleAsync(account.SiteId);
        var id = account.Identity.AccountId;
        switch (account.Identity.ProviderKey)
        {
            case "codex":
                var codex = await db.CodexAccounts.SingleAsync(a => a.Id == id);
                return new(codex.AccessToken, codex.RefreshToken, codex.TokenExpiresAt, codex.LastRefreshAt, codex.IsEnabled,
                    false, false, site.ApiKey, site.IsEnabled);
            case "google":
                var google = await db.GoogleAccounts.SingleAsync(a => a.Id == id);
                return new(google.AccessToken, google.RefreshToken, google.TokenExpiresAt, google.LastRefreshAt, google.IsEnabled,
                    false, google.DisabledByUpstream, site.ApiKey, site.IsEnabled);
            case "kimi":
                var kimi = await db.KimiAccounts.SingleAsync(a => a.Id == id);
                return new(kimi.AccessToken, kimi.RefreshToken, kimi.TokenExpiresAt, kimi.LastRefreshAt, kimi.IsEnabled,
                    false, false, site.ApiKey, site.IsEnabled);
            case "xai":
                var xai = await db.XaiAccounts.SingleAsync(a => a.Id == id);
                return new(xai.AccessToken, xai.RefreshToken, xai.TokenExpiresAt, xai.LastRefreshAt, xai.IsEnabled,
                    xai.RequiresReauth, false, site.ApiKey, site.IsEnabled);
            default: throw new ArgumentOutOfRangeException(nameof(account));
        }
    }

    private void AssertRouting(SeededAccount account, params string[] purposes)
    {
        var expected = _resolver.Configurations[account.Identity];
        var acquisitions = _transport.Acquisitions.Where(a => a.Scope == Scope(account.Identity)).ToArray();
        Assert.NotEmpty(acquisitions);
        Assert.All(acquisitions, acquisition =>
        {
            Assert.Same(expected, acquisition.Configuration);
            Assert.Equal(NetworkMode.ProxyProfile, acquisition.Configuration.Mode);
            Assert.Equal(expected.ProxyProfileId, acquisition.Configuration.ProxyProfileId);
            Assert.Equal(expected.ProxyEndpoint, acquisition.Configuration.ProxyEndpoint);
            Assert.Equal(expected.Credentials!.UserName, acquisition.Configuration.Credentials!.UserName);
            Assert.Equal(expected.Credentials.Password, acquisition.Configuration.Credentials.Password);
            Assert.Contains(acquisition.Purpose, purposes);
        });
        Assert.Equal(acquisitions.Length, _resolver.Accounts.Count(a => a == account.Identity));
        Assert.Empty(_resolver.Operations);
    }

    private void AssertNoFallbackOrSharedHeaders()
    {
        Assert.Equal(0, _fallbackHandler.Sends);
        Assert.Empty(_fallback.DefaultRequestHeaders);
        Assert.All(_transport.Clients, client => Assert.Empty(client.DefaultRequestHeaders));
        Assert.Equal(0, _transport.ActiveLeases);
    }

    public void Dispose()
    {
        _services.Dispose();
        _transport.Dispose();
        _fallback.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); } catch { }
        }
    }

    private sealed record SeededAccount(AccountIdentity Identity, Guid SiteId, string AccessToken,
        string RefreshToken, string ExternalId, string DeviceId);
    private sealed record CredentialState(string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt,
        DateTimeOffset? LastRefreshAt, bool IsEnabled, bool RequiresReauth, bool DisabledByUpstream,
        string? SiteApiKey, bool SiteIsEnabled);
    private sealed record Acquisition(ResolvedNetworkConfiguration Configuration, string Scope, string Purpose);
    private sealed record CapturedRequest(string Scope, Uri Uri, HttpMethod Method, string Body,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class RecordingResolver : IAccountNetworkPolicyResolver
    {
        public ConcurrentDictionary<AccountIdentity, ResolvedNetworkConfiguration> Configurations { get; } = new();
        public ConcurrentQueue<AccountIdentity> Accounts { get; } = new();
        public ConcurrentQueue<NetworkOperationContext> Operations { get; } = new();
        public NetworkPolicyException? Failure { get; set; }
        public Task<ResolvedNetworkConfiguration> ResolveForAccountAsync(AccountIdentity account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Accounts.Enqueue(account);
            return Failure is null ? Task.FromResult(Configurations[account]) : Task.FromException<ResolvedNetworkConfiguration>(Failure);
        }
        public Task<ResolvedNetworkConfiguration> ResolveAsync(NetworkOperationContext operation, CancellationToken cancellationToken = default)
        {
            Operations.Enqueue(operation);
            throw new InvalidOperationException("Existing-account service must resolve its actual account GUID, not a pending operation.");
        }
    }

    private sealed class RecordingTransport : INetworkTransport, IDisposable
    {
        private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
        private int _activeLeases;
        public int ActiveLeases => Volatile.Read(ref _activeLeases);
        public IEnumerable<HttpClient> Clients => _clients.Values;
        public ConcurrentQueue<Acquisition> Acquisitions { get; } = new();
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
        public bool RejectOldQuotaToken { get; set; }
        public Func<CapturedRequest, CancellationToken, Task>? BeforeResponse { get; set; }

        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope,
            string purpose, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquisitions.Enqueue(new(configuration, isolationScope, purpose));
            var client = _clients.GetOrAdd(isolationScope, scope => new HttpClient(new CaptureHandler(this, scope))
            {
                Timeout = Timeout.InfiniteTimeSpan
            });
            Interlocked.Increment(ref _activeLeases);
            return ValueTask.FromResult<INetworkTransportLease>(new Lease(this, client));
        }

        private async Task<HttpResponseMessage> SendAsync(string scope, HttpRequestMessage request, CancellationToken ct)
        {
            var captured = new CapturedRequest(scope, request.RequestUri!, request.Method,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase));
            Requests.Enqueue(captured);
            if (BeforeResponse is not null) await BeforeResponse(captured, ct);
            var split = scope.Split(':', 2);
            var account = new AccountIdentity(split[0], Guid.Parse(split[1]));
            var url = captured.Uri.AbsoluteUri;
            HttpContent content;
            if (url == XaiConstants.DiscoveryUrl)
            {
                content = new StringContent("""{"device_authorization_endpoint":"https://auth.x.ai/oauth/device/code","token_endpoint":"https://auth.x.ai/oauth/token"}""");
            }
            else if (url == TokenUrl(account.ProviderKey))
            {
                content = new StringContent(JsonSerializer.Serialize(new
                {
                    access_token = NewAccess(account), refresh_token = NewRefresh(account), expires_in = 3600, token_type = "Bearer"
                }), Encoding.UTF8, "application/json");
            }
            else if (url == CreditsUrl)
            {
                content = new StringContent("""{"available_count":1,"credits":[{"id":"credit-1","status":"available","reset_type":"codex_rate_limits","expires_at":4102444800}]}""");
            }
            else if (url == CreditsUrl + "/consume") content = new StringContent("{}");
            else if (url == QuotaUrl(account.ProviderKey))
            {
                if (RejectOldQuotaToken && captured.Header("Authorization") == $"Bearer old-access-{account.AccountId:N}")
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
                content = account.ProviderKey switch
                {
                    "codex" => new StringContent("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":20,"limit_window_seconds":18000}}}"""),
                    "google" => new StringContent("""{"models":{"gemini-test":{"quotaInfo":{"remainingFraction":0.8}}}}"""),
                    "kimi" => new StringContent("""{"usage":{"limit":"100","used":"20"}}"""),
                    "xai" => new ByteArrayContent([0, 0, 0, 0, 7, 10, 5, 13, .. BitConverter.GetBytes(20f)]),
                    _ => throw new InvalidOperationException("Unknown provider")
                };
            }
            else throw new InvalidOperationException($"Unexpected fake HTTP endpoint: {url}");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            // A response cookie must not become a default request header on any pooled client.
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"upstream={account.AccountId:N}; Path=/; Secure");
            return response;
        }

        public void Dispose()
        {
            foreach (var client in _clients.Values) client.Dispose();
        }
        private sealed class CaptureHandler(RecordingTransport owner, string scope) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.SendAsync(scope, request, ct);
        }
        private sealed class Lease(RecordingTransport owner, HttpClient client) : INetworkTransportLease
        {
            private int _disposed;
            public HttpClient Client => client;
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._activeLeases);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TwoRequestBarrier : IDisposable
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        public Task EnterAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _entered) == 2) _bothEntered.TrySetResult();
            return _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        public void Dispose() => _bothEntered.TrySetCanceled();
    }
    private sealed class RejectFallbackHandler : HttpMessageHandler
    {
        private int _sends;
        public int Sends => Volatile.Read(ref _sends);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _sends);
            throw new InvalidOperationException("A network-bound operation unexpectedly used the legacy client.");
        }
    }
    private sealed class FixedVersion : ICodexClientVersionResolver
    {
        public Task<CodexClientVersionInfo> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexClientVersionInfo { ClientVersion = "1.0.0", UserAgent = "dedicated-network-test/1.0.0" });
    }
    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AITool.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
