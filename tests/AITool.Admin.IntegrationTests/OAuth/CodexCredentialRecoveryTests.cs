using AITool.Infrastructure.Proxy;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Admin.Controllers.Admin;
using AITool.Admin.Services;
using AITool.Infrastructure.Networking;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace AITool.Admin.IntegrationTests.OAuth;

public sealed class CodexCredentialRecoveryTests : IDisposable
{
    [Theory]
    [InlineData("workspace", "member@example.com", true)]
    [InlineData("wrong-workspace", "member@example.com", false)]
    [InlineData("workspace", "other@example.com", false)]
    public async Task Reauthorize_oauth_callback_updates_only_matching_existing_account(string workspace, string email, bool matches)
    {
        var (account, site) = await SeedAsync(false, false, true);
        await _db.Client.Updateable<CodexAccount>().SetColumns(x => new CodexAccount { AccountId = "workspace", Email = "member@example.com" })
            .Where(x => x.Id == account.Id).ExecuteCommandAsync();
        var controller = Controller(new LoginOAuth(workspace, email));
        var start = (await controller.StartOAuth(new StartOAuthRequest { ReauthorizeAccountId = account.Id }, default)).Should().BeOfType<OkObjectResult>().Subject;
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(start.Value));
        var state = json.RootElement.GetProperty("state").GetString();
        var result = await controller.CompleteOAuth(new CompleteOAuthRequest { CallbackUrl = $"http://localhost/callback?code=test&state={state}", DisplayName = "must-not-rename" }, default);
        if (matches) result.Should().BeOfType<OkObjectResult>(); else result.Should().BeOfType<BadRequestObjectResult>();
        var saved = await _db.CodexAccounts.InSingleAsync(account.Id);
        saved.DisplayName.Should().Be("Codex");
        saved.AccessToken.Should().Be(matches ? "new-access" : "old-access");
        (await _db.Sites.InSingleAsync(site.Id)).ApiKey.Should().Be(matches ? "new-access" : "old-access");
        (await _db.CodexAccounts.ToListAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task Reauthorization_preserves_manual_cooling_and_global_disable(bool manual, bool cooling, bool featureEnabled)
    {
        var (account, site) = await SeedAsync(manual, cooling, featureEnabled);
        await _db.Client.Updateable<CodexAccount>().SetColumns(x => new CodexAccount { AccountId = "workspace", Email = "member@example.com" })
            .Where(x => x.Id == account.Id).ExecuteCommandAsync();
        var controller = Controller(new LoginOAuth("workspace", "member@example.com"));
        var start = (OkObjectResult)await controller.StartOAuth(new StartOAuthRequest { ReauthorizeAccountId = account.Id }, default);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(start.Value));
        var state = json.RootElement.GetProperty("state").GetString();
        (await controller.CompleteOAuth(new CompleteOAuthRequest { CallbackUrl = $"http://localhost/callback?code=test&state={state}" }, default)).Should().BeOfType<OkObjectResult>();
        var saved = await _db.CodexAccounts.InSingleAsync(account.Id);
        saved.RequiresReauth.Should().BeFalse();
        saved.IsEnabled.Should().BeFalse();
        saved.ManuallyDisabled.Should().Be(manual);
        saved.IsQuotaCooling.Should().Be(cooling);
        (await _db.Sites.InSingleAsync(site.Id)).IsEnabled.Should().BeFalse();
    }

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"codex-recovery-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly AppDbContext _db;
    private readonly ProxyRequestMetadataCache _cache;

    public CodexCredentialRecoveryTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_path};Pooling=False");
        services.AddScoped<ProxyRequestMetadataCache>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<ISqlSugarClient>());
        _db = _services.GetRequiredService<AppDbContext>();
        _cache = _services.GetRequiredService<ProxyRequestMetadataCache>();
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public async Task Editing_refresh_token_recovers_expired_account_without_overriding_other_disables(bool manual, bool cooling, bool featureEnabled, bool expectedEnabled)
    {
        var (account, site) = await SeedAsync(manual, cooling, featureEnabled);
        var oldSnapshot = new ForwardOAuthCredentialSnapshot("codex", account.Id, site.Id, account.AccessToken, account.RefreshToken, account.LastRefreshAt);
        var controller = Controller(new StubOAuth("new-access"));
        var response = await controller.Update(account.Id, new UpdateAccountRequest { DisplayName = "Codex", RefreshToken = "replacement" }, default);
        response.Should().BeOfType<OkObjectResult>();
        var saved = await _db.CodexAccounts.InSingleAsync(account.Id);
        var linked = await _db.Sites.InSingleAsync(site.Id);
        saved.RequiresReauth.Should().BeFalse();
        saved.IsEnabled.Should().Be(expectedEnabled);
        saved.ManuallyDisabled.Should().Be(manual);
        saved.IsQuotaCooling.Should().Be(cooling);
        saved.DisabledByFeatureToggle.Should().Be(!featureEnabled);
        saved.AccessToken.Should().Be("new-access");
        saved.RefreshToken.Should().Be("rotated-refresh");
        linked.ApiKey.Should().Be("new-access");
        linked.IsEnabled.Should().Be(expectedEnabled);
        await new ForwardOAuthInvalidationService(_db, _cache).InvalidateAsync("old-access", oldSnapshot, default);
        (await _db.CodexAccounts.InSingleAsync(account.Id)).RequiresReauth.Should().BeFalse("过期凭证的迟到 401 不应使新凭证失效");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Editing_does_not_recover_when_refresh_fails_or_returns_no_access_token(bool fail)
    {
        var (account, site) = await SeedAsync(false, false, true);
        var response = await Controller(new StubOAuth(fail ? null : "")).Update(account.Id, new UpdateAccountRequest { DisplayName = "Codex", RefreshToken = "replacement" }, default);
        response.Should().BeOfType<OkObjectResult>();
        var saved = await _db.CodexAccounts.InSingleAsync(account.Id);
        saved.RequiresReauth.Should().BeTrue();
        saved.IsEnabled.Should().BeFalse();
        (await _db.Sites.InSingleAsync(site.Id)).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Manual_refresh_recovers_expired_account()
    {
        var (account, site) = await SeedAsync(false, false, true);
        (await Controller(new StubOAuth("new-access")).RefreshToken(account.Id, default)).Should().BeOfType<OkObjectResult>();
        var saved = await _db.CodexAccounts.InSingleAsync(account.Id);
        saved.RequiresReauth.Should().BeFalse();
        saved.IsEnabled.Should().BeTrue();
        (await _db.Sites.InSingleAsync(site.Id)).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Site_sync_failure_preserves_rotated_credentials_without_enabling_account()
    {
        var (account, site) = await SeedAsync(false, false, true);
        await _db.Client.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_site_sync BEFORE UPDATE OF ApiKey ON Sites BEGIN SELECT RAISE(ABORT, 'site sync failed'); END;");
        var response = (await Controller(new StubOAuth("new-access")).Update(account.Id, new UpdateAccountRequest { DisplayName = "Codex", RefreshToken = "replacement" }, default)).Should().BeOfType<OkObjectResult>().Subject;
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response.Value));
        json.RootElement.GetProperty("message").GetString().Should().Contain("同步未完成");
        var saved = await _db.CodexAccounts.InSingleAsync(account.Id);
        saved.AccessToken.Should().Be("new-access");
        saved.RefreshToken.Should().Be("rotated-refresh");
        saved.RequiresReauth.Should().BeTrue();
        saved.IsEnabled.Should().BeFalse();
        var linked = await _db.Sites.InSingleAsync(site.Id);
        linked.IsEnabled.Should().BeFalse();
        linked.ApiKey.Should().Be("old-access");
    }

    private CodexApiController Controller(ICodexOAuthClient oauth)
    {
        var provisioner = new CodexAccountProvisioner(_db, _cache, null!, new SiteCascadeDeleter(_db), NullLogger<CodexAccountProvisioner>.Instance);
        return new CodexApiController(_db, oauth, provisioner, null!, null!, null!, null!, null!, null!, NullLogger<CodexApiController>.Instance, _cache);
    }

    private async Task<(CodexAccount, Site)> SeedAsync(bool manual, bool cooling, bool featureEnabled)
    {
        var site = new Site { Id = Guid.NewGuid(), Name = "Codex", BaseUrl = "https://example.com", ApiKey = "old-access", IsEnabled = false };
        var account = new CodexAccount { Id = Guid.NewGuid(), LinkedSiteId = site.Id, DisplayName = "Codex", AccessToken = "old-access", RefreshToken = "old-refresh", LastRefreshAt = DateTimeOffset.UtcNow.AddHours(-1), RequiresReauth = true, IsEnabled = false, ManuallyDisabled = manual, IsQuotaCooling = cooling };
        await _db.InsertAsync(site);
        await _db.InsertAsync(account);
        await _db.InsertAsync(new SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = featureEnabled });
        return (account, site);
    }

    public void Dispose()
    {
        _services.GetRequiredService<ISqlSugarClient>().Dispose();
        _services.Dispose();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(path)) File.Delete(path);
    }

    private sealed class StubOAuth(string? access) : ICodexOAuthClient
    {
        public (string State, string Verifier) CreateOAuthSession() => throw new NotSupportedException();
        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) => access is null ? throw new InvalidOperationException("invalid refresh token") : Task.FromResult(new CodexTokenSet { AccessToken = access, RefreshToken = "rotated-refresh", ExpiresIn = 3600 });
    }

    private sealed class LoginOAuth(string workspace, string email) : ICodexOAuthClient
    {
        public (string State, string Verifier) CreateOAuthSession() => (Guid.NewGuid().ToString("N"), "verifier");
        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default) => Task.FromResult("https://example.test/authorize");
        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
                ["email"] = email, ["https://api.openai.com/auth"] = new Dictionary<string, string> { ["chatgpt_account_id"] = workspace }
            });
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Task.FromResult(new CodexTokenSet { AccessToken = "new-access", RefreshToken = "new-refresh", IdToken = "h." + encoded + ".s", ExpiresIn = 3600 });
        }
        public Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
