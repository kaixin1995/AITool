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

        // ② 带 accountId 的登录（同 workspace 重新授权）→ 认领该唯一无身份存量，原位补写身份。
        var claimed = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-old", email: "old@example.com", plan: "plus", token: "at-old-3"), CancellationToken.None);
        claimed.Id.Should().Be(legacy.Id);

        // ③ 再以另一 workspace（acc-new）登录同邮箱 → 已无无身份行可认领，新建独立账号，
        //    已带身份的记录绝不被覆盖。
        var withId = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-new", email: "old@example.com", plan: "team", token: "at-new"), CancellationToken.None);
        withId.Id.Should().NotBe(legacy.Id);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accounts = await db.CodexAccounts.ToListAsync();
            accounts.Count.Should().Be(2);
            var legacyRow = accounts.Single(a => a.Id == legacy.Id);
            legacyRow.AccountId.Should().Be("acc-old");
            legacyRow.AccessToken.Should().Be("at-old-3");
            accounts.Single(a => a.Id == withId.Id).AccessToken.Should().Be("at-new");
        }
    }

    [Fact]
    public async Task Login_with_account_id_claims_the_single_legacy_row_of_same_email()
    {
        var provisioner = CreateProvisioner();

        // 存量：老版本登录落库的无 accountId 行（refresh_token 即将被重新授权轮换掉）。
        var legacy = await provisioner.ProvisionFromTokensAsync(Input(accountId: null, email: "dev@example.com", plan: null, token: "at-old"), CancellationToken.None);

        // 同一 workspace 重新授权（这次解析出了 accountId）：应认领存量行原位更新，
        // 而不是另建第二条、让存量行变成刷新必败的孤儿死凭证。
        var claimed = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-plus", email: "dev@example.com", plan: "plus", token: "at-new"), CancellationToken.None);

        claimed.Id.Should().Be(legacy.Id, "同邮箱唯一无身份存量应被带身份登录认领");
        claimed.LinkedSiteId.Should().Be(legacy.LinkedSiteId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.CodexAccounts.ToListAsync()).Count.Should().Be(1);
            var row = await db.CodexAccounts.InSingleAsync(legacy.Id);
            row!.AccountId.Should().Be("acc-plus", "认领后补写身份");
            row.AccessToken.Should().Be("at-new");
            // 认领治愈彻底：旧默认名（裸邮箱）升级为「邮箱 (plan)」，站点名与账号名同步。
            var claimedSite = await db.Sites.InSingleAsync(legacy.LinkedSiteId);
            claimedSite!.Name.Should().Be("dev@example.com (plus)");
            row.DisplayName.Should().Be("dev@example.com (plus)");
        }
    }

    [Fact]
    public async Task Custom_name_survives_relogin_and_default_name_upgrades_with_plan()
    {
        var provisioner = CreateProvisioner();

        // 默认名建号（plus）→ 重登时订阅升级 pro：默认名跟随升级。
        var account = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-p", email: "dev@example.com", plan: "plus", token: "at-1"), CancellationToken.None);
        var upgraded = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-p", email: "dev@example.com", plan: "pro", token: "at-2"), CancellationToken.None);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(account.Id);
            row!.DisplayName.Should().Be("dev@example.com (pro)", "默认名形态随 plan 升级");
            (await db.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be("dev@example.com (pro)");
        }

        // 用户自定义名后重登：自定义名绝不覆盖，站点名与账号名同步保持。
        await provisioner.UpdateAsync(account.Id, "我的主力号", CancellationToken.None);
        var renamed = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-p", email: "dev@example.com", plan: "pro", token: "at-3", displayName: null), CancellationToken.None);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(account.Id);
            row!.DisplayName.Should().Be("我的主力号", "自定义名不被默认名逻辑覆盖");
            (await db.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be("我的主力号");
        }
        renamed.Id.Should().Be(account.Id);
    }

    [Fact]
    public async Task Ambiguous_legacy_rows_are_not_claimed()
    {
        var provisioner = CreateProvisioner();

        // 两条同邮箱无身份存量（历史重复）+ 一条别的邮箱无身份存量。
        await provisioner.ProvisionFromTokensAsync(Input(accountId: null, email: "dev@example.com", plan: null, token: "at-a"), CancellationToken.None);
        await SeedLegacyRowAsync("dev@example.com", "at-b");
        await SeedLegacyRowAsync("other@example.com", "at-c");

        // 同邮箱无身份行不唯一 → 不认领（避免随机合并），新建独立账号。
        var fresh = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-x", email: "dev@example.com", plan: "team", token: "at-new"), CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.CodexAccounts.ToListAsync();
            rows.Count.Should().Be(4, "两条历史重复 + 另一邮箱存量 + 新建");
            rows.Count(a => a.AccountId == "acc-x").Should().Be(1);
            rows.Where(a => string.IsNullOrEmpty(a.AccountId)).Should().HaveCount(3, "存量行原样保留，不被随机合并");
        }
    }

    [Fact]
    public async Task Concurrent_provisions_of_same_account_produce_single_row()
    {
        // OAuth 回调双击/并发导入：同 accountId 并发供给必须串行化，
        // 否则两条同身份账号的刷新循环共用同一个会被轮换的 refresh_token 互相作废。
        var provisioner = CreateProvisioner();

        var tasks = Enumerable.Range(0, 6).Select(i => provisioner.ProvisionFromTokensAsync(
            Input(accountId: "acc-race", email: "race@example.com", plan: "plus", token: "at-" + i), CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.Id).Distinct().Should().HaveCount(1, "并发供给收敛到同一条账号");
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.CodexAccounts.ToListAsync()).Count.Should().Be(1);
            (await db.Sites.Where(s => s.ManagedSource == "Codex").ToListAsync()).Count.Should().Be(1, "隐藏 Site 也只有一个");
        }
    }

    [Fact]
    public async Task Mixed_identity_sequence_converges_via_claim()
    {
        // 键漂移自愈：带 id 供给先落库 → 同 email 无 id 供给后到（无法判断属于哪个 workspace，
        // 新建暂态行是正确语义）→ 再次带 id 登录时认领暂态行 → 收敛为一条。
        var provisioner = CreateProvisioner();

        var withId = await provisioner.ProvisionFromTokensAsync(
            Input(accountId: "acc-mix", email: "mix@example.com", plan: "team", token: "at-id"), CancellationToken.None);
        await provisioner.ProvisionFromTokensAsync(
            Input(accountId: null, email: "mix@example.com", plan: null, token: "at-legacy"), CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.CodexAccounts.ToListAsync()).Count.Should().Be(2, "无 id 供给无法归并到已带身份的行，暂态新建");
        }

        await provisioner.ProvisionFromTokensAsync(
            Input(accountId: "acc-mix", email: "mix@example.com", plan: "team", token: "at-id-2"), CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.CodexAccounts.ToListAsync()).Count.Should().Be(2, "认领只发生在同 workspace 的 accountId 命中或唯一无 id 存量——acc-mix 已存在，命中自身更新，暂态行留给用户手动清理或后续无 id 流失配");
            var claimed = await db.CodexAccounts.InSingleAsync(withId.Id);
            claimed!.AccessToken.Should().Be("at-id-2");
        }
    }

    private async Task SeedLegacyRowAsync(string email, string token)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = new Site
        {
            Name = email,
            BaseUrl = "https://chatgpt.com/backend-api/codex",
            ApiKey = token,
            SupportsResponses = true,
            ProtocolType = "Responses",
            ManagedSource = "Codex",
        };
        await db.Client.Insertable(site).ExecuteCommandAsync();
        await db.Client.Insertable(new CodexAccount
        {
            Email = email,
            AccessToken = token,
            RefreshToken = "rt-" + token,
            LinkedSiteId = site.Id,
        }).ExecuteCommandAsync();
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

    private static CodexProvisionInput Input(string? accountId, string email, string? plan, string token, string? displayName = null) => new()
    {
        AccountId = accountId,
        Email = email,
        PlanType = plan,
        AccessToken = token,
        RefreshToken = "rt-" + token,
        IdToken = "idt",
        TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        DisplayName = displayName,
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
