using AITool.Infrastructure.Proxy;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Admin.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.Admin.IntegrationTests.Services;

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
            null!,
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
