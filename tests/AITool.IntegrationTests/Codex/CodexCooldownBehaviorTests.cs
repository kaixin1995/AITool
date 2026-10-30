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
    public async Task Reset_completes_cooldown_cleanup_when_http_canceled_midflight()
    {
        // 管理员点重置后中途关页（HTTP 阶段取消）：清冷却+站点恢复仍要完成（取消只作用于上游刷新）。
        var (accountId, siteId) = await SeedCoolingAccountAsync(isEnabled: true, siteEnabled: false);
        using var cts = new CancellationTokenSource();
        var oauth = new StubOAuthClient(onBeforeRefresh: cts.Cancel);

        await CreateResetService(oauth).ResetAsync(accountId, cts.Token);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(accountId);
            row!.IsQuotaCooling.Should().BeFalse("取消不得中断本地冷却清理");
            (await db.Sites.InSingleAsync(siteId))!.IsEnabled.Should().BeTrue("启用账号的站点照常恢复");
        }
    }

    [Fact]
    public async Task Reset_persists_tokens_before_site_write()
    {
        // site 行缺失（异常形态）：账号 token 五列仍必须先落库（写序=账号先/site 后，
        // 偏态永远落在可自愈的"账号新/site 旧"侧）。
        var accountId = await SeedCoolingAccountWithoutSiteAsync();
        var oauth = new StubOAuthClient(new CodexTokenSet { AccessToken = "new-at", RefreshToken = "rt-new", ExpiresIn = 3600 });

        await CreateResetService(oauth).ResetAsync(accountId, CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(accountId);
            row!.AccessToken.Should().Be("new-at", "site 缺失不阻断账号 token 落库");
            row.RefreshToken.Should().Be("rt-new");
        }
    }

    [Fact]
    public async Task Reset_account_write_survives_site_write_failure()
    {
        // 真正锁定写序：site 表 UPDATE 被触发器强制失败——账号 token 五列必须已先行落库
        //（若写序为 site 先/账号后，账号写会因同一异常中断，AccessToken 仍是旧值）。
        var (accountId, siteId) = await SeedCoolingAccountAsync(isEnabled: true, siteEnabled: true);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER block_site_apikey_update BEFORE UPDATE OF ApiKey ON Sites BEGIN SELECT RAISE(ABORT, 'injected site failure'); END;";
            command.ExecuteNonQuery();
        }

        var oauth = new StubOAuthClient(new CodexTokenSet { AccessToken = "new-at", RefreshToken = "rt-new", ExpiresIn = 3600 });
        await CreateResetService(oauth).ResetAsync(accountId, CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(accountId);
            row!.AccessToken.Should().Be("new-at", "账号五列在 site 写之前已落库（写序锁定的核心断言）");
            row.RefreshToken.Should().Be("rt-new");
            row.IsQuotaCooling.Should().BeFalse("site 写失败不阻断冷却清理");
            (await db.Sites.InSingleAsync(siteId))!.ApiKey.Should().Be("old-at", "site 写被注入失败，保持旧值（可自愈偏态）");
        }
    }

    private async Task<Guid> SeedCoolingAccountWithoutSiteAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = new CodexAccount
        {
            DisplayName = "no-site-account",
            Email = "dev@example.com",
            AccessToken = "old-at",
            RefreshToken = "rt-keep",
            LinkedSiteId = Guid.NewGuid(), // 不存在的 site
            IsEnabled = true,
            IsQuotaCooling = true,
            QuotaCoolingUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await db.Client.Insertable(account).ExecuteCommandAsync();
        return account.Id;
    }

    [Fact]
    public async Task Reset_ignores_whitespace_access_token_response()
    {
        // 上游 2xx 但 access_token 为纯空白：不得用空白值覆盖有效凭证（段 B 跳过），冷却清理照常。
        var (accountId, siteId) = await SeedCoolingAccountAsync(isEnabled: true, siteEnabled: false);
        var oauth = new StubOAuthClient(new CodexTokenSet { AccessToken = " ", RefreshToken = "rt-new", ExpiresIn = 3600 });

        await CreateResetService(oauth).ResetAsync(accountId, CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(accountId);
            row!.AccessToken.Should().Be("old-at", "空白 access_token 不覆盖有效凭证");
            row.RefreshToken.Should().Be("rt-keep", "段 B 整体跳过，refresh_token 也不动");
            row.IsQuotaCooling.Should().BeFalse("跳过落盘不阻断冷却清理");
            (await db.Sites.InSingleAsync(siteId))!.ApiKey.Should().Be("old-at");
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

    private sealed class StubOAuthClient(CodexTokenSet? tokens = null, Action? onBeforeRefresh = null) : ICodexOAuthClient
    {
        public Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        {
            // 模拟"刷新进行到一半时管理员关页"：先触发调用方取消，再抛 OCE。
            onBeforeRefresh?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(tokens ?? new CodexTokenSet());
        }
        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public (string State, string Verifier) CreateOAuthSession() => throw new NotSupportedException();
        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
