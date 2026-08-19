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

        var inspection = new AccountQuotaInspectionService(
            _serviceProvider,
            NullLogger<AccountQuotaInspectionService>.Instance,
            new TestingHostEnvironment(),
            _serviceProvider.GetRequiredService<SiteUsageTracker>());

        var result = await inspection.RunManualAsync(forceRefresh: true, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("keep");
        _serviceProvider.GetRequiredService<FakeQuotaProvider>().EnabledChanges.Should().BeEmpty();
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
        var inspection = new AccountQuotaInspectionService(
            _serviceProvider,
            NullLogger<AccountQuotaInspectionService>.Instance,
            new TestingHostEnvironment(),
            _serviceProvider.GetRequiredService<SiteUsageTracker>());

        var result = await inspection.RunManualAsync(forceRefresh: true, CancellationToken.None);

        result.Accounts.Should().ContainSingle();
        result.Accounts[0].Action.Should().Be("keep");
        provider.EnabledChanges.Should().BeEmpty();
    }

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

        public Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<AccountQuotaTarget>>([
                new AccountQuotaTarget
                {
                    ProviderKey = ProviderKey,
                    AccountId = _accountId,
                    DisplayName = "weekly-exhausted",
                    LinkedSiteId = Guid.NewGuid(),
                    IsEnabled = false,
                    ManuallyDisabled = false,
                    IsQuotaCooling = false,
                    DisabledByFeatureToggle = false,
                    DisabledByUpstream = DisabledByUpstream
                }
            ]);
        }

        public AccountQuotaSnapshot? ParseCachedQuota(string rawJson) => null;

        public Task<AccountQuotaSnapshot> QueryAsync(AccountQuotaTarget account, bool forceRefresh, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccountQuotaSnapshot
            {
                Success = true,
                Windows =
                [
                    new AccountQuotaWindow { Id = "five-hour", UsedPercent = 10, ResetAtUtc = DateTimeOffset.UtcNow.AddHours(1) },
                    new AccountQuotaWindow { Id = "weekly", UsedPercent = 99, ResetAtUtc = DateTimeOffset.UtcNow.AddDays(2) }
                ]
            });
        }

        public Task SetEnabledAsync(AccountQuotaTarget account, bool enabled, string reason, CancellationToken cancellationToken)
        {
            EnabledChanges.Add(enabled);
            return Task.CompletedTask;
        }

        public Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
