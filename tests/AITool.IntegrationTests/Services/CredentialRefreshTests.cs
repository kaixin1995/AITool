using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// 验证 OAuth 401 凭证刷新在并发请求下不会重复轮换 refresh_token。
/// </summary>
public sealed class CredentialRefreshTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-credential-refresh-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _serviceProvider;
    private readonly Guid _siteId = Guid.NewGuid();

    public CredentialRefreshTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        _serviceProvider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    /// <summary>
    /// 同一隐藏站点的并发 401 刷新只应产生一次真实 OAuth 刷新请求。
    /// </summary>
    [Fact]
    public async Task Codex_refresh_is_single_flight_per_linked_site()
    {
        var oauth = new BlockingCodexOAuthClient();
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Sites.Add(new Site
        {
            Id = _siteId,
            Name = "Codex refresh test",
            ApiKey = "old-access-token",
            BaseUrl = "https://example.com",
            IsEnabled = true
        });
        db.CodexAccounts.Add(new CodexAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "Codex refresh test",
            AccessToken = "old-access-token",
            RefreshToken = "old-refresh-token",
            LinkedSiteId = _siteId,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        var service = new CodexCredentialRefreshService(
            db,
            oauth,
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
            NullLogger<CodexCredentialRefreshService>.Instance);

        var first = service.RefreshAsync(_siteId, "old-access-token", CancellationToken.None);
        await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.RefreshAsync(_siteId, "old-access-token", CancellationToken.None);
        oauth.ReleaseRefresh();

        var results = await Task.WhenAll(first, second);

        oauth.RefreshCallCount.Should().Be(1);
        results.Should().AllBe("new-access-token");
        (await db.CodexAccounts.SingleAsync()).AccessToken.Should().Be("new-access-token");
        (await db.Sites.SingleAsync()).ApiKey.Should().Be("new-access-token");
    }

    /// <summary>
    /// 调用方在刷新进行中取消（客户端断开）不得中断写回：
    /// OpenAI 轮换 refresh_token 后，半途放弃会导致新旧 token 同时失效（refresh_token_reused）。
    /// </summary>
    [Fact]
    public async Task Codex_refresh_survives_caller_cancellation_after_refresh_started()
    {
        var oauth = new BlockingCodexOAuthClient();
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Sites.Add(new Site
        {
            Id = _siteId,
            Name = "Codex refresh cancel test",
            ApiKey = "old-access-token",
            BaseUrl = "https://example.com",
            IsEnabled = true
        });
        db.CodexAccounts.Add(new CodexAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "Codex refresh cancel test",
            AccessToken = "old-access-token",
            RefreshToken = "old-refresh-token",
            LinkedSiteId = _siteId,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        var service = new CodexCredentialRefreshService(
            db,
            oauth,
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
            NullLogger<CodexCredentialRefreshService>.Instance);

        using var callerCts = new CancellationTokenSource();
        var refreshTask = service.RefreshAsync(_siteId, "old-access-token", callerCts.Token);
        await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 刷新请求已发出（旧 refresh_token 已被消费）之后调用方才断开。
        callerCts.Cancel();
        oauth.ReleaseRefresh();

        var result = await refreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().Be("new-access-token");
        var account = await db.CodexAccounts.SingleAsync();
        account.AccessToken.Should().Be("new-access-token");
        account.RefreshToken.Should().Be("new-refresh-token");
        (await db.Sites.SingleAsync()).ApiKey.Should().Be("new-access-token");
    }

    [Fact]
    public async Task Codex_provision_waits_for_refresh_and_preserves_rotated_import()
    {
        var oauth = new BlockingCodexOAuthClient();
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.Sites.Add(new Site { Id = _siteId, Name = "test", ApiKey = "old-access-token", BaseUrl = "https://example.com", IsEnabled = true });
        db.CodexAccounts.Add(new CodexAccount { Id = id, DisplayName = "test", AccountId = "workspace", Email = "test@example.com", AccessToken = "old-access-token", RefreshToken = "old-refresh-token", LinkedSiteId = _siteId, IsEnabled = true });
        await db.SaveChangesAsync();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var refresh = new CodexCredentialRefreshService(db, oauth, cache, NullLogger<CodexCredentialRefreshService>.Instance);
        var provisioner = new CodexAccountProvisioner(db, cache, null!, new SiteCascadeDeleter(db), NullLogger<CodexAccountProvisioner>.Instance);
        var refreshTask = refresh.RefreshForAccountAsync(id, "old-access-token", CancellationToken.None);
        await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var provisionTask = provisioner.ProvisionFromTokensAsync(new CodexProvisionInput
        {
            AccountId = "workspace", Email = "test@example.com", AccessToken = "old-access-token", RefreshToken = "old-refresh-token"
        }, CancellationToken.None);
        await Task.Delay(150);
        provisionTask.IsCompleted.Should().BeFalse();
        oauth.ReleaseRefresh();
        await Task.WhenAll(refreshTask, provisionTask).WaitAsync(TimeSpan.FromSeconds(10));
        var account = await db.CodexAccounts.InSingleAsync(id);
        account.AccessToken.Should().Be("new-access-token");
        account.RefreshToken.Should().Be("new-refresh-token");
        (await db.Sites.InSingleAsync(_siteId)).ApiKey.Should().Be("new-access-token");
    }

    [Fact]
    public async Task Codex_refresh_keeps_account_credentials_when_site_update_fails()
    {
        // 写序合同（788394c 确立，234557a 曾以原子事务反转、写序测试抓回）：
        // 账号五列先独立落库，site ApiKey 同步失败只保留旧值（可自愈偏态），凭证绝不回滚丢失。
        var oauth = new BlockingCodexOAuthClient();
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.Sites.Add(new Site { Id = _siteId, Name = "test", ApiKey = "old-access-token", BaseUrl = "https://example.com", IsEnabled = true });
        db.CodexAccounts.Add(new CodexAccount { Id = id, DisplayName = "test", AccessToken = "old-access-token", RefreshToken = "old-refresh-token", LinkedSiteId = _siteId, IsEnabled = true });
        await db.SaveChangesAsync();
        await db.Client.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_site_credential BEFORE UPDATE OF ApiKey ON Sites BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        var service = new CodexCredentialRefreshService(db, oauth, scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(), NullLogger<CodexCredentialRefreshService>.Instance);
        var task = service.RefreshForAccountAsync(id, "old-access-token", CancellationToken.None);
        await oauth.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oauth.ReleaseRefresh();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        var account = await db.CodexAccounts.InSingleAsync(id);
        account.AccessToken.Should().Be("new-access-token", "账号凭证先于 site 写独立落库，site 失败不回滚");
        account.RefreshToken.Should().Be("new-refresh-token");
        (await db.Sites.InSingleAsync(_siteId)).ApiKey.Should().Be("old-access-token", "site 写被注入失败，保持旧值（可自愈偏态，下次刷新重写）");
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

    private sealed class BlockingCodexOAuthClient : ICodexOAuthClient
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refreshCallCount;
        public TaskCompletionSource<bool> RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefreshCallCount => Volatile.Read(ref _refreshCallCount);

        public (string State, string Verifier) CreateOAuthSession() => ("state", "verifier");

        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken = default) =>
            Task.FromResult("https://example.com/oauth");

        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshCallCount);
            RefreshStarted.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return new CodexTokenSet
            {
                AccessToken = "new-access-token",
                RefreshToken = "new-refresh-token",
                ExpiresIn = 3600
            };
        }

        public void ReleaseRefresh() => _release.TrySetResult(true);
    }
}
