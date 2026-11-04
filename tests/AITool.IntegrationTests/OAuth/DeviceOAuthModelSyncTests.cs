using System.Net;
using AITool.Infrastructure.OAuth;
using Microsoft.AspNetCore.Http;
using AITool.Application.Accounts;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

public sealed class DeviceOAuthModelSyncTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-device-oauth-models-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public DeviceOAuthModelSyncTests()
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

    [Fact]
    public async Task Sync_deduplicates_selections_applies_public_alias_and_preserves_unlisted_mappings()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vendor = new TestVendor("sync-test", chatSupported: true);
        var provisioner = CreateProvisioner(db);
        var account = await provisioner.ProvisionAsync(vendor, "test", null, "sync-user",
            new DeviceOAuthTokenSet { AccessToken = "at" }, null, CancellationToken.None);
        var extraItem = new AITool.Domain.Models.ModelLibraryItem { ModelName = "unlisted" };
        await db.InsertAsync(extraItem);
        var extraMapping = new AITool.Domain.SiteCatalog.SiteModelMapping
        {
            SiteId = account.LinkedSiteId,
            ModelLibraryItemId = extraItem.Id,
            RemoteModelName = "unlisted",
            IsEnabled = true
        };
        await db.InsertAsync(extraMapping);

        var inserted = await provisioner.SyncSelectedModelsAsync(account.Id,
            [("remote-a", "public-a", true), ("REMOTE-A", "public-b", true)], CancellationToken.None);
        inserted.Should().Be(1, "duplicate upstream names are applied once, with the last selection winning");

        var mapping = (await db.SiteModelMappings.Where(m => m.SiteId == account.LinkedSiteId && m.RemoteModelName == "remote-a").ToListAsync()).Should().ContainSingle().Subject;
        var item = await db.ModelLibraryItems.InSingleAsync(mapping.ModelLibraryItemId);
        item!.ModelName.Should().Be("public-b");
        item.DisplayName.Should().Be("public-b");
        (await db.SiteModelMappings.InSingleAsync(extraMapping.Id))!.IsEnabled.Should().BeTrue();

        var aliased = await provisioner.SyncSelectedModelsAsync(account.Id, [("remote-a", "public-c", true)], CancellationToken.None);
        aliased.Should().Be(1);
        mapping = (await db.SiteModelMappings.Where(m => m.SiteId == account.LinkedSiteId && m.RemoteModelName == "remote-a").ToListAsync()).Single();
        (await db.ModelLibraryItems.InSingleAsync(mapping.ModelLibraryItemId))!.ModelName.Should().Be("public-c");

        await provisioner.SyncSelectedModelsAsync(account.Id, [("remote-a", "public-c", false)], CancellationToken.None);
        mapping = (await db.SiteModelMappings.Where(m => m.SiteId == account.LinkedSiteId && m.RemoteModelName == "remote-a").ToListAsync()).Single();
        mapping.IsEnabled.Should().BeFalse();
        await provisioner.Invoking(p => p.SyncSelectedModelsAsync(account.Id, [], CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        (await db.SiteModelMappings.InSingleAsync(mapping.Id))!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_rejects_accounts_without_a_live_chat_site()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vendor = new TestVendor("view-only", chatSupported: false);
        var account = await CreateProvisioner(db).ProvisionAsync(vendor, "test", null, "read-only-user",
            new DeviceOAuthTokenSet { AccessToken = "at" }, null, CancellationToken.None);
        account.LinkedSiteId.Should().Be(Guid.Empty);

        await CreateProvisioner(db).Invoking(p => p.SyncSelectedModelsAsync(account.Id, [("remote-a", "public-a", true)], CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*仅供查看*");
    }

    [Fact]
    public async Task Batch_model_sync_skips_other_accounts_without_changing_their_mappings()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vendor = new TestVendor("batch-test", chatSupported: true);
        var provisioner = CreateProvisioner(db);
        var source = await provisioner.ProvisionAsync(vendor, "source", null, "source-user",
            new DeviceOAuthTokenSet { AccessToken = "source" }, null, CancellationToken.None);
        var other = await provisioner.ProvisionAsync(vendor, "other", null, "other-user",
            new DeviceOAuthTokenSet { AccessToken = "other" }, null, CancellationToken.None);
        await provisioner.SyncSelectedModelsAsync(other.Id, [("remote-a", "remote-a", true)], CancellationToken.None);

        var result = (OkObjectResult)await CreateController(db, provisioner, vendor).BatchApplyModels(
            new DeviceOAuthAccountsApiController.BatchApplyDeviceModelsRequest
            {
                SourceAccountId = source.Id,
                Models = [new() { RemoteModelName = "remote-a", Selected = false }, new() { RemoteModelName = "remote-b", Selected = true }]
            }, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.SerializeToDocument(result.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var data = json.RootElement.GetProperty("data");
        data.GetProperty("succeeded").GetInt32().Should().Be(1);
        data.GetProperty("failed").GetInt32().Should().Be(1);
        data.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("accountId").GetGuid() == other.Id)
            .GetProperty("error").GetString().Should().Contain("权限").And.Contain("跳过");
        var otherMappings = await db.SiteModelMappings.Where(m => m.SiteId == other.LinkedSiteId).ToListAsync();
        otherMappings.Should().ContainSingle(m => m.RemoteModelName == "remote-a" && m.IsEnabled);
        var sourceMappings = await db.SiteModelMappings.Where(m => m.SiteId == source.LinkedSiteId).ToListAsync();
        sourceMappings.Should().ContainSingle(m => m.RemoteModelName == "remote-b" && m.IsEnabled);
    }

    [Fact]
    public async Task Model_api_allows_view_only_fetch_but_rejects_import_and_missing_accounts()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vendor = new TestVendor("view-only", chatSupported: false);
        var provisioner = CreateProvisioner(db);
        var account = await provisioner.ProvisionAsync(vendor, "test", null, "api-user",
            new DeviceOAuthTokenSet { AccessToken = "at" }, null, CancellationToken.None);
        var controller = CreateController(db, provisioner, vendor);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider } };
        var vendors = (OkObjectResult)controller.GetVendors();
        var vendorsJson = System.Text.Json.JsonSerializer.SerializeToDocument(vendors.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        vendorsJson.RootElement.GetProperty("data")[0].GetProperty("modelFetchSupported").GetBoolean().Should().BeTrue();
        vendorsJson.RootElement.GetProperty("data")[0].GetProperty("modelCatalogScope").GetString().Should().Be("account");
        var publicController = CreateController(db, provisioner, new TestVendor("public", chatSupported: true, DeviceOAuthModelCatalogScope.PublicCatalog));
        var publicResult = (OkObjectResult)publicController.GetVendors();
        using var publicJson = System.Text.Json.JsonSerializer.SerializeToDocument(publicResult.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        publicJson.RootElement.GetProperty("data")[0].GetProperty("modelCatalogScope").GetString().Should().Be("public");

        (await controller.FetchModels(account.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var import = await controller.ImportModels(account.Id,
            new DeviceOAuthAccountsApiController.ImportDeviceModelsRequest
            {
                Models = [new DeviceOAuthAccountsApiController.DeviceModelSelectionItem { RemoteModelName = "remote-a", Selected = true }]
            }, CancellationToken.None);
        import.Should().BeOfType<BadRequestObjectResult>();

        var empty = await controller.ImportModels(Guid.NewGuid(), null, CancellationToken.None);
        empty.Should().BeOfType<NotFoundObjectResult>();
        var delete = await controller.DeleteAccount(Guid.NewGuid(), CancellationToken.None);
        delete.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Model_api_rejects_empty_import_for_chat_accounts_and_reports_import_capability()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vendor = new TestVendor("chat-fetch", chatSupported: true);
        var provisioner = CreateProvisioner(db);
        var account = await provisioner.ProvisionAsync(vendor, "test", null, "chat-user",
            new DeviceOAuthTokenSet { AccessToken = "at" }, null, CancellationToken.None);
        var controller = CreateController(db, provisioner, vendor);

        var summary = (OkObjectResult)await controller.GetAccounts(CancellationToken.None);
        var data = (System.Collections.IEnumerable)summary.Value!.GetType().GetProperty("Data")!.GetValue(summary.Value)!;
        var accountSummary = data.Cast<object>().Single();
        var json = System.Text.Json.JsonSerializer.SerializeToDocument(accountSummary,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.RootElement.GetProperty("modelImportSupported").GetBoolean().Should().BeTrue();

        var emptyImport = await controller.ImportModels(account.Id,
            new DeviceOAuthAccountsApiController.ImportDeviceModelsRequest(), CancellationToken.None);
        emptyImport.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(401, true)]
    [InlineData(407, false)]
    public async Task Model_api_only_disables_matching_credential_on_direct_401(int status, bool rotate)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new BlockingModelHandler(status, entered, release));
        var vendor = new CopilotOAuthVendor(http);
        var provisioner = CreateProvisioner(db);
        var account = await provisioner.ProvisionAsync(vendor, "target", null, "target-user",
            new DeviceOAuthTokenSet { AccessToken = "old-access" }, null, CancellationToken.None);
        var other = await provisioner.ProvisionAsync(vendor, "other", null, "other-user",
            new DeviceOAuthTokenSet { AccessToken = "other-access", RefreshToken = "other-refresh" }, null, CancellationToken.None);
        var controller = CreateController(db, provisioner, vendor);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider }
        };

        var fetchTask = controller.FetchModels(account.Id, CancellationToken.None);
        var completed = await Task.WhenAny(fetchTask, entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
        if (completed == fetchTask)
        {
            await fetchTask;
            Assert.Fail("Fetch completed before entered signal was observed.");
        }

        Exception? primaryException = null;
        IActionResult? fetchResult = null;
        try
        {
            if (rotate)
            {
                await using var rotationScope = _services.CreateAsyncScope();
                var rotationDb = rotationScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var current = await rotationDb.DeviceOAuthAccounts.InSingleAsync(account.Id);
                current!.AccessToken = "new-access";
                current.RefreshToken = "new-refresh";
                current.LastRefreshAt = DateTimeOffset.UtcNow;
                await rotationDb.UpdateAsync(current);
            }

            release.TrySetResult(true);

            if (status == 401)
            {
                fetchResult = await fetchTask.WaitAsync(TimeSpan.FromSeconds(10));
                var result = fetchResult.Should().BeOfType<ConflictObjectResult>().Subject;
                result.StatusCode.Should().Be(409);
                System.Text.Json.JsonSerializer.Serialize(result.Value).Should().Contain("oauth_reauthorization_required");
            }
            else
            {
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fetchTask.WaitAsync(TimeSpan.FromSeconds(10)));
            }

            var stored = await db.DeviceOAuthAccounts.InSingleAsync(account.Id);
            var shouldDisable = status == 401 && !rotate;
            stored!.IsEnabled.Should().Be(!shouldDisable);
            stored.RequiresReauth.Should().Be(shouldDisable);
            stored.AccessToken.Should().Be(rotate ? "new-access" : "old-access");
            (await db.Sites.InSingleAsync(account.LinkedSiteId))!.IsEnabled.Should().Be(!shouldDisable);
            var untouched = await db.DeviceOAuthAccounts.InSingleAsync(other.Id);
            untouched!.IsEnabled.Should().BeTrue();
            untouched.RequiresReauth.Should().BeFalse();
            (await db.Sites.InSingleAsync(other.LinkedSiteId))!.IsEnabled.Should().BeTrue();
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            release.TrySetResult(true);
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

    private sealed class BlockingModelHandler(int status, TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization!.ToString().Should().Be("Bearer old-access");
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
        }
    }

    private DeviceOAuthAccountProvisioner CreateProvisioner(AppDbContext db)
        => new(db,
            new ProxyRequestMetadataCache(_services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                new ServiceScopeFactoryAdapter(_services)),
            new SiteCascadeDeleter(db), NullLogger<DeviceOAuthAccountProvisioner>.Instance);

    private static DeviceOAuthAccountsApiController CreateController(AppDbContext db, DeviceOAuthAccountProvisioner provisioner, IDeviceOAuthVendor vendor)
        => new(new DeviceOAuthVendorRegistry([vendor]), provisioner, null!, db);

    public void Dispose()
    {
        _services.Dispose();
        TryDelete(_databasePath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + "-wal"); } catch { }
        try { File.Delete(path + "-shm"); } catch { }
    }

    private sealed class ServiceScopeFactoryAdapter(IServiceProvider services) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => services.CreateScope();
    }

    private sealed class TestVendor(string vendorKey, bool chatSupported, DeviceOAuthModelCatalogScope modelCatalogScope = DeviceOAuthModelCatalogScope.AccountScoped) : IDeviceOAuthVendor
    {
        public string VendorKey => vendorKey;
        public string Label => vendorKey;
        public DeviceOAuthChatSiteConfig? ChatSiteConfig => chatSupported
            ? new DeviceOAuthChatSiteConfig("https://models.example", "OpenAI", true, false, false, new Dictionary<string, string>())
            : null;
        public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => [];
        public bool SupportsModelFetch => true;
        public DeviceOAuthModelCatalogScope ModelCatalogScope => modelCatalogScope;
        public Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceOAuthRemoteModel>>([new("remote-a", "Remote A")]);
        public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
            => Task.FromResult(new DeviceOAuthQuotaResult());
        public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => null;
    }
}
