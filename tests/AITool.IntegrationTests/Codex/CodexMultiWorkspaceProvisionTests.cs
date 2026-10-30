using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Codex;

/// <summary>
/// Codex 同邮箱多 workspace 供给回归：同一邮箱可同时属于个人 plus 空间与组织 team 空间，
/// 两边 chatgpt_account_id 不同，必须作为两条独立账号共存（对齐 CLIProxyAPI 的身份键方案），
/// 绝不能按邮箱折叠覆盖。accountId 缺失时邮箱兜底只允许命中同样无 accountId 的存量。
/// </summary>
public sealed class CodexMultiWorkspaceProvisionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-codex-multi-workspace-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public CodexMultiWorkspaceProvisionTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    [Fact]
    public async Task Same_email_different_account_ids_coexist_as_two_accounts()
    {
        var provisioner = CreateProvisioner();

        var plus = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-plus", email: "dev@example.com", plan: "plus", token: "at-plus"), CancellationToken.None);
        var team = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-team", email: "dev@example.com", plan: "team", token: "at-team"), CancellationToken.None);

        // 两条独立账号 + 两个独立隐藏 Site，互不覆盖。
        plus.Id.Should().NotBe(team.Id);
        team.LinkedSiteId.Should().NotBe(plus.LinkedSiteId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accounts = await db.CodexAccounts.ToListAsync();
            accounts.Count.Should().Be(2, "同邮箱两个 workspace 是两条账号，不得按邮箱折叠");

            var plusRow = accounts.Single(a => a.AccountId == "acc-plus");
            plusRow.Email.Should().Be("dev@example.com");
            plusRow.PlanType.Should().Be("plus");
            plusRow.AccessToken.Should().Be("at-plus", "后登录的 team 不得覆盖先登录的 plus");
            plusRow.DisplayName.Should().Be("dev@example.com (plus)", "默认名带 plan 后缀，多 workspace 一眼可分");

            var teamRow = accounts.Single(a => a.AccountId == "acc-team");
            teamRow.PlanType.Should().Be("team");
            teamRow.AccessToken.Should().Be("at-team");
            teamRow.DisplayName.Should().Be("dev@example.com (team)");

            // 隐藏 Site 各自带正确的 Chatgpt-Account-Id（额度/请求按 workspace 隔离的前提）。
            var plusSite = await db.Sites.InSingleAsync(plus.LinkedSiteId);
            plusSite!.ExtraHeadersJson.Should().Contain("acc-plus");
            var teamSite = await db.Sites.InSingleAsync(team.LinkedSiteId);
            teamSite!.ExtraHeadersJson.Should().Contain("acc-team");
        }
    }

    [Fact]
    public async Task Same_account_id_relogin_updates_in_place()
    {
        var provisioner = CreateProvisioner();

        var first = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-a", email: "dev@example.com", plan: "plus", token: "at-1"), CancellationToken.None);
        var second = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-a", email: "dev@example.com", plan: "pro", token: "at-2"), CancellationToken.None);

        second.Id.Should().Be(first.Id, "同 workspace 重登按 account_id 幂等更新");
        second.LinkedSiteId.Should().Be(first.LinkedSiteId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.CodexAccounts.ToListAsync()).Count.Should().Be(1);
            var row = await db.CodexAccounts.InSingleAsync(first.Id);
            row!.AccessToken.Should().Be("at-2");
            row.PlanType.Should().Be("pro", "订阅升级后 plan 跟随更新");
        }
    }

    [Fact]
    public async Task Email_fallback_only_matches_legacy_rows_without_account_id()
    {
        var provisioner = CreateProvisioner();

        // 存量：无 accountId 的旧账号（早期版本落库形态）。
        var legacy = await provisioner.ProvisionFromTokensAsync(Input(accountId: null, email: "old@example.com", plan: null, token: "at-old"), CancellationToken.None);

        // ① 无 accountId 的新登录 → 邮箱兜底命中该存量，原位更新（保留既有找回语义）。
        var refreshed = await provisioner.ProvisionFromTokensAsync(Input(accountId: null, email: "old@example.com", plan: null, token: "at-old-2"), CancellationToken.None);
        refreshed.Id.Should().Be(legacy.Id);

        // ② 带 accountId 的登录（同邮箱另一 workspace）→ 绝不覆盖已带身份的记录，新建独立账号。
        var withId = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-new", email: "old@example.com", plan: "team", token: "at-new"), CancellationToken.None);
        withId.Id.Should().NotBe(legacy.Id);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accounts = await db.CodexAccounts.ToListAsync();
            accounts.Count.Should().Be(2);
            accounts.Single(a => a.Id == legacy.Id).AccessToken.Should().Be("at-old-2");
            accounts.Single(a => a.Id == withId.Id).AccessToken.Should().Be("at-new");
        }
    }

    private CodexAccountProvisioner CreateProvisioner()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new CodexAccountProvisioner(
            db,
            new ProxyRequestMetadataCache(_services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                new ServiceScopeFactoryAdapter(_services)),
            new StubModelCatalog(),
            new SiteCascadeDeleter(db),
            NullLogger<CodexAccountProvisioner>.Instance);
    }

    private static CodexProvisionInput Input(string? accountId, string email, string? plan, string token) => new()
    {
        AccountId = accountId,
        Email = email,
        PlanType = plan,
        AccessToken = token,
        RefreshToken = "rt-" + token,
        IdToken = "idt",
        TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

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

    private sealed class StubModelCatalog : ICodexModelCatalog
    {
        public IReadOnlyList<string> GetModelsForPlan(string? planType) => ["gpt-5"];
        public Task<CodexCatalogRefreshResult> TryRefreshFromRemoteAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexCatalogRefreshResult { Success = false, Error = "stub" });
    }
}
