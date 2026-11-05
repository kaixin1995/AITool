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
        services.AddSqlSugar($"Data Source={_databasePath};Pooling=False");
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
    public async Task Startup_sync_merges_managed_headers_without_discarding_custom_headers()
    {
        var vendor = new HeaderedVendor();
        var account = await ProvisionWithStaleHeadersAsync(vendor,
            staleJson: """{"user-agent":"old","x-account-fingerprint":"old-fp","X-Custom":"keep"}""");

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);

        var headers = ReadSiteHeaders(account.LinkedSiteId);
        headers.Should().ContainKey("X-Custom").WhoseValue.Should().Be("keep");
        headers.Should().ContainKey("user-agent").WhoseValue.Should().Be("HeaderedCli/1.0");
        headers.Should().ContainKey("x-account-fingerprint").WhoseValue.Should().Be("fp-1");
        headers["X-AITool-Force-Stream"].Should().Be("true");
        headers.Keys.Count(k => k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Should().Be(1);

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);
        ReadSiteHeaders(account.LinkedSiteId).Should().BeEquivalentTo(headers);
    }

    [Fact]
    public async Task Startup_sync_does_not_overwrite_invalid_existing_json()
    {
        var account = await ProvisionWithStaleHeadersAsync(new HeaderedVendor(), staleJson: "not json");

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Sites.InSingleAsync(account.LinkedSiteId))!.ExtraHeadersJson.Should().Be("not json");
    }

    [Fact]
    public async Task Startup_sync_backfills_domain_identity_header_for_legacy_accounts()
    {
        // 存量账号：chatHeaders（旧登录遗留）不含 X-Domain，但 ExtraJson 已有 domain —— 同步时兜底注入。
        var vendor = new HeaderedVendor("workbuddy-cn", domainIdentityHeaderName: "X-Domain");
        _vendors.Add(vendor);
        var account = await ProvisionWithStaleHeadersAsync(vendor, staleJson: """{"User-Agent":"HeaderedCli/1.0"}""",
            extraJson: """{"chatHeaders":{"X-Account-Fingerprint":"fp-1"},"domain":"www.codebuddy.cn"}""");

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);

        var headers = ReadSiteHeaders(account.LinkedSiteId);
        headers["X-Domain"].Should().Be("www.codebuddy.cn", "存量账号从 extra.domain 兜底注入，无需重新扫码");
        headers["X-Account-Fingerprint"].Should().Be("fp-1");

        // chatHeaders 显式携带 X-Domain 时以账号值为准（不被兜底覆盖）。
        var explicitAccount = await ProvisionWithStaleHeadersAsync(vendor, staleJson: null,
            extraJson: """{"chatHeaders":{"X-Domain":"custom.example"},"domain":"www.codebuddy.cn"}""");
        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);
        ReadSiteHeaders(explicitAccount.LinkedSiteId)["X-Domain"].Should().Be("custom.example");
    }

    [Fact]
    public async Task Non_workbuddy_vendor_with_extra_domain_does_not_inject_x_domain_header()
    {
        // 非声明 domainIdentityHeaderName 的厂商（如默认 null）：即使 extra 携带 domain，
        // 供给与启动同步后均不得注入 X-Domain；但显式配置 chatHeaders.X-Domain 时应保留。
        var vendor = new HeaderedVendor("minimax-vendor", domainIdentityHeaderName: null);
        _vendors.Add(vendor);

        // ① 仅有 extra.domain，无显式 chatHeaders.X-Domain：供给及同步后均无 X-Domain
        var implicitAccount = await ProvisionWithStaleHeadersAsync(vendor, staleJson: null,
            extraJson: """{"chatHeaders":{"X-Account-Fingerprint":"fp-1"},"domain":"api.minimax.example"}""");
        var headersAfterProvision = ReadSiteHeaders(implicitAccount.LinkedSiteId);
        headersAfterProvision.ContainsKey("X-Domain").Should().BeFalse("未声明域身份头的厂商供给时不注入 X-Domain");

        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);
        var headersAfterSync = ReadSiteHeaders(implicitAccount.LinkedSiteId);
        headersAfterSync.ContainsKey("X-Domain").Should().BeFalse("未声明域身份头的厂商同步时也不注入 X-Domain");
        headersAfterSync["X-Account-Fingerprint"].Should().Be("fp-1");

        // ② 显式 chatHeaders 携带 X-Domain：供给与同步后均原样保留
        var explicitAccount = await ProvisionWithStaleHeadersAsync(vendor, staleJson: null,
            extraJson: """{"chatHeaders":{"X-Domain":"explicit.example","X-Account-Fingerprint":"fp-2"},"domain":"api.minimax.example"}""");
        var explicitHeaders = ReadSiteHeaders(explicitAccount.LinkedSiteId);
        explicitHeaders["X-Domain"].Should().Be("explicit.example", "显式配置的 X-Domain 必须保留");
        await CreateRefreshService().SyncManagedSiteHeadersAsync(CancellationToken.None);
        ReadSiteHeaders(explicitAccount.LinkedSiteId)["X-Domain"].Should().Be("explicit.example");
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

    [Fact]
    public async Task Legacy_default_names_are_normalized_but_custom_names_untouched()
    {
        const string legacy = "WorkBuddy（腾讯 CodeBuddy） 账号";
        var vendor = new HeaderedVendor("workbuddy-cn");
        var account = await ProvisionWithStaleHeadersAsync(vendor, staleJson: null);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            account.DisplayName = legacy;
            site!.Name = legacy;
            await db.Client.Updateable(account).UpdateColumns(a => new { a.DisplayName }).ExecuteCommandAsync();
            await db.Client.Updateable(site).UpdateColumns(s => new { s.Name }).ExecuteCommandAsync();
        }

        await CreateRefreshService().NormalizeLegacyDefaultNamesAsync(CancellationToken.None);

        await using (var verify = _services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var refreshed = await db.DeviceOAuthAccounts.InSingleAsync(account.Id);
            refreshed!.DisplayName.Should().Be("WorkBuddy 账号", "旧默认名精确替换为新默认名");
            (await db.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be("WorkBuddy 账号");
        }

        // 用户自定义名不包含旧默认名精确匹配 → 原样保留。
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var account2 = await ProvisionCustomNamedAccountAsync();
            await CreateRefreshService().NormalizeLegacyDefaultNamesAsync(CancellationToken.None);
            (await db.DeviceOAuthAccounts.InSingleAsync(account2.Id))!.DisplayName.Should().Be("我的 WorkBuddy（腾讯 CodeBuddy） 测试号");
        }
    }

    [Theory]
    [InlineData("workbuddy-cn", "WorkBuddy 账号")]
    [InlineData("WORKBUDDY-CN", "WorkBuddy 账号")]
    [InlineData("workbuddy-global", "WorkBuddy（腾讯 CodeBuddy） 账号")]
    [InlineData("other-vendor", "WorkBuddy（腾讯 CodeBuddy） 账号")]
    public async Task Legacy_name_migration_only_targets_the_mapped_vendor(string vendorKey, string expected)
    {
        const string legacy = "WorkBuddy（腾讯 CodeBuddy） 账号";
        var account = await ProvisionWithStaleHeadersAsync(new HeaderedVendor(vendorKey), staleJson: null);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            account.DisplayName = legacy;
            site!.Name = legacy;
            await db.Client.Updateable(account).UpdateColumns(a => new { a.DisplayName }).ExecuteCommandAsync();
            await db.Client.Updateable(site).UpdateColumns(s => new { s.Name }).ExecuteCommandAsync();
        }

        await CreateRefreshService().NormalizeLegacyDefaultNamesAsync(CancellationToken.None);
        await CreateRefreshService().NormalizeLegacyDefaultNamesAsync(CancellationToken.None);

        await using var verify = _services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.DeviceOAuthAccounts.InSingleAsync(account.Id))!.DisplayName.Should().Be(expected);
        (await verifyDb.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be(expected);
    }

    [Fact]
    public async Task Legacy_name_migration_does_not_rename_unmanaged_sites()
    {
        const string legacy = "WorkBuddy（腾讯 CodeBuddy） 账号";
        var account = await ProvisionWithStaleHeadersAsync(new HeaderedVendor("workbuddy-cn"), staleJson: null);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            account.DisplayName = legacy;
            site!.Name = legacy;
            site.ManagedSource = null;
            await db.Client.Updateable(account).UpdateColumns(a => new { a.DisplayName }).ExecuteCommandAsync();
            await db.Client.Updateable(site).UpdateColumns(s => new { s.Name, s.ManagedSource }).ExecuteCommandAsync();
        }

        await CreateRefreshService().NormalizeLegacyDefaultNamesAsync(CancellationToken.None);

        await using var verify = _services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.DeviceOAuthAccounts.InSingleAsync(account.Id))!.DisplayName.Should().Be("WorkBuddy 账号");
        (await verifyDb.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be(legacy);
    }

    [Fact]
    public async Task Legacy_name_migration_does_not_overwrite_concurrent_user_renames()
    {
        const string legacy = "WorkBuddy（腾讯 CodeBuddy） 账号";
        const string custom = "用户并发改名的 WorkBuddy 账号";
        const int accountCount = 4;
        var vendor = new HeaderedVendor("workbuddy-cn");

        var accounts = new List<DeviceOAuthAccount>(accountCount);
        for (var i = 0; i < accountCount; i++)
        {
            accounts.Add(await ProvisionWithStaleHeadersAsync(vendor, staleJson: null));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accountIds = accounts.Select(a => a.Id).ToArray();
            var siteIds = accounts.Select(a => a.LinkedSiteId).ToArray();
            await db.Client.Updateable<DeviceOAuthAccount>()
                .SetColumns(a => new DeviceOAuthAccount { DisplayName = legacy })
                .Where(a => accountIds.Contains(a.Id))
                .ExecuteCommandAsync();
            await db.Client.Updateable<Site>()
                .SetColumns(s => new Site { Name = legacy })
                .Where(s => siteIds.Contains(s.Id))
                .ExecuteCommandAsync();
        }

        // 本地 SQLite 的若干异步调用会同步完成，轮询“第一行已迁移”无法稳定拿到并发窗口；
        // 因此用 AOP 在迁移快照读取完成、首个 UPDATE 真正执行前挂起，稳定制造 TOCTOU 窗口。
        var sqlSugar = _services.GetRequiredService<SqlSugar.ISqlSugarClient>();
        using var releaseFirstMigrationUpdate = new ManualResetEventSlim(false);
        var firstMigrationUpdateBlocked = 0;
        var firstMigrationUpdateStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sqlSugar.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("DeviceOAuthAccounts", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("WHERE 1=0", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (Interlocked.CompareExchange(ref firstMigrationUpdateBlocked, 1, 0) != 0) return;
            firstMigrationUpdateStarted.TrySetResult(true);
            releaseFirstMigrationUpdate.Wait(TimeSpan.FromSeconds(15));
        };

        var refreshService = CreateRefreshService();
        var migration = Task.Run(() => refreshService.NormalizeLegacyDefaultNamesAsync(CancellationToken.None));
        var renamedByTest = new List<Guid>();
        try
        {
            await firstMigrationUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            using var client = db.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            await client.Ado.BeginTranAsync();
            try
            {
                // 先拿写锁，确保“读取仍为旧默认名的行 -> 改成用户自定义名”与迁移后续写入互斥。
                await client.Ado.ExecuteCommandAsync("UPDATE DeviceOAuthAccounts SET UpdatedAt=UpdatedAt WHERE 1=0;");
                var pending = await client.Queryable<DeviceOAuthAccount>()
                    .Where(a => !a.IsDeleted && a.DisplayName == legacy)
                    .ToListAsync();
                renamedByTest.AddRange(pending.Select(a => a.Id));
                if (pending.Count > 0)
                {
                    var accountIds = pending.Select(a => a.Id).ToArray();
                    var siteIds = pending.Select(a => a.LinkedSiteId).Where(id => id != Guid.Empty).ToArray();
                    await client.Updateable<DeviceOAuthAccount>()
                        .SetColumns(a => new DeviceOAuthAccount { DisplayName = custom, IsDisplayNameCustom = true })
                        .Where(a => accountIds.Contains(a.Id) && a.DisplayName == legacy)
                        .ExecuteCommandAsync();
                    await client.Updateable<Site>()
                        .SetColumns(s => new Site { Name = custom })
                        .Where(s => siteIds.Contains(s.Id)
                                    && s.Name == legacy
                                    && s.ManagedSource == DeviceOAuthAccountProvisioner.ManagedSource)
                        .ExecuteCommandAsync();
                }
                await client.Ado.CommitTranAsync();
            }
            catch
            {
                await client.Ado.RollbackTranAsync();
                throw;
            }
        }
        }
        finally
        {
            releaseFirstMigrationUpdate.Set();
            sqlSugar.Aop.OnLogExecuting = null;
        }

        renamedByTest.Should().NotBeEmpty("测试必须在迁移快照之后、后续行写入之前完成一次并发改名，否则回归无效");
        await migration.WaitAsync(TimeSpan.FromSeconds(15));

        await using (var verifyScope = _services.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var preserved = await db.DeviceOAuthAccounts
                .Where(a => renamedByTest.Contains(a.Id))
                .ToListAsync();
            preserved.Should().HaveCount(renamedByTest.Count);
            foreach (var account in preserved)
            {
                account.DisplayName.Should().Be(custom, "迁移不得覆盖快照读取后用户并发改写的账号名");
                account.IsDisplayNameCustom.Should().BeTrue();
                var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
                site.Should().NotBeNull();
                site!.Name.Should().Be(custom, "迁移不得覆盖快照读取后用户并发改写的托管站点名");
            }
        }
    }

    private async Task<DeviceOAuthAccount> ProvisionCustomNamedAccountAsync()
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
            new HeaderedVendor("workbuddy-cn"), "我的 WorkBuddy（腾讯 CodeBuddy） 测试号", null, $"u-{Guid.NewGuid():N}",
            new DeviceOAuthTokenSet { AccessToken = "at" }, null, CancellationToken.None);
        account.LinkedSiteId.Should().NotBe(Guid.Empty);
        return account;
    }

    private DeviceOAuthTokenRefreshService CreateRefreshService()
    {
        if (_vendors.Count == 0)
        {
            _vendors.Add(new HeaderedVendor());
        }
        return new DeviceOAuthTokenRefreshService(_services, NullLogger<DeviceOAuthTokenRefreshService>.Instance);
    }

    private async Task<DeviceOAuthAccount> ProvisionWithStaleHeadersAsync(HeaderedVendor vendor, string? staleJson, string? extraJson = null)
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
            extraJson ?? """{"chatHeaders":{"X-Account-Fingerprint":"fp-1"}}""",
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

    private sealed class HeaderedVendor(string vendorKey = "headered", string? domainIdentityHeaderName = null) : IDeviceOAuthVendor
    {
        public string VendorKey => vendorKey;
        public string Label => vendorKey;
        public DeviceOAuthChatSiteConfig? ChatSiteConfig => new(
            "https://chat.example/v1", "OpenAI", true, false, false,
            new Dictionary<string, string>
            {
                ["User-Agent"] = "HeaderedCli/1.0",
                ["X-AITool-Force-Stream"] = "true",
            })
        {
            DomainIdentityHeaderName = domainIdentityHeaderName,
        };
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
