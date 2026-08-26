using AITool.Application.Codex;
using AITool.Application.Google;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Services;

public sealed class TokenRefreshDisabledAccountTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-token-refresh-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _serviceProvider;

    public TokenRefreshDisabledAccountTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddSingleton<StubCodexOAuthClient>();
        services.AddSingleton<ICodexOAuthClient>(serviceProvider => serviceProvider.GetRequiredService<StubCodexOAuthClient>());
        services.AddSingleton<StubGoogleOAuthClient>();
        services.AddSingleton<IGoogleOAuthClient>(serviceProvider => serviceProvider.GetRequiredService<StubGoogleOAuthClient>());
        _serviceProvider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    [Fact]
    public async Task Disabled_codex_account_is_still_refreshed()
    {
        var siteId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await SeedRuntimeSettingsAsync();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Site
            {
                Id = siteId,
                Name = "Disabled Codex site",
                BaseUrl = "https://example.com",
                ApiKey = "old-codex-token",
                IsEnabled = false
            });
            db.CodexAccounts.Add(new CodexAccount
            {
                Id = accountId,
                DisplayName = "Disabled Codex account",
                AccessToken = "old-codex-token",
                RefreshToken = "codex-refresh-token",
                TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                LinkedSiteId = siteId,
                IsEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var service = new CodexTokenRefreshService(
            _serviceProvider,
            NullLogger<CodexTokenRefreshService>.Instance,
            new TestingHostEnvironment());

        await service.RefreshDueAccountsAsync(CancellationToken.None);

        var oauth = _serviceProvider.GetRequiredService<StubCodexOAuthClient>();
        oauth.RefreshCallCount.Should().Be(1);
        await using var verifyScope = _serviceProvider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.CodexAccounts.SingleAsync(account => account.Id == accountId)).AccessToken.Should().Be("new-codex-token");
        (await verifyDb.Sites.SingleAsync(site => site.Id == siteId)).ApiKey.Should().Be("new-codex-token");
    }

    [Fact]
    public async Task Disabled_google_account_is_still_refreshed()
    {
        var siteId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await SeedRuntimeSettingsAsync();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Site
            {
                Id = siteId,
                Name = "Disabled Google site",
                BaseUrl = "https://example.com",
                ApiKey = "old-google-token",
                IsEnabled = false
            });
            db.GoogleAccounts.Add(new GoogleAccount
            {
                Id = accountId,
                DisplayName = "Disabled Google account",
                AccountKind = "Antigravity",
                AccessToken = "old-google-token",
                RefreshToken = "google-refresh-token",
                TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                LinkedSiteId = siteId,
                IsEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var service = new GoogleTokenRefreshService(
            _serviceProvider,
            NullLogger<GoogleTokenRefreshService>.Instance,
            new TestingHostEnvironment());

        await service.RefreshDueAccountsAsync(CancellationToken.None);

        var oauth = _serviceProvider.GetRequiredService<StubGoogleOAuthClient>();
        oauth.RefreshCallCount.Should().Be(1);
        await using var verifyScope = _serviceProvider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.GoogleAccounts.SingleAsync(account => account.Id == accountId)).AccessToken.Should().Be("new-google-token");
        (await verifyDb.Sites.SingleAsync(site => site.Id == siteId)).ApiKey.Should().Be("new-google-token");
    }

    [Fact]
    public async Task Google_upstream_403_disables_account_and_linked_site()
    {
        var siteId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Google 403 site",
            BaseUrl = "https://example.com",
            ApiKey = "google-token",
            IsEnabled = true
        });
        db.GoogleAccounts.Add(new GoogleAccount
        {
            Id = accountId,
            DisplayName = "Google 403 account",
            AccountKind = "Antigravity",
            AccessToken = "google-token",
            RefreshToken = "google-refresh-token",
            LinkedSiteId = siteId,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        var service = new GoogleCredentialRefreshService(
            db,
            _serviceProvider.GetRequiredService<StubGoogleOAuthClient>(),
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
            NullLogger<GoogleCredentialRefreshService>.Instance);

        (await service.DisableAsync(siteId, "proxy-403", CancellationToken.None)).Should().BeTrue();

        var account = await db.GoogleAccounts.SingleAsync(item => item.Id == accountId);
        account.IsEnabled.Should().BeFalse();
        account.DisabledByUpstream.Should().BeTrue();
        (await db.Sites.SingleAsync(site => site.Id == siteId)).IsEnabled.Should().BeFalse();
    }

    private async Task SeedRuntimeSettingsAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            OAuthFeaturesEnabled = true
        });
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        TryDeleteDatabaseFile(_databasePath);
    }

    private static void TryDeleteDatabaseFile(string databasePath)
    {
        try { File.Delete(databasePath); } catch { }
        try { File.Delete(databasePath + "-wal"); } catch { }
        try { File.Delete(databasePath + "-shm"); } catch { }
    }

    private sealed class TestingHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AITool.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubCodexOAuthClient : ICodexOAuthClient
    {
        public int RefreshCallCount { get; private set; }

        public (string State, string Verifier) CreateOAuthSession() => ("state", "verifier");

        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default) =>
            Task.FromResult("https://example.com/oauth");

        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        {
            RefreshCallCount++;
            return Task.FromResult(new CodexTokenSet
            {
                AccessToken = "new-codex-token",
                RefreshToken = "new-codex-refresh-token",
                ExpiresIn = 3600
            });
        }
    }

    private sealed class StubGoogleOAuthClient : IGoogleOAuthClient
    {
        public int RefreshCallCount { get; private set; }

        public GoogleOAuthSession CreateSession() => new("state");

        public string BuildAuthorizeUrl(string accountKind, GoogleOAuthSession session) => "https://example.com/oauth";

        public Task<GoogleTokenSet> ExchangeCodeAsync(string accountKind, string code, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GoogleTokenSet> RefreshTokenAsync(string accountKind, string refreshToken, CancellationToken ct)
        {
            RefreshCallCount++;
            return Task.FromResult(new GoogleTokenSet
            {
                AccessToken = "new-google-token",
                ExpiresIn = 3600
            });
        }

        public Task<string?> GetUserEmailAsync(string accessToken, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetUserProjectsAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<GoogleCodeAssistProfile> LoadCodeAssistProfileAsync(string accountKind, string accessToken, CancellationToken ct) =>
            Task.FromResult(new GoogleCodeAssistProfile());
    }
}
