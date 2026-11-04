using AITool.Application.Codex;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Infrastructure.Codex;
using AITool.Domain.Sites;
using AITool.Infrastructure.OAuth;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Persistence;

/// <summary>
/// 旧库升级迁移测试：用「本轮新增列之前的旧表结构 + 已有数据」建库，再跑 CodeFirst
/// InitializeDatabase（只增不删），验证——①加列/建表不报错（SQLite 对有数据表加
/// NOT NULL 列必须带默认值，否则 ALTER 失败导致整个应用启动崩溃）；②旧行的新列
/// 取到安全默认值（新开关全部 false、新可空列 null）；③旧数据完整保留。
/// 这是生产库原地升级（v1.0.1.x → 本版本）的唯一离线验证手段。
/// </summary>
public sealed class LegacySchemaMigrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-legacy-mig-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Existing_database_upgrades_in_place_with_new_columns_and_defaults()
    {
        // ① 旧结构建库：本轮改动前的核心表（Site/SiteKey 无新列；SystemRuntimeSettings 无 5 个新开关；
        //    无 DeviceOAuthAccounts 表）。列清单取自 git 基线 cc8f000 的实体定义。
        var dbFile = _databasePath;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Sites (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    BaseUrl TEXT NOT NULL,
                    EndpointPathMode TEXT NOT NULL DEFAULT 'standard-root',
                    ApiKey TEXT,
                    ProtocolType TEXT NOT NULL,
                    SupportsOpenAi INTEGER NOT NULL DEFAULT 0,
                    SupportsAnthropic INTEGER NOT NULL DEFAULT 0,
                    SupportsResponses INTEGER NOT NULL DEFAULT 0,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    ManagedSource TEXT,
                    ExtraHeadersJson TEXT,
                    ClientEmulation TEXT NOT NULL DEFAULT 'None',
                    EgressProxyUrl TEXT,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE SiteKeys (
                    Id TEXT PRIMARY KEY,
                    SiteId TEXT NOT NULL,
                    KeyValue TEXT NOT NULL,
                    Remark TEXT,
                    Priority INTEGER NOT NULL DEFAULT 0,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    CreatedAt TEXT NOT NULL,
                    LastQuotaRawJson TEXT,
                    LastQuotaCheckedAt TEXT,
                    LastQuotaStatus TEXT,
                    LastQuotaError TEXT
                );
                CREATE TABLE SystemRuntimeSettings (
                    Id INTEGER PRIMARY KEY,
                    ProxyRequestTimeoutSeconds INTEGER NOT NULL DEFAULT 60,
                    ProxyRetryCount INTEGER NOT NULL DEFAULT 1,
                    RateLimitRetryCount INTEGER NOT NULL DEFAULT 0,
                    CircuitBreakerFailureThreshold INTEGER NOT NULL DEFAULT 5,
                    CircuitBreakerRecoveryMinutes INTEGER NOT NULL DEFAULT 2,
                    OAuthFeaturesEnabled INTEGER NOT NULL DEFAULT 0,
                    OAuthInspectionEnabled INTEGER NOT NULL DEFAULT 0,
                    UsageLogAutoCleanupEnabled INTEGER NOT NULL DEFAULT 1
                );
                INSERT INTO Sites (Id, Name, BaseUrl, ProtocolType, CreatedAt, UpdatedAt)
                    VALUES ('11111111-1111-1111-1111-111111111111', '旧站点', 'https://old.example.com', 'OpenAI', '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');
                INSERT INTO SiteKeys (Id, SiteId, KeyValue, CreatedAt)
                    VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'old-key', '2026-01-01T00:00:00+00:00');
                INSERT INTO SystemRuntimeSettings (Id) VALUES (1);
                """;
            command.ExecuteNonQuery();
        }

        // ② 以旧库为基础跑 CodeFirst 初始化（应用启动路径）。此步抛异常 = 生产升级必炸。
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={dbFile}");
        var provider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(provider.GetRequiredService<SqlSugar.ISqlSugarClient>());

        // ③ 原始 SQL 二分定位：行是否物理保留、新列是否已加（区分「CodeFirst 重建了表」与「ORM 读映射问题」）。
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}"))
        {
            raw.Open();
            using var check = raw.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM Sites;";
            var siteCount = (long)(check.ExecuteScalar() ?? 0L);
            siteCount.Should().Be(1, "迁移后旧 Site 行必须物理保留（0 = CodeFirst 重建了表，数据丢失）");
        }

        // ④ 旧数据保留 + 新列默认值安全（经 ORM 读回）。
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var site = await db.Sites.FirstAsync(s => s.Name == "旧站点");
            site.Should().NotBeNull("旧站点必须原样保留");
            site!.Name.Should().Be("旧站点");
            site.QuotaBalanceJson.Should().BeNull("新增可空列默认 null（未配置自定义余额）");

            var key = await db.SiteKeys.InSingleAsync(Guid.Parse("22222222-2222-2222-2222-222222222222"));
            key.Should().NotBeNull();
            key!.EgressProxyUrl.Should().BeNull("密钥级代理默认 null（继承站点级）");

            var settings = await db.SystemRuntimeSettings.Where(s => s.Id == 1).FirstAsync();
            settings.Should().NotBeNull();
            settings!.RouteQuotaOrderingEnabled.Should().BeFalse("新路由开关默认关——升级后行为与旧版一致");
            settings.SessionAffinityEnabled.Should().BeFalse();
            settings.TieredRouteCooldownEnabled.Should().BeFalse();
            settings.StrictClientErrorFallbackEnabled.Should().BeFalse();
            settings.StreamHeadBufferEnabled.Should().BeFalse();

            // 新表可写可读（空表）。
            (await db.DeviceOAuthAccounts.CountAsync()).Should().Be(0);
        }

        provider.Dispose();
    }


    /// <summary>
    /// CodexAccounts 加列升级专项（IsDisplayNameCustom 非空 bool 列）：
    /// SQLite 对有数据表加 NOT NULL 列必须带默认值，否则 ALTER 失败导致应用启动崩溃；
    /// 旧名称来源无法识别，旧行保守标记为自定义并在重登时保留。
    /// </summary>
    [Fact]
    public async Task Codex_accounts_table_upgrades_with_display_name_custom_flag()
    {
        var dbFile = _databasePath;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE CodexAccounts (
                    Id TEXT PRIMARY KEY,
                    DisplayName TEXT NOT NULL,
                    Email TEXT,
                    AccountId TEXT,
                    PlanType TEXT,
                    AccessToken TEXT,
                    RefreshToken TEXT,
                    IdToken TEXT,
                    TokenExpiresAt TEXT,
                    LastRefreshAt TEXT,
                    LinkedSiteId TEXT NOT NULL,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    DisabledByFeatureToggle INTEGER NOT NULL DEFAULT 0,
                    ManuallyDisabled INTEGER NOT NULL DEFAULT 0,
                    AutoDisableThreshold TEXT,
                    IsQuotaCooling INTEGER NOT NULL DEFAULT 0,
                    QuotaCoolingUntil TEXT,
                    LastQuotaRawJson TEXT,
                    LastQuotaCheckedAt TEXT,
                    CreatedAt TEXT NOT NULL
                );
                INSERT INTO CodexAccounts (Id, DisplayName, Email, AccessToken, RefreshToken, LinkedSiteId, CreatedAt)
                    VALUES ('33333333-3333-3333-3333-333333333333', '旧Codex账号', 'dev@example.com', 'at', 'rt', '44444444-4444-4444-4444-444444444444', '2026-01-01T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={dbFile}");
        var provider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(provider.GetRequiredService<SqlSugar.ISqlSugarClient>());

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var account = await db.CodexAccounts.InSingleAsync(Guid.Parse("33333333-3333-3333-3333-333333333333"));
            account.Should().NotBeNull("旧 Codex 账号必须原样保留");
            account!.IsDisplayNameCustom.Should().BeTrue("旧名称的来源无法识别，必须保守保护");
            account.DisplayName.Should().Be("旧Codex账号");
            await db.InsertAsync(new Site { Id = account.LinkedSiteId, Name = account.DisplayName, BaseUrl = "https://example.com", ApiKey = "at" });

            using var cacheServices = new ServiceCollection().AddMemoryCache().AddLogging().AddScoped<ProxyRequestMetadataCache>().BuildServiceProvider();
            using var cacheScope = cacheServices.CreateScope();
            var provisioner = new CodexAccountProvisioner(db, cacheScope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
                new CodexModelCatalog(new HttpClient(), NullLogger<CodexModelCatalog>.Instance),
                new SiteCascadeDeleter(db), NullLogger<CodexAccountProvisioner>.Instance);
            var relogin = await provisioner.ProvisionFromTokensAsync(new CodexProvisionInput
            {
                DisplayName = null, Email = "dev@example.com", PlanType = "plus", AccessToken = "new-at", RefreshToken = "new-rt",
            }, CancellationToken.None);
            relogin.Id.Should().Be(account.Id);
            var saved = await db.CodexAccounts.InSingleAsync(account.Id);
            saved.DisplayName.Should().Be("旧Codex账号");
            saved.IsDisplayNameCustom.Should().BeTrue();
            saved.AccessToken.Should().Be("new-at");
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            site.Name.Should().Be("旧Codex账号");
            site.ApiKey.Should().Be("new-at");

            // 重复启动不能把已有 false 标记的新版账号当作旧账号覆盖。
            var fresh = await provisioner.ProvisionFromTokensAsync(new CodexProvisionInput
            {
                Email = "fresh@example.com", PlanType = "plus", AccessToken = "fresh-at", RefreshToken = "fresh-rt",
            }, CancellationToken.None);
            fresh.IsDisplayNameCustom.Should().BeFalse();
            SqlSugarSetup.InitializeDatabase(db.Client);
            (await db.CodexAccounts.InSingleAsync(fresh.Id)).IsDisplayNameCustom.Should().BeFalse();
        }

        provider.Dispose();
    }

    /// <summary>
    /// DeviceOAuthAccounts 加列升级专项（IsDisplayNameCustom 非空 bool 列）：
    /// SQLite 对有数据表加 NOT NULL 列必须带默认值，否则 ALTER 失败导致应用启动崩溃；
    /// 无法识别历史用户命名，旧行保守标记为自定义，重登时保留名称。
    /// </summary>
    [Fact]
    public async Task Device_oauth_accounts_table_upgrades_with_display_name_custom_flag()
    {
        var dbFile = _databasePath;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE DeviceOAuthAccounts (
                    Id TEXT PRIMARY KEY,
                    VendorKey TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    Email TEXT,
                    ExternalUserId TEXT,
                    AccessToken TEXT,
                    RefreshToken TEXT,
                    TokenExpiresAt TEXT,
                    LastRefreshAt TEXT,
                    RequiresReauth INTEGER NOT NULL DEFAULT 0,
                    ExtraJson TEXT,
                    LinkedSiteId TEXT NOT NULL,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    DisabledByFeatureToggle INTEGER NOT NULL DEFAULT 0,
                    ManuallyDisabled INTEGER NOT NULL DEFAULT 0,
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    LastQuotaRawJson TEXT,
                    LastQuotaCheckedAt TEXT,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                INSERT INTO DeviceOAuthAccounts (Id, VendorKey, DisplayName, ExternalUserId, AccessToken, RefreshToken, LinkedSiteId, CreatedAt, UpdatedAt)
                    VALUES ('55555555-5555-5555-5555-555555555555', 'zcode', '旧设备账号', 'legacy-user', 'at', 'rt', '66666666-6666-6666-6666-666666666666', '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={dbFile}");
        var provider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(provider.GetRequiredService<SqlSugar.ISqlSugarClient>());

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var account = await db.DeviceOAuthAccounts.InSingleAsync(Guid.Parse("55555555-5555-5555-5555-555555555555"));
            account.Should().NotBeNull("旧设备 OAuth 账号必须原样保留");
            account!.IsDisplayNameCustom.Should().BeTrue("旧名称的来源无法识别，必须保守保护");
            account.DisplayName.Should().Be("旧设备账号");
            await db.InsertAsync(new Site { Id = account.LinkedSiteId, Name = account.DisplayName, BaseUrl = "https://example.com", ApiKey = "at" });

            var cacheServices = new ServiceCollection().AddMemoryCache().AddLogging().AddScoped<ProxyRequestMetadataCache>().BuildServiceProvider();
            using var cacheScope = cacheServices.CreateScope();
            var provisioner = new DeviceOAuthAccountProvisioner(db, cacheScope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
                new SiteCascadeDeleter(db), NullLogger<DeviceOAuthAccountProvisioner>.Instance);
            var vendor = new ZCodeOAuthVendor(new HttpClient());
            var tokens = new DeviceOAuthTokenSet { AccessToken = "new-at", RefreshToken = "new-rt" };
            var relogin = await provisioner.ProvisionAsync(vendor, "上游新名称", null, "legacy-user", tokens, null, CancellationToken.None);
            relogin.Id.Should().Be(account.Id);
            var saved = await db.DeviceOAuthAccounts.InSingleAsync(account.Id);
            saved.DisplayName.Should().Be("旧设备账号");
            saved.IsDisplayNameCustom.Should().BeTrue();
            saved.AccessToken.Should().Be("new-at");
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            site.Name.Should().Be("旧设备账号");
            site.ApiKey.Should().Be("new-at");

            // 重复启动不能把已有 false 标记的新版账号当作旧账号覆盖。
            var fresh = await provisioner.ProvisionAsync(vendor, "默认名", null, "new-user", tokens, null, CancellationToken.None);
            fresh.IsDisplayNameCustom.Should().BeFalse();
            SqlSugarSetup.InitializeDatabase(db.Client);
            (await db.DeviceOAuthAccounts.InSingleAsync(fresh.Id)).IsDisplayNameCustom.Should().BeFalse();
            await provisioner.ProvisionAsync(vendor, "更新的默认名", null, "new-user", tokens, null, CancellationToken.None);
            (await db.DeviceOAuthAccounts.InSingleAsync(fresh.Id)).DisplayName.Should().Be("更新的默认名");
            cacheServices.Dispose();
        }

        provider.Dispose();
    }

    /// <summary>
    /// 号池加列/建表升级专项（ProxyRouteEntries 加可空 ManagedByPoolId 列 + AccountPools/
    /// AccountPoolMembers 新表）：可空列默认 null（无托管），新表空建，旧数据原样保留。
    /// </summary>
    [Fact]
    public async Task Route_entries_upgrade_with_pool_tables_and_managed_column()
    {
        var dbFile = _databasePath;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ProxyRouteEntries (
                    Id TEXT PRIMARY KEY,
                    EntryName TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE ProxyRouteRules (
                    Id TEXT PRIMARY KEY,
                    ExternalModelName TEXT NOT NULL,
                    UpstreamModelName TEXT NOT NULL,
                    SiteId TEXT NOT NULL,
                    SiteModelName TEXT NOT NULL,
                    Priority INTEGER NOT NULL DEFAULT 0,
                    ModelPriority INTEGER NOT NULL DEFAULT 0,
                    InstancePriority INTEGER NOT NULL DEFAULT 0,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    AvailabilityMode TEXT NOT NULL DEFAULT 'AllDay',
                    TimeRangesJson TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO ProxyRouteEntries (Id, EntryName, CreatedAt)
                    VALUES ('77777777-7777-7777-7777-777777777777', '旧入口', '2026-01-01T00:00:00+00:00');
                INSERT INTO ProxyRouteRules (Id, ExternalModelName, UpstreamModelName, SiteId, SiteModelName)
                    VALUES ('88888888-8888-8888-8888-888888888888', '旧入口', 'm', '99999999-9999-9999-9999-999999999999', 'm');
                INSERT INTO ProxyRouteRules (Id, ExternalModelName, UpstreamModelName, SiteId, SiteModelName)
                    VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'rule-only', 'm', '99999999-9999-9999-9999-999999999999', 'm');
                """;
            command.ExecuteNonQuery();
        }

        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={dbFile}");
        var provider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(provider.GetRequiredService<SqlSugar.ISqlSugarClient>());

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entry = await db.ProxyRouteEntries.InSingleAsync(Guid.Parse("77777777-7777-7777-7777-777777777777"));
            entry.Should().NotBeNull("旧路由入口必须原样保留");
            entry!.ManagedByPoolId.Should().BeNull("升级后旧入口无托管标记（手工路由语义不变）");
            (await db.ProxyRouteRules.CountAsync()).Should().Be(2);
            var rule = await db.ProxyRouteRules.InSingleAsync(Guid.Parse("88888888-8888-8888-8888-888888888888"));
            rule.TargetKind.Should().Be("Site");
            rule.TargetRouteEntryId.Should().BeNull();
            rule.TargetRouteEntryName.Should().BeNull();
            var backfilled = await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == "rule-only");
            backfilled.Should().NotBeNull();
            var stableId = backfilled.Id;
            // Startup is repeatable and preserves existing/backfilled identities, including empty legacy discriminators.
            await db.Client.Ado.ExecuteCommandAsync("UPDATE ProxyRouteRules SET TargetKind = '' WHERE ExternalModelName = 'rule-only';");
            SqlSugarSetup.InitializeDatabase(db.Client);
            (await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == "rule-only")).Id.Should().Be(stableId);
            (await db.ProxyRouteRules.InSingleAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))).TargetKind.Should().Be("Site");
            (await db.ProxyRouteEntries.CountAsync()).Should().Be(2);
            (await db.ProxyRouteEntries.InSingleAsync(entry.Id)).Id.Should().Be(Guid.Parse("77777777-7777-7777-7777-777777777777"));
            (await db.AccountPools.CountAsync()).Should().Be(0, "新表空建可写可读");
            (await db.AccountPoolMembers.CountAsync()).Should().Be(0);
        }

        provider.Dispose();
    }

    [Fact]
    public async Task Nullable_prerelease_discriminator_is_normalized_without_rebinding_deleted_identity()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ProxyRouteRules (
                    Id TEXT PRIMARY KEY, ExternalModelName TEXT NOT NULL,
                    UpstreamModelName TEXT NOT NULL, SiteId TEXT NOT NULL, SiteModelName TEXT NOT NULL,
                    Priority INTEGER NOT NULL DEFAULT 0, ModelPriority INTEGER NOT NULL DEFAULT 0,
                    InstancePriority INTEGER NOT NULL DEFAULT 0, IsEnabled INTEGER NOT NULL DEFAULT 1,
                    AvailabilityMode TEXT NOT NULL DEFAULT 'AllDay', TimeRangesJson TEXT NOT NULL DEFAULT '',
                    TargetKind TEXT NULL, TargetRouteEntryId TEXT NULL, TargetRouteEntryName TEXT NULL
                );
                INSERT INTO ProxyRouteRules (Id, ExternalModelName, UpstreamModelName, SiteId, SiteModelName, TargetKind)
                    VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'legacy', 'm', '99999999-9999-9999-9999-999999999999', 'm', NULL);
                INSERT INTO ProxyRouteRules (Id, ExternalModelName, UpstreamModelName, SiteId, SiteModelName, TargetKind, TargetRouteEntryId, TargetRouteEntryName)
                    VALUES ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'parent', '', '00000000-0000-0000-0000-000000000000', '', 'RouteEntry', 'cccccccc-cccc-cccc-cccc-cccccccccccc', 'legacy');
                """;
            command.ExecuteNonQuery();
        }
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={_databasePath}");
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<SqlSugar.ISqlSugarClient>();
        SqlSugarSetup.InitializeDatabase(client);
        SqlSugarSetup.InitializeDatabase(client);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.ProxyRouteRules.InSingleAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))).TargetKind.Should().Be("Site");
        var reference = await db.ProxyRouteRules.InSingleAsync(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        reference.TargetKind.Should().Be("RouteEntry");
        reference.TargetRouteEntryId.Should().Be(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        (await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == "legacy")).Id.Should().NotBe(reference.TargetRouteEntryId!.Value);
        (await db.ProxyRouteEntries.CountAsync()).Should().Be(2);
    }

    public void Dispose()
    {
        TryDelete(_databasePath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + "-wal"); } catch { }
        try { File.Delete(path + "-shm"); } catch { }
    }
}
