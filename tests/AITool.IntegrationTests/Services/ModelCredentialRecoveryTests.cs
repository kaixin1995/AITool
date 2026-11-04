using AITool.Application.Xai;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.Services;

public sealed class ModelCredentialRecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"model-recovery-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly FakeXai _oauth = new();

    public ModelCredentialRecoveryTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_path}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddScoped<ForwardOAuthInvalidationService>();
        services.AddSingleton<IXaiOAuthClient>(_oauth);
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refreshes_and_retries_once_then_only_disables_if_retry_rejected(bool rejectRetry)
    {
        var (account, snapshot) = await SeedAsync();
        var seen = new List<string>();
        async Task<string> Fetch(string token)
        {
            seen.Add(token);
            await Task.Yield();
            if (token == "old" || rejectRetry) throw new ModelAccessTokenUnauthorizedException();
            return "models";
        }
        var operation = () => ModelCredentialRecovery.FetchAsync(_db, _scope.ServiceProvider, snapshot, Fetch, default);
        if (rejectRetry) await operation.Should().ThrowAsync<ModelAccessTokenUnauthorizedException>();
        else (await operation()).Should().Be("models");
        seen.Should().Equal("old", "new");
        _oauth.Calls.Should().Be(1);
        var saved = await _db.XaiAccounts.InSingleAsync(account.Id);
        saved.AccessToken.Should().Be("new");
        saved.RefreshToken.Should().Be("rotated");
        saved.IsEnabled.Should().Be(!rejectRetry);
        saved.RequiresReauth.Should().Be(rejectRetry);
        var site = await _db.Sites.InSingleAsync(account.LinkedSiteId);
        site.ApiKey.Should().Be("new");
        site.IsEnabled.Should().Be(!rejectRetry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_explicit_refresh_refusal_disables_account(bool refused)
    {
        var (account, snapshot) = await SeedAsync();
        _oauth.Error = refused ? new XaiRefreshTokenInvalidException() : new HttpRequestException("temporary");
        var operation = () => ModelCredentialRecovery.FetchAsync<string>(_db, _scope.ServiceProvider, snapshot,
            _ => throw new ModelAccessTokenUnauthorizedException(), default);
        if (refused) await operation.Should().ThrowAsync<ModelAccessTokenUnauthorizedException>();
        else await operation.Should().ThrowAsync<HttpRequestException>();
        var saved = await _db.XaiAccounts.InSingleAsync(account.Id);
        saved.IsEnabled.Should().Be(!refused);
        saved.RequiresReauth.Should().Be(refused);
        (await _db.Sites.InSingleAsync(account.LinkedSiteId)).IsEnabled.Should().Be(!refused);
    }

    [Fact]
    public async Task Concurrent_rotation_is_reused_and_stale_response_cannot_disable_it()
    {
        var (account, snapshot) = await SeedAsync();
        var calls = 0;
        async Task<string> Fetch(string token)
        {
            calls++;
            if (calls == 1)
            {
                await _db.Client.Updateable<XaiAccount>().SetColumns(a => new XaiAccount { AccessToken = "concurrent", RefreshToken = "other" }).Where(a => a.Id == account.Id).ExecuteCommandAsync();
                throw new ModelAccessTokenUnauthorizedException();
            }
            token.Should().Be("concurrent");
            await _db.Client.Updateable<XaiAccount>().SetColumns(a => new XaiAccount { AccessToken = "latest" }).Where(a => a.Id == account.Id).ExecuteCommandAsync();
            throw new ModelAccessTokenUnauthorizedException();
        }
        var operation = () => ModelCredentialRecovery.FetchAsync(_db, _scope.ServiceProvider, snapshot, Fetch, default);
        await operation.Should().ThrowAsync<ModelAccessTokenUnauthorizedException>();
        _oauth.Calls.Should().Be(0);
        var saved = await _db.XaiAccounts.InSingleAsync(account.Id);
        saved.AccessToken.Should().Be("latest");
        saved.IsEnabled.Should().BeTrue();
        saved.RequiresReauth.Should().BeFalse();
    }

    [Fact]
    public async Task Without_refresh_token_the_rejected_credential_is_disabled()
    {
        var (account, snapshot) = await SeedAsync();
        await _db.Client.Updateable<XaiAccount>().SetColumns(a => new XaiAccount { RefreshToken = null }).Where(a => a.Id == account.Id).ExecuteCommandAsync();
        snapshot = snapshot with { RefreshToken = null };
        var operation = () => ModelCredentialRecovery.FetchAsync<string>(_db, _scope.ServiceProvider, snapshot,
            _ => throw new ModelAccessTokenUnauthorizedException(), default);
        await operation.Should().ThrowAsync<ModelAccessTokenUnauthorizedException>();
        (await _db.XaiAccounts.InSingleAsync(account.Id)).RequiresReauth.Should().BeTrue();
        _oauth.Calls.Should().Be(0);
    }

    private async Task<(XaiAccount, ForwardOAuthCredentialSnapshot)> SeedAsync()
    {
        var site = new Site { Name = "test", IsEnabled = true, ApiKey = "old" };
        await _db.Client.Insertable(site).ExecuteCommandAsync();
        var account = new XaiAccount { LinkedSiteId = site.Id, AccessToken = "old", RefreshToken = "refresh", IsEnabled = true };
        await _db.Client.Insertable(account).ExecuteCommandAsync();
        return (account, new("xai", account.Id, site.Id, account.AccessToken, account.RefreshToken, account.LastRefreshAt));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        try { File.Delete(_path); File.Delete(_path + "-wal"); File.Delete(_path + "-shm"); } catch (IOException) { }
    }

    private sealed class FakeXai : IXaiOAuthClient
    {
        public int Calls { get; private set; }
        public Exception? Error { get; set; }
        public Task<XaiDeviceCodeResponse> StartDeviceFlowAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<XaiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<XaiTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct)
        {
            Calls++;
            if (Error is not null) throw Error;
            return Task.FromResult(new XaiTokenSet { AccessToken = "new", RefreshToken = "rotated" });
        }
    }
}
