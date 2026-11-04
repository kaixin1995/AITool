using System.Net;
using AITool.Application.Accounts;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
using AITool.Infrastructure.OAuth;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.OAuth;

/// <summary>Real controller and vendor HTTP paths, with an isolated database and no network transport.</summary>
public sealed class DeviceOAuthModelUnauthorizedIsolationTests : IDisposable
{
    private const string OldAccess = "old-access";
    // No refresh token: these cases isolate invalidation of the credential actually sent.
    private const string? OldRefresh = null;
    private static readonly DateTimeOffset InitialRefresh = new(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RotatedRefresh = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-model-isolation-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public DeviceOAuthModelUnauthorizedIsolationTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddScoped<ForwardOAuthInvalidationService>();
        services.AddSingleton(new DeviceOAuthVendorRegistry([]));
        services.AddScoped<DeviceOAuthCredentialRefreshService>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    public static IEnumerable<object[]> Cases()
    {
        // Kiro can reject either management call; Trae can reject batch or fallback.
        foreach (var path in new[] { "kiro-profile", "kiro-models", "trae-cn-batch", "trae-cn-fallback", "trae-global-batch", "trae-global-fallback" })
        foreach (var linkedSite in new[] { false, true })
        {
            foreach (var rotation in new[] { "none", "access", "refresh", "timestamp", "all" })
                yield return [path, linkedSite, 401, rotation];
            foreach (var rotation in new[] { "none", "all" })
                yield return [path, linkedSite, 407, rotation];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Fetch_models_only_invalidates_the_exact_sent_credential(string path, bool linkedSite, int status, string rotation)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        using var handler = new BlockingHandler(path, status);
        using var http = new HttpClient(handler);
        IDeviceOAuthVendor vendor = path.StartsWith("kiro", StringComparison.Ordinal)
            ? new KiroOAuthVendor(http)
            : new TraeOAuthVendor(http, global: path.StartsWith("trae-global", StringComparison.Ordinal));
        var provisioner = new DeviceOAuthAccountProvisioner(db,
            new ProxyRequestMetadataCache(_services.GetRequiredService<IMemoryCache>(), _services.GetRequiredService<IServiceScopeFactory>()),
            new SiteCascadeDeleter(db), NullLogger<DeviceOAuthAccountProvisioner>.Instance);
        var tokens = new DeviceOAuthTokenSet { AccessToken = OldAccess, RefreshToken = OldRefresh };
        var target = await provisioner.ProvisionAsync(vendor, "target", null, "target", tokens, null, CancellationToken.None);
        var other = await provisioner.ProvisionAsync(vendor, "other", null, "other", tokens, null, CancellationToken.None);

        // These real vendors are view-only and normally have no site. A synthetic linked site
        // additionally exercises the generic invalidator's site branch without claiming the
        // vendor provisions one. The other account deliberately shares the same credentials.
        target.LinkedSiteId.Should().Be(Guid.Empty);
        other.LinkedSiteId.Should().Be(Guid.Empty);
        var targetSite = new Site { Name = "target-site", BaseUrl = "https://target.invalid", ApiKey = OldAccess };
        var otherSite = new Site { Name = "other-site", BaseUrl = "https://other.invalid", ApiKey = OldAccess };
        var unrelatedSite = new Site { Name = "unrelated-site", BaseUrl = "https://unrelated.invalid" };
        if (linkedSite)
        {
            await db.InsertAsync(targetSite);
            target.LinkedSiteId = targetSite.Id;
        }
        await db.InsertAsync(otherSite);
        await db.InsertAsync(unrelatedSite);
        other.LinkedSiteId = otherSite.Id;
        target.LastRefreshAt = InitialRefresh;
        other.LastRefreshAt = InitialRefresh;
        await db.UpdateAsync(target);
        await db.UpdateAsync(other);

        var controller = new DeviceOAuthAccountsApiController(new DeviceOAuthVendorRegistry([vendor]), provisioner, null!, db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider } }
        };
        var fetchTask = controller.FetchModels(target.Id, CancellationToken.None);
        var completed = await Task.WhenAny(fetchTask, handler.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
        if (completed == fetchTask)
        {
            await fetchTask;
            Assert.Fail("Fetch completed before entered signal was observed.");
        }

        Exception? primaryException = null;
        try
        {
            handler.AssertBlockedRequest();
            if (rotation != "none")
            {
                await using var rotationScope = _services.CreateAsyncScope();
                var rotationDb = rotationScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var current = (await rotationDb.DeviceOAuthAccounts.InSingleAsync(target.Id))!;
                if (rotation is "access" or "all") current.AccessToken = "new-access";
                if (rotation is "refresh" or "all") current.RefreshToken = "new-refresh";
                if (rotation is "timestamp" or "all") current.LastRefreshAt = RotatedRefresh;
                await rotationDb.UpdateAsync(current);
                if (linkedSite && (rotation is "access" or "all"))
                {
                    var rotatedSite = (await rotationDb.Sites.InSingleAsync(targetSite.Id))!;
                    rotatedSite.ApiKey = "new-access";
                    await rotationDb.UpdateAsync(rotatedSite);
                }
            }

            handler.Release.TrySetResult(true);

            if (status == 401)
            {
                var fetchResult = await fetchTask.WaitAsync(TimeSpan.FromSeconds(10));
                var result = fetchResult.Should().BeOfType<ConflictObjectResult>().Subject;
                result.StatusCode.Should().Be(409);
                var body = result.Value.Should().BeOfType<ApiResponse>().Subject;
                body.Success.Should().BeFalse();
                body.ErrorCode.Should().Be("oauth_reauthorization_required");
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fetchTask.WaitAsync(TimeSpan.FromSeconds(10)));
                error.Message.Should().Contain("407");
            }

            var expectedDisabled = status == 401 && rotation == "none";
            var stored = (await db.DeviceOAuthAccounts.InSingleAsync(target.Id))!;
            stored.IsEnabled.Should().Be(!expectedDisabled);
            stored.RequiresReauth.Should().Be(expectedDisabled);
            stored.AccessToken.Should().Be(rotation is "access" or "all" ? "new-access" : OldAccess);
            stored.RefreshToken.Should().Be(rotation is "refresh" or "all" ? "new-refresh" : OldRefresh);
            stored.LastRefreshAt.Should().Be(rotation is "timestamp" or "all" ? RotatedRefresh : InitialRefresh);
            stored.LinkedSiteId.Should().Be(linkedSite ? targetSite.Id : Guid.Empty);
            if (linkedSite)
            {
                var storedSite = (await db.Sites.InSingleAsync(targetSite.Id))!;
                storedSite.IsEnabled.Should().Be(!expectedDisabled);
                storedSite.ApiKey.Should().Be(rotation is "access" or "all" ? "new-access" : OldAccess);
            }
            var untouched = (await db.DeviceOAuthAccounts.InSingleAsync(other.Id))!;
            untouched.IsEnabled.Should().BeTrue();
            untouched.RequiresReauth.Should().BeFalse();
            untouched.AccessToken.Should().Be(OldAccess);
            untouched.RefreshToken.Should().Be(OldRefresh);
            untouched.LastRefreshAt.Should().Be(InitialRefresh);
            var storedOtherSite = (await db.Sites.InSingleAsync(otherSite.Id))!;
            storedOtherSite.IsEnabled.Should().BeTrue();
            storedOtherSite.ApiKey.Should().Be(OldAccess);
            (await db.Sites.InSingleAsync(unrelatedSite.Id))!.IsEnabled.Should().BeTrue();
            handler.AssertRequests();
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            handler.Release.TrySetResult(true);
            try
            {
                await fetchTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch when (primaryException is not null)
            {
                // Primary exception takes precedence.
            }
            catch (InvalidOperationException) when (status != 401)
            {
                // Expected exception already observed in try block.
            }
        }
    }

    private sealed class BlockingHandler(string path, int status) : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(string Method, string Host, string Path, string Authorization)> _requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            _requests.Add((request.Method.Method, uri.Host, uri.AbsolutePath,
                request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : ""));
            if (path == "kiro-models" && uri.AbsolutePath == "/List-Available-Profiles")
                return Json(HttpStatusCode.OK, "{\"profiles\":[{\"arn\":\"arn:aws:codewhisperer:us-east-1:123:profile/test\"}]}");
            if (path.EndsWith("fallback", StringComparison.Ordinal) && uri.AbsolutePath == "/api/ide/v1/batch_get_detail_param")
                return Json(HttpStatusCode.OK, "{}");

            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return Json((HttpStatusCode)status, "{}");
        }

        public void AssertBlockedRequest()
        {
            var kiro = path.StartsWith("kiro", StringComparison.Ordinal);
            _requests.Last().Authorization.Should().Be((kiro ? "Bearer " : "Cloud-IDE-JWT ") + OldAccess);
            var expectedPath = path switch
            {
                "kiro-profile" => "/List-Available-Profiles",
                "kiro-models" => "/List-Available-Models",
                _ when path.EndsWith("batch", StringComparison.Ordinal) => "/api/ide/v1/batch_get_detail_param",
                _ => "/api/ide/v1/get_detail_param"
            };
            _requests.Last().Path.Should().Be(expectedPath);
        }

        public void AssertRequests()
        {
            var kiro = path.StartsWith("kiro", StringComparison.Ordinal);
            var host = kiro ? "management.us-east-1.kiro.dev"
                : path.StartsWith("trae-global", StringComparison.Ordinal) ? "coresg-normal.trae.ai" : "trae-api-cn.mchost.guru";
            var authorization = (kiro ? "Bearer " : "Cloud-IDE-JWT ") + OldAccess;
            _requests.Should().OnlyContain(r => r.Host == host && r.Method == (kiro ? "GET" : "POST") && r.Authorization == authorization);
            var expectedPaths = path switch
            {
                "kiro-profile" => new[] { "/List-Available-Profiles" },
                "kiro-models" => new[] { "/List-Available-Profiles", "/List-Available-Models" },
                _ when path.EndsWith("batch", StringComparison.Ordinal) => new[] { "/api/ide/v1/batch_get_detail_param" },
                _ when status == 401 => new[] { "/api/ide/v1/batch_get_detail_param", "/api/ide/v1/get_detail_param" },
                _ => new[] { "/api/ide/v1/batch_get_detail_param", "/api/ide/v1/get_detail_param", "/api/ide/v1/get_detail_param", "/api/ide/v1/get_detail_param", "/api/ide/v1/get_detail_param", "/api/ide/v1/get_detail_param" }
            };
            _requests.Select(r => r.Path).Should().Equal(expectedPaths);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string text)
            => new(status) { Content = new StringContent(text) };
    }

    public void Dispose()
    {
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); } catch (IOException) { }
        }
    }
}
