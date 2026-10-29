using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// 托管站点请求头启动同步：升级引入的新 ChatSiteConfig 头（如 WorkBuddy 的
/// X-AITool-Force-Stream）必须无需重新登录即可写回既有隐藏站点。
/// </summary>
public sealed class DeviceOAuthSiteHeaderSyncTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-device-oauth-header-sync-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly List<IDeviceOAuthVendor> _vendors = [];

    public DeviceOAuthSiteHeaderSyncTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddSingleton(_ => new DeviceOAuthVendorRegistry(_vendors));
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    [Fact]
    public async Task Startup_sync_backfills_new_vendor_headers_and_keeps_account_level_headers()
    {
        var vendor = new HeaderedVendor();
        var account = await ProvisionWithStaleHeadersAsync(vendor, staleJson: """{"User-Agent":"HeaderedCli/1.0"}""");

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);

        var headers = ReadSiteHeaders(account.LinkedSiteId);
        headers["X-AITool-Force-Stream"].Should().Be("true", "升级新增的厂商头在启动时回填，无需重新登录");
        headers["User-Agent"].Should().Be("HeaderedCli/1.0");
        headers["X-Account-Fingerprint"].Should().Be("fp-1", "账号级 chatHeaders（指纹/身份）在同步后保留");
    }

    [Fact]
    public async Task Startup_sync_is_idempotent_when_headers_already_match()
    {
        var vendor = new HeaderedVendor();
        var account = await ProvisionWithStaleHeadersAsync(vendor, staleJson: null);

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);
        var afterFirst = ReadSiteHeaders(account.LinkedSiteId);
        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);
        var afterSecond = ReadSiteHeaders(account.LinkedSiteId);

        afterSecond.Should().BeEquivalentTo(afterFirst);
        afterSecond["X-AITool-Force-Stream"].Should().Be("true");
    }

    [Fact]
    public async Task Startup_sync_never_touches_non_managed_sites()
    {
        var vendor = new HeaderedVendor();
        var account = await ProvisionWithStaleHeadersAsync(vendor, staleJson: """{"User-Agent":"HeaderedCli/1.0"}""");

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            site!.ManagedSource = null; // 模拟用户自建站点被账号误关联
            await db.Client.Updateable(site).UpdateColumns(s => new { s.ManagedSource }).ExecuteCommandAsync();
        }

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);

        var headers = ReadSiteHeaders(account.LinkedSiteId);
        headers.ContainsKey("X-AITool-Force-Stream").Should().BeFalse("非托管站点不回写");
        headers["User-Agent"].Should().Be("HeaderedCli/1.0");
    }

    [Fact]
    public async Task Startup_sync_skips_accounts_of_unknown_vendors()
    {
        var provisionVendor = new HeaderedVendor();
        var account = await ProvisionWithStaleHeadersAsync(provisionVendor, staleJson: """{"User-Agent":"HeaderedCli/1.0"}""");

        // 注册表里只有另一个厂商：该账号应被跳过而非报错。
        _vendors.Add(new HeaderedVendor("other-vendor"));
        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);

        var headers = ReadSiteHeaders(account.LinkedSiteId);
        headers.ContainsKey("X-AITool-Force-Stream").Should().BeFalse();
    }

    private DeviceOAuthTokenRefreshService CreateRefreshService()
    {
        if (_vendors.Count == 0)
        {
            _vendors.Add(new HeaderedVendor());
        }
        return new DeviceOAuthTokenRefreshService(_services, NullLogger<DeviceOAuthTokenRefreshService>.Instance);
    }

    private async Task<DeviceOAuthAccount> ProvisionWithStaleHeadersAsync(HeaderedVendor vendor, string? staleJson)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provisioner = new DeviceOAuthAccountProvisioner(
            db,
            new ProxyRequestMetadataCache(_services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                new ServiceScopeFactoryAdapter(_services)),
            new SiteCascadeDeleter(db),
            NullLogger<DeviceOAuthAccountProvisioner>.Instance);

        var account = await provisioner.ProvisionAsync(
            vendor, "header-sync", null, $"u-{Guid.NewGuid():N}",
            new DeviceOAuthTokenSet { AccessToken = "at" },
            """{"chatHeaders":{"X-Account-Fingerprint":"fp-1"}}""",
            CancellationToken.None);
        account.LinkedSiteId.Should().NotBe(Guid.Empty);

        if (staleJson is not null)
        {
            // 模拟升级前登录遗留的旧站点头（缺新头）。
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            site!.ExtraHeadersJson = staleJson;
            await db.Client.Updateable(site).UpdateColumns(s => new { s.ExtraHeadersJson }).ExecuteCommandAsync();
        }

        return account;
    }

    private Dictionary<string, string> ReadSiteHeaders(Guid siteId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = db.Sites.InSingleAsync(siteId).GetAwaiter().GetResult();
        site.Should().NotBeNull();
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(site!.ExtraHeadersJson!)!;
    }

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

    private sealed class HeaderedVendor(string vendorKey = "headered") : IDeviceOAuthVendor
    {
        public string VendorKey => vendorKey;
        public string Label => vendorKey;
        public DeviceOAuthChatSiteConfig? ChatSiteConfig => new(
            "https://chat.example/v1", "OpenAI", true, false, false,
            new Dictionary<string, string>
            {
                ["User-Agent"] = "HeaderedCli/1.0",
                ["X-AITool-Force-Stream"] = "true",
            });
        public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => [];
        public bool SupportsModelFetch => true;
        public Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceOAuthRemoteModel>>([]);
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
