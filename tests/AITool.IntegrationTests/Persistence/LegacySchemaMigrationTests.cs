using AITool.Infrastructure.Persistence;
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
    /// 旧行标记默认 false（由形态启发兜底识别默认名）。
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
            account!.IsDisplayNameCustom.Should().BeFalse("升级后旧行标记默认 false（形态启发兜底）");
            account.DisplayName.Should().Be("旧Codex账号");
        }

        provider.Dispose();
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
