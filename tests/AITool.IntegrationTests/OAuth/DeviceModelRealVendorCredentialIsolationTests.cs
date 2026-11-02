using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
using AITool.Infrastructure.OAuth;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.OAuth;

/// <summary>Real controller and vendors against private SQLite and an entirely fake upstream.</summary>
public sealed class DeviceModelRealVendorCredentialIsolationTests : IDisposable
{
    private static readonly DateTimeOffset InitialRefresh = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset RotatedRefresh = new(2024, 2, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"device-model-isolation-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _siteId = Guid.NewGuid();
    private readonly Guid _otherAccountId = Guid.NewGuid();
    private readonly Guid _otherSiteId = Guid.NewGuid();

    public DeviceModelRealVendorCredentialIsolationTests()
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
    }

    // Each branch is tested independently: WorkBuddy probes in parallel and Global
    // has a fallback enterprise URL; ZCode reaches its subscription only after two
    // unauthenticated configuration requests.
    public static IEnumerable<object[]> Cases()
    {
        foreach (var endpoint in new[] { "workbuddy-cn-enterprise", "workbuddy-cn-v3", "workbuddy-global-enterprise", "workbuddy-global-fallback", "workbuddy-global-v3", "zcode-subscription" })
        foreach (var status in new[] { 401, 407 })
        foreach (var rotation in new[] { "none", "access", "refresh", "timestamp" })
            yield return [endpoint, status, rotation];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Catalog_rejection_only_disables_the_exact_account_snapshot(string endpoint, int status, string rotation)
    {
        var vendorKey = endpoint.StartsWith("workbuddy-cn") ? "workbuddy-cn"
            : endpoint.StartsWith("workbuddy-global") ? "workbuddy-global" : "zcode";
        await SeedAsync(vendorKey);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CatalogHandler(endpoint, (HttpStatusCode)status, entered, release);
        using var http = new HttpClient(handler);
        IDeviceOAuthVendor vendor = vendorKey switch
        {
            "workbuddy-cn" => new WorkBuddyOAuthVendor(http),
            "workbuddy-global" => new WorkBuddyOAuthVendor(http, global: true),
            _ => new ZCodeOAuthVendor(http)
        };
        var controller = new DeviceOAuthAccountsApiController(new DeviceOAuthVendorRegistry([vendor]), null!, null!, _db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _scope.ServiceProvider } }
        };

        var fetch = controller.FetchModels(_accountId, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await RotateAsync(rotation);
        }
        finally
        {
            release.TrySetResult(true);
        }

        if (status == 401)
        {
            var result = (await fetch).Should().BeOfType<ObjectResult>().Subject;
            result.StatusCode.Should().Be(401);
            JsonSerializer.Serialize(result.Value).Should().Contain("upstream_unauthorized");
        }
        else
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fetch);
        }

        handler.TargetRequests.Should().ContainSingle();
        if (vendorKey == "zcode")
            handler.PublicPaths.Should().Equal("/api/v1/client/configs", "/provider.json");
        else
            handler.OtherAuthenticatedRequests.Should().NotBeEmpty("both WorkBuddy probes are exercised");
        await AssertStateAsync(status == 401 && rotation == "none", rotation);
    }

    [Theory]
    [InlineData("configs", 401)]
    [InlineData("configs", 407)]
    [InlineData("provider", 401)]
    [InlineData("provider", 407)]
    public async Task ZCode_public_configuration_failures_do_not_reject_account_credentials(string endpoint, int status)
    {
        await SeedAsync("zcode");
        using var handler = new PublicConfigHandler(endpoint, (HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var vendor = new ZCodeOAuthVendor(http);
        var controller = new DeviceOAuthAccountsApiController(new DeviceOAuthVendorRegistry([vendor]), null!, null!, _db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _scope.ServiceProvider } }
        };

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => controller.FetchModels(_accountId, CancellationToken.None));
        handler.Paths.Should().Equal(endpoint == "configs"
            ? new[] { "/api/v1/client/configs" }
            : new[] { "/api/v1/client/configs", "/provider.json" });
        await AssertStateAsync(false, "none");
    }

    private sealed class PublicConfigHandler(string endpoint, HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.Contains("Authorization").Should().BeFalse();
            Paths.Add(uri.AbsolutePath);
            if (uri.Host == "zcode.z.ai" && uri.AbsolutePath == "/api/v1/client/configs")
                return Task.FromResult(endpoint == "configs"
                    ? Json(status, "{}")
                    : Json(HttpStatusCode.OK, "{\"code\":0,\"data\":{\"configs\":{\"builtin_provider_config_json\":\"https://config.example.invalid/provider.json\"}}}"));
            if (endpoint == "provider" && uri.Host == "config.example.invalid" && uri.AbsolutePath == "/provider.json")
                return Task.FromResult(Json(status, "{}"));
            throw new InvalidOperationException("Unexpected fake upstream request: " + uri);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body) };
    }

    private async Task SeedAsync(string vendorKey)
    {
        foreach (var (accountId, siteId, name) in new[]
                 { (_accountId, _siteId, "target"), (_otherAccountId, _otherSiteId, "other") })
        {
            await _db.InsertAsync(new Site
            {
                Id = siteId, Name = name, BaseUrl = "https://example.invalid", ApiKey = "old-access",
                IsEnabled = true, ManagedSource = "device_oauth"
            });
            // Deliberately identical credentials: identity and site, not just token, must isolate the update.
            await _db.InsertAsync(new DeviceOAuthAccount
            {
                Id = accountId, LinkedSiteId = siteId, VendorKey = vendorKey, DisplayName = name,
                AccessToken = "old-access", RefreshToken = "old-refresh", LastRefreshAt = InitialRefresh,
                IsEnabled = true
            });
        }
    }

    private async Task RotateAsync(string rotation)
    {
        if (rotation == "none") return;
        // A distinct scope represents a concurrent refresh committed after the HTTP send.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        switch (rotation)
        {
            case "access":
                await db.Client.Updateable<DeviceOAuthAccount>()
                    .SetColumns(a => new DeviceOAuthAccount { AccessToken = "new-access" })
                    .Where(a => a.Id == _accountId).ExecuteCommandAsync();
                await db.Client.Updateable<Site>().SetColumns(s => new Site { ApiKey = "new-access" })
                    .Where(s => s.Id == _siteId).ExecuteCommandAsync();
                break;
            case "refresh":
                await db.Client.Updateable<DeviceOAuthAccount>()
                    .SetColumns(a => new DeviceOAuthAccount { RefreshToken = "new-refresh" })
                    .Where(a => a.Id == _accountId).ExecuteCommandAsync();
                break;
            case "timestamp":
                await db.Client.Updateable<DeviceOAuthAccount>()
                    .SetColumns(a => new DeviceOAuthAccount { LastRefreshAt = RotatedRefresh })
                    .Where(a => a.Id == _accountId).ExecuteCommandAsync();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(rotation));
        }
    }

    private async Task AssertStateAsync(bool disabled, string rotation)
    {
        var account = await _db.Client.Queryable<DeviceOAuthAccount>().InSingleAsync(_accountId);
        account.IsEnabled.Should().Be(!disabled);
        account.RequiresReauth.Should().Be(disabled);
        account.AccessToken.Should().Be(rotation == "access" ? "new-access" : "old-access");
        account.RefreshToken.Should().Be(rotation == "refresh" ? "new-refresh" : "old-refresh");
        account.LastRefreshAt.Should().Be(rotation == "timestamp" ? RotatedRefresh : InitialRefresh);
        var site = await _db.Client.Queryable<Site>().InSingleAsync(_siteId);
        site.IsEnabled.Should().Be(!disabled);
        site.ApiKey.Should().Be(rotation == "access" ? "new-access" : "old-access");

        var other = await _db.Client.Queryable<DeviceOAuthAccount>().InSingleAsync(_otherAccountId);
        other.IsEnabled.Should().BeTrue();
        other.RequiresReauth.Should().BeFalse();
        other.AccessToken.Should().Be("old-access");
        other.RefreshToken.Should().Be("old-refresh");
        other.LastRefreshAt.Should().Be(InitialRefresh);
        var otherSite = await _db.Client.Queryable<Site>().InSingleAsync(_otherSiteId);
        otherSite.IsEnabled.Should().BeTrue();
        otherSite.ApiKey.Should().Be("old-access");
    }

    private sealed class CatalogHandler(
        string endpoint, HttpStatusCode status, TaskCompletionSource<bool> entered,
        TaskCompletionSource<bool> release) : HttpMessageHandler
    {
        public ConcurrentQueue<string> TargetRequests { get; } = new();
        public ConcurrentQueue<string> OtherAuthenticatedRequests { get; } = new();
        public ConcurrentQueue<string> PublicPaths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            request.Method.Should().Be(HttpMethod.Get);
            if (endpoint == "zcode-subscription" && uri.Host == "zcode.z.ai" && uri.AbsolutePath == "/api/v1/client/configs")
            {
                request.Headers.Contains("Authorization").Should().BeFalse();
                PublicPaths.Enqueue(uri.AbsolutePath);
                return Json(HttpStatusCode.OK, "{\"code\":0,\"data\":{\"configs\":{\"builtin_provider_config_json\":\"https://config.example.invalid/provider.json\"}}}");
            }
            if (endpoint == "zcode-subscription" && uri.Host == "config.example.invalid" && uri.AbsolutePath == "/provider.json")
            {
                request.Headers.Contains("Authorization").Should().BeFalse();
                PublicPaths.Enqueue(uri.AbsolutePath);
                return Json(HttpStatusCode.OK, "{}");
            }

            var expectedHost = endpoint.StartsWith("workbuddy-cn") ? "copilot.tencent.com"
                : endpoint.StartsWith("workbuddy-global") ? "www.workbuddy.ai" : "api.z.ai";
            uri.Scheme.Should().Be("https");
            uri.Host.Should().Be(expectedHost);
            request.Headers.TryGetValues("Authorization", out var values).Should().BeTrue();
            values!.Single().Should().Be(endpoint == "zcode-subscription" ? "old-access" : "Bearer old-access");
            var path = uri.AbsolutePath;
            var target = endpoint.EndsWith("-v3") ? path == "/v3/config"
                : endpoint.EndsWith("-fallback") ? path == "/console/enterprises/personal/models"
                : endpoint.EndsWith("-enterprise") ? path == (endpoint.StartsWith("workbuddy-cn")
                    ? "/console/enterprises/personal/models" : "/v2/enterprises/personal/models")
                : path == "/api/biz/subscription/list";
            if (target)
            {
                TargetRequests.Enqueue(path);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return Json(status, "{\"msg\":\"401 unauthorized\"}");
            }

            // Do not let an unexpected request escape to a real transport, and only
            // permit the other WorkBuddy probe (including Global's fallback).
            if (!endpoint.StartsWith("workbuddy") || path is not ("/v3/config" or "/console/enterprises/personal/models" or "/v2/enterprises/personal/models"))
                throw new InvalidOperationException("Unexpected fake upstream request: " + uri);
            OtherAuthenticatedRequests.Enqueue(path);
            return Json(HttpStatusCode.ProxyAuthenticationRequired, "{\"msg\":\"401 unauthorized\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body) };
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch (IOException) { }
    }
}
