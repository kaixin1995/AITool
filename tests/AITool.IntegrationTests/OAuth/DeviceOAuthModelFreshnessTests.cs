using System.Collections.Concurrent;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

public sealed class DeviceOAuthModelFreshnessTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"model-fresh-{Guid.NewGuid():N}.db");
    private readonly FakeVendor _vendor = new();
    private readonly ServiceProvider _services;

    public DeviceOAuthModelFreshnessTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_path}");
        // Independent connections model separate web requests without sharing a SqlSugarScope async context.
        var rootClient = (SqlSugar.ISqlSugarClient)services.Last(d => d.ServiceType == typeof(SqlSugar.ISqlSugarClient)).ImplementationInstance!;
        services.AddScoped<SqlSugar.ISqlSugarClient>(_ => rootClient.CopyNew());
        services.AddSingleton(new DeviceOAuthVendorRegistry([_vendor]));
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddScoped<ForwardOAuthInvalidationService>();
        services.AddScoped<DeviceOAuthCredentialRefreshService>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    [Fact]
    public async Task Fresh_token_is_used_without_refresh()
    {
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddHours(1));
        (await FetchAsync(id)).Should().BeOfType<OkObjectResult>();
        _vendor.RefreshCalls.Should().Be(0);
        _vendor.SeenAccess.Should().Equal("old");
    }

    [Theory]
    [InlineData(null, "refresh")]
    [InlineData("rotated", "rotated")]
    public async Task Expired_token_is_rotated_and_persisted(string? returnedRefresh, string expectedRefresh)
    {
        _vendor.ReturnedRefresh = returnedRefresh;
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        (await FetchAsync(id)).Should().BeOfType<OkObjectResult>();
        _vendor.RefreshCalls.Should().Be(1);
        _vendor.SeenRefresh.Should().Equal("refresh");
        _vendor.SeenAccess.Should().Equal("new");
        using var scope = _services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeviceOAuthAccounts.InSingleAsync(id);
        account.AccessToken.Should().Be("new");
        account.RefreshToken.Should().Be(expectedRefresh);
        account.TokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(30));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_rejected_refresh_does_not_fetch_models(bool refused)
    {
        _vendor.RefuseRefresh = refused;
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddMinutes(-1), refused ? "refresh" : null);
        var result = (await FetchAsync(id)).Should().BeOfType<ConflictObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(result.Value).Should().Contain("oauth_reauthorization_required");
        _vendor.RefreshCalls.Should().Be(refused ? 1 : 0);
        _vendor.SeenAccess.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unauthorized_forces_one_refresh_and_only_one_retry(bool rejectRetry)
    {
        _vendor.RejectOld = true;
        _vendor.RejectAll = rejectRetry;
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddHours(1));
        var result = await FetchAsync(id);
        if (rejectRetry) result.Should().BeOfType<ConflictObjectResult>();
        else result.Should().BeOfType<OkObjectResult>();
        _vendor.RefreshCalls.Should().Be(1);
        _vendor.SeenAccess.Should().Equal("old", "new");
        using var scope = _services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeviceOAuthAccounts.InSingleAsync(id);
        account.RequiresReauth.Should().Be(rejectRetry);
        account.IsEnabled.Should().Be(!rejectRetry);
    }

    [Fact]
    public async Task Refused_refresh_after_unauthorized_only_sends_one_model_request()
    {
        _vendor.RejectOld = true;
        _vendor.RefuseRefresh = true;
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddHours(1));
        (await FetchAsync(id)).Should().BeOfType<ConflictObjectResult>();
        _vendor.SeenAccess.Should().Equal("old");
        _vendor.RefreshCalls.Should().Be(1);
        using var scope = _services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeviceOAuthAccounts.InSingleAsync(id);
        account.RequiresReauth.Should().BeTrue();
        account.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_refreshed_access_token_is_not_persisted()
    {
        _vendor.ReturnedAccess = "";
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var result = (await FetchAsync(id)).Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(503);
        _vendor.SeenAccess.Should().BeEmpty();
        using var scope = _services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeviceOAuthAccounts.InSingleAsync(id);
        account.AccessToken.Should().Be("old");
        account.RefreshToken.Should().Be("refresh");
        account.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transient_refresh_error_is_sanitized_and_does_not_disable_account(bool afterUnauthorized)
    {
        _vendor.Error = new HttpRequestException("sensitive-upstream-body");
        _vendor.RejectOld = afterUnauthorized;
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddMinutes(afterUnauthorized ? 60 : -1));
        var result = (await FetchAsync(id)).Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(503);
        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);
        body.Should().Contain("oauth_refresh_unavailable").And.NotContain("sensitive-upstream-body");
        _vendor.SeenAccess.Should().HaveCount(afterUnauthorized ? 1 : 0);
        using var scope = _services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeviceOAuthAccounts.InSingleAsync(id);
        account.IsEnabled.Should().BeTrue();
        account.RequiresReauth.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_fetches_share_the_refresh_gate()
    {
        _vendor.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _vendor.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = await SeedAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var queued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQueued(Guid accountId) { if (accountId == id) queued.TrySetResult(true); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        IActionResult[] results;
        try
        {
            var first = Task.Run(() => FetchAsync(id));
            await _vendor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = Task.Run(() => FetchAsync(id));
            try { await queued.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { _vendor.Release.TrySetResult(true); }
            results = await Task.WhenAll(first, second);
        }
        finally { DeviceOAuthRefreshGate.Queued -= OnQueued; }
        results.Should().OnlyContain(r => r is OkObjectResult);
        _vendor.RefreshCalls.Should().Be(1);
        _vendor.SeenAccess.Should().HaveCount(2).And.OnlyContain(t => t == "new");
    }

    private async Task<Guid> SeedAsync(DateTimeOffset expiry, string? refresh = "refresh")
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = new DeviceOAuthAccount { VendorKey = _vendor.VendorKey, DisplayName = "test", AccessToken = "old", RefreshToken = refresh, TokenExpiresAt = expiry, IsEnabled = true };
        await db.InsertAsync(account);
        return account.Id;
    }

    private async Task<IActionResult> FetchAsync(Guid id)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var controller = new DeviceOAuthAccountsApiController(scope.ServiceProvider.GetRequiredService<DeviceOAuthVendorRegistry>(), null!, null!, db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider } }
        };
        return await controller.FetchModels(id, CancellationToken.None);
    }

    public void Dispose()
    {
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch (IOException) { }
    }

    private sealed class FakeVendor : IDeviceOAuthVendor
    {
        public int RefreshCalls;
        public string? ReturnedRefresh = "rotated";
        public string ReturnedAccess = "new";
        public bool RefuseRefresh;
        public bool RejectOld;
        public bool RejectAll;
        public Exception? Error;
        public TaskCompletionSource<bool>? Entered;
        public TaskCompletionSource<bool>? Release;
        public ConcurrentQueue<string> SeenAccess { get; } = new();
        public ConcurrentQueue<string> SeenRefresh { get; } = new();
        public string VendorKey => "freshness-test";
        public string Label => VendorKey;
        public DeviceOAuthChatSiteConfig? ChatSiteConfig => null;
        public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => [];
        public bool SupportsModelFetch => true;
        public async Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RefreshCalls);
            SeenRefresh.Enqueue(refreshToken);
            Entered?.TrySetResult(true);
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if (RefuseRefresh) throw new DeviceOAuthRefreshRefusedException("refused");
            if (Error is not null) throw Error;
            return new DeviceOAuthTokenSet { AccessToken = ReturnedAccess, RefreshToken = ReturnedRefresh, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }
        public Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
        {
            SeenAccess.Enqueue(token.AccessToken);
            if (RejectAll || (RejectOld && token.AccessToken == "old")) throw new ModelAccessTokenUnauthorizedException();
            return Task.FromResult<IReadOnlyList<DeviceOAuthRemoteModel>>([new("model", "Model")]);
        }
        public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => null;
    }
}
