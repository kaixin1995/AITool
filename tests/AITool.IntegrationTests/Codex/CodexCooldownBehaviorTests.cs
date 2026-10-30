using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Codex;

/// <summary>
/// 冷却链路行为锁定（五轮加固的回归保护）：
/// ① 重置额度时上游不回传新 refresh_token → 存量凭证保留（清空将永久无法刷新）；
/// ② 冷却恢复写前重读实时启用状态——冷却期间被手动禁用的账号不被自动拉回路由。
/// </summary>
public sealed class CodexCooldownBehaviorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-codex-cooldown-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public CodexCooldownBehaviorTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());

        // 冷却恢复尊重 OAuth 功能总开关，测试库开启它。
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = true });
            db.SaveChangesAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Reset_keeps_existing_refresh_token_when_upstream_omits_it()
    {
        var (accountId, siteId) = await SeedCoolingAccountAsync(isEnabled: true, siteEnabled: false);
        var oauth = new StubOAuthClient(new CodexTokenSet
        {
            AccessToken = "new-at",
            RefreshToken = "", // 上游不回传新 refresh_token
            ExpiresIn = 3600,
        });
        var service = CreateResetService(oauth);

        await service.ResetAsync(accountId, CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(accountId);
            row!.AccessToken.Should().Be("new-at", "access_token 正常回写");
            row.RefreshToken.Should().Be("rt-keep", "上游未回传时存量 refresh_token 保留（清空=永久无法刷新）");
            row.IsQuotaCooling.Should().BeFalse("重置清除冷却");
            row.IsEnabled.Should().BeTrue("重置恢复启用");
            (await db.Sites.InSingleAsync(siteId))!.ApiKey.Should().Be("new-at");
        }
    }

    [Fact]
    public async Task Reset_does_not_reenable_manually_disabled_account()
    {
        // 重置额度只清冷却，不越权启用：手动禁用（或阈值自动禁用）的账号不因重置被拉回路由。
        var (accountId, siteId) = await SeedCoolingAccountAsync(isEnabled: false, siteEnabled: false);
        var oauth = new StubOAuthClient(new CodexTokenSet { AccessToken = "new-at", RefreshToken = "rt-new", ExpiresIn = 3600 });
        await CreateResetService(oauth).ResetAsync(accountId, CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(accountId);
            row!.IsQuotaCooling.Should().BeFalse("冷却清除");
            row.IsEnabled.Should().BeFalse("手动禁用不被重置覆盖");
            row.AccessToken.Should().Be("new-at", "token 刷新照常");
            (await db.Sites.InSingleAsync(siteId))!.IsEnabled.Should().BeFalse("禁用账号的站点不拉回路由");
        }
    }

    [Fact]
    public async Task Recovery_skips_site_restore_for_manually_disabled_account()
    {
        // 冷却到期，但用户在冷却期间手动禁用了账号（IsEnabled=false + site 已禁）。
        var (accountId, siteId) = await SeedCoolingAccountAsync(isEnabled: false, siteEnabled: false);

        var recovery = new CodexCooldownRecoveryService(
            _services, NullLogger<CodexCooldownRecoveryService>.Instance, new TestingHostEnvironment());
        await recovery.RecoverDueAsync(CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(accountId);
            row!.IsQuotaCooling.Should().BeFalse("冷却状态本身清除");
            row.IsEnabled.Should().BeFalse("手动禁用不被覆盖");
            (await db.Sites.InSingleAsync(siteId))!.IsEnabled.Should().BeFalse("被手动禁用的账号不被自动拉回路由");
        }
    }

    [Fact]
    public async Task Recovery_restores_site_for_enabled_account()
    {
        var (accountId, siteId) = await SeedCoolingAccountAsync(isEnabled: true, siteEnabled: false);

        var recovery = new CodexCooldownRecoveryService(
            _services, NullLogger<CodexCooldownRecoveryService>.Instance, new TestingHostEnvironment());
        await recovery.RecoverDueAsync(CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Sites.InSingleAsync(siteId))!.IsEnabled.Should().BeTrue("启用账号的站点恢复路由");
        }
    }

    private CodexQuotaCooldownService CreateResetService(ICodexOAuthClient oauth)
    {
        var scope = _services.CreateScope();
        return new CodexQuotaCooldownService(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            new ProxyRequestMetadataCache(_services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                new ServiceScopeFactoryAdapter(_services)),
            oauth,
            NullLogger<CodexQuotaCooldownService>.Instance);
    }

    private async Task<(Guid AccountId, Guid SiteId)> SeedCoolingAccountAsync(bool isEnabled, bool siteEnabled)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = new Site
        {
            Name = "cooling-site",
            BaseUrl = "https://chatgpt.com/backend-api/codex",
            ApiKey = "old-at",
            SupportsResponses = true,
            ProtocolType = "Responses",
            ManagedSource = "Codex",
            IsEnabled = siteEnabled,
        };
        await db.Client.Insertable(site).ExecuteCommandAsync();
        var account = new CodexAccount
        {
            DisplayName = "cooling-account",
            Email = "dev@example.com",
            AccessToken = "old-at",
            RefreshToken = "rt-keep",
            LinkedSiteId = site.Id,
            IsEnabled = isEnabled,
            IsQuotaCooling = true,
            QuotaCoolingUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await db.Client.Insertable(account).ExecuteCommandAsync();
        return (account.Id, site.Id);
    }

    public void Dispose()
    {
        _services.Dispose();
        try { File.Delete(_databasePath); } catch { }
        try { File.Delete(_databasePath + "-wal"); } catch { }
        try { File.Delete(_databasePath + "-shm"); } catch { }
    }

    private sealed class ServiceScopeFactoryAdapter(IServiceProvider services) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => services.CreateScope();
    }

    private sealed class TestingHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = default!;
    }

    private sealed class StubOAuthClient(CodexTokenSet tokens) : ICodexOAuthClient
    {
        public Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
            => Task.FromResult(tokens);
        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public (string State, string Verifier) CreateOAuthSession() => throw new NotSupportedException();
        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
