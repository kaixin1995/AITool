using AITool.Application.Accounts;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// 验证通用额度巡检会综合所有额度窗口进行自动启停判断。
/// </summary>
public sealed class AccountQuotaInspectionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-account-inspection-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _serviceProvider;

    public AccountQuotaInspectionTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddSingleton<ProxyRequestMetadataCache>();
        services.AddSingleton<SiteUsageTracker>();
        services.AddSingleton<FakeQuotaProvider>();
        services.AddSingleton<IAccountQuotaProvider>(sp => sp.GetRequiredService<FakeQuotaProvider>());
        _serviceProvider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    /// <summary>
    /// 周窗口达到阈值时，即使五小时窗口较低，也不应错误恢复已禁用账号。
    /// </summary>
    [Fact]
    public async Task Inspection_uses_maximum_used_percent_across_windows()
    {
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                OAuthFeaturesEnabled = true,
                OAuthInspectionEnabled = true,
                OAuthAutoDisableThresholdPercent = 95,
                OAuthQuotaMaxCacheHours = 6,
                OAuthInspectionCacheEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var provider = _serviceProvider.GetRequiredService<FakeQuotaProvider>();
        provider.AccountIsEnabled = false;
        provider.FreshUsedPercent = 99;
        var inspection = CreateInspectionService();

        var result = await inspection.RunManualAsync(forceRefresh: true, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("keep");
        provider.EnabledChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspection_does_not_reenable_upstream_disabled_account()
    {
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                OAuthFeaturesEnabled = true,
                OAuthInspectionEnabled = true,
                OAuthAutoDisableThresholdPercent = 95,
                OAuthQuotaMaxCacheHours = 6,
                OAuthInspectionCacheEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var provider = _serviceProvider.GetRequiredService<FakeQuotaProvider>();
        provider.DisabledByUpstream = true;
        provider.AccountIsEnabled = false;
        provider.FreshUsedPercent = 50;
        var inspection = CreateInspectionService();

        var result = await inspection.RunManualAsync(forceRefresh: true, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("keep");
        provider.EnabledChanges.Should().BeEmpty();
    }

    /// <summary>
    /// 滞回带内（回启阈值 ≤ 已用% < 禁用阈值）不得自动回启，避免阈值附近反复启停。
    /// </summary>
    [Fact]
    public async Task Inspection_does_not_reenable_within_hysteresis_band()
    {
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                OAuthFeaturesEnabled = true,
                OAuthInspectionEnabled = true,
                OAuthAutoDisableThresholdPercent = 95,
                OAuthQuotaMaxCacheHours = 6,
                OAuthInspectionCacheEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var provider = _serviceProvider.GetRequiredService<FakeQuotaProvider>();
        provider.AccountIsEnabled = false;
        provider.FreshUsedPercent = 92; // 92 < 95（旧逻辑会回启），但 ≥ 回启阈值 87
        var inspection = CreateInspectionService();

        var result = await inspection.RunManualAsync(forceRefresh: false, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("keep");
        provider.EnabledChanges.Should().BeEmpty();
    }

    /// <summary>低于回启阈值（95-8=87）时应自动回启。</summary>
    [Fact]
    public async Task Inspection_reenables_below_hysteresis_threshold()
    {
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                OAuthFeaturesEnabled = true,
                OAuthInspectionEnabled = true,
                OAuthAutoDisableThresholdPercent = 95,
                OAuthQuotaMaxCacheHours = 6,
                OAuthInspectionCacheEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var provider = _serviceProvider.GetRequiredService<FakeQuotaProvider>();
        provider.AccountIsEnabled = false;
        provider.FreshUsedPercent = 50;
        var inspection = CreateInspectionService();

        var result = await inspection.RunManualAsync(forceRefresh: false, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("enable");
        provider.EnabledChanges.Should().BeEquivalentTo([true]);
    }

    /// <summary>
    /// 缓存读数达到禁用阈值时，必须先绕过缓存做一次新鲜读数复核；复核低于阈值则不禁用。
    /// </summary>
    [Fact]
    public async Task Inspection_rechecks_fresh_before_disabling_on_cached_snapshot()
    {
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                OAuthFeaturesEnabled = true,
                OAuthInspectionEnabled = true,
                OAuthAutoDisableThresholdPercent = 95,
                OAuthQuotaMaxCacheHours = 6,
                OAuthInspectionCacheEnabled = true
            });
            await db.SaveChangesAsync();
        }

        var provider = _serviceProvider.GetRequiredService<FakeQuotaProvider>();
        provider.AccountIsEnabled = true;
        provider.CachedUsedPercent = 99;
        provider.FreshUsedPercent = 50;
        var inspection = CreateInspectionService();

        var result = await inspection.RunManualAsync(forceRefresh: false, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("keep");
        result.Accounts[0].FromCache.Should().BeFalse("复核后应以新鲜读数为准");
        provider.EnabledChanges.Should().BeEmpty();
        provider.QueryCallCount.Should().Be(1, "缓存命中后复核只需一次真实查询");
    }

    /// <summary>缓存读数达阈值但复核读数失败时，本轮跳过自动禁用。</summary>
    [Fact]
    public async Task Inspection_skips_disable_when_fresh_recheck_fails()
    {
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                OAuthFeaturesEnabled = true,
                OAuthInspectionEnabled = true,
                OAuthAutoDisableThresholdPercent = 95,
                OAuthQuotaMaxCacheHours = 6,
                OAuthInspectionCacheEnabled = true
            });
            await db.SaveChangesAsync();
        }

        var provider = _serviceProvider.GetRequiredService<FakeQuotaProvider>();
        provider.AccountIsEnabled = true;
        provider.CachedUsedPercent = 99;
        provider.FreshQueryFails = true;
        var inspection = CreateInspectionService();

        var result = await inspection.RunManualAsync(forceRefresh: false, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("keep");
        provider.EnabledChanges.Should().BeEmpty();
    }

    private AccountQuotaInspectionService CreateInspectionService() => new(
        _serviceProvider,
        NullLogger<AccountQuotaInspectionService>.Instance,
        new TestingHostEnvironment(),
        _serviceProvider.GetRequiredService<SiteUsageTracker>());

    public void Dispose()
    {
        _serviceProvider.Dispose();
        TryDeleteDatabaseFile(_databasePath);
    }

    private static void TryDeleteDatabaseFile(string databasePath)
    {
        try { File.Delete(databasePath); } catch { }
        try { File.Delete(databasePath + "-wal"); } catch { }
        try { File.Delete(databasePath + "-shm"); } catch { }
    }

    private sealed class TestingHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AITool.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class FakeQuotaProvider : IAccountQuotaProvider
    {
        private readonly Guid _accountId = Guid.NewGuid();
        public string ProviderKey => "fake";
        public List<bool> EnabledChanges { get; } = [];
        public bool DisabledByUpstream { get; set; }

        /// <summary>账号是否处于启用状态（测试可调）。</summary>
        public bool AccountIsEnabled { get; set; } = true;

        /// <summary>真实查询返回的已用百分比；设置 CachedUsedPercent 后缓存路径才会生效。</summary>
        public double FreshUsedPercent { get; set; } = 99;

        /// <summary>缓存快照的已用百分比；为 null 时表示账号没有可复用缓存。</summary>
        public double? CachedUsedPercent { get; set; }

        /// <summary>真实查询是否返回失败。</summary>
        public bool FreshQueryFails { get; set; }

        public int QueryCallCount { get; private set; }

        public Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<AccountQuotaTarget>>([
                new AccountQuotaTarget
                {
                    ProviderKey = ProviderKey,
                    AccountId = _accountId,
                    DisplayName = "fake-account",
                    LinkedSiteId = Guid.NewGuid(),
                    IsEnabled = AccountIsEnabled,
                    ManuallyDisabled = false,
                    IsQuotaCooling = false,
                    DisabledByFeatureToggle = false,
                    DisabledByUpstream = DisabledByUpstream,
                    LastQuotaRawJson = CachedUsedPercent.HasValue ? "{\"fake\":true}" : null,
                    LastQuotaCheckedAt = CachedUsedPercent.HasValue ? DateTimeOffset.UtcNow.AddMinutes(-10) : null
                }
            ]);
        }

        public AccountQuotaSnapshot? ParseCachedQuota(string rawJson) =>
            CachedUsedPercent.HasValue
                ? new AccountQuotaSnapshot
                {
                    Success = true,
                    CheckedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                    Windows = [CreateWindow("cached", CachedUsedPercent.Value)]
                }
                : null;

        public Task<AccountQuotaSnapshot> QueryAsync(AccountQuotaTarget account, bool forceRefresh, CancellationToken cancellationToken)
        {
            QueryCallCount++;
            if (FreshQueryFails)
            {
                return Task.FromResult(new AccountQuotaSnapshot { Success = false, Error = "upstream unavailable" });
            }

            return Task.FromResult(new AccountQuotaSnapshot
            {
                Success = true,
                Windows = [CreateWindow("weekly", FreshUsedPercent)]
            });
        }

        private static AccountQuotaWindow CreateWindow(string id, double usedPercent) => new()
        {
            Id = id,
            UsedPercent = usedPercent,
            ResetAtUtc = DateTimeOffset.UtcNow.AddDays(2)
        };

        public Task SetEnabledAsync(AccountQuotaTarget account, bool enabled, string reason, CancellationToken cancellationToken)
        {
            EnabledChanges.Add(enabled);
            return Task.CompletedTask;
        }

        public Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
