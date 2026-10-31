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
/// Codex 多身份共存供给回归，身份键 = (chatgpt_account_id, email) 复合（对齐 CLIProxyAPI 的
/// 凭证文件名键 hash8(account_id)+email）。两个维度缺一不可：
/// <para>chatgpt_account_id 标识 workspace 而非用户——同一邮箱可同时属于个人 plus 空间与组织
/// team 空间（同邮箱不同 account_id，两条账号）；同一 team workspace 的多个成员（不同邮箱）
/// 共享同一个 account_id（同 account_id 不同邮箱，同样必须两条账号）。</para>
/// <para>任一维度单键折叠都会互相覆盖：按邮箱折叠吞掉多 workspace，按 account_id 折叠吞掉
/// 同 workspace 的其它成员。accountId 缺失时邮箱兜底只允许命中同样无 accountId 的存量。</para>
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
    public async Task Custom_name_of_default_shape_is_never_overwritten()
    {
        // 反审 P2：用户对默认名就地微调（如「email (公司主力)」）与默认名形态无法靠字符串区分——
        // 自定义标记置位后，供给的默认名升级逻辑绝不触碰该名。
        var provisioner = CreateProvisioner();
        var account = await provisioner.ProvisionFromTokensAsync(
            Input(accountId: "acc-c", email: "dev@example.com", plan: "team", token: "at-1"), CancellationToken.None);

        await provisioner.UpdateAsync(account.Id, "dev@example.com (公司主力)", CancellationToken.None);
        var relogin = await provisioner.ProvisionFromTokensAsync(
            Input(accountId: "acc-c", email: "dev@example.com", plan: "team", token: "at-2"), CancellationToken.None);

        relogin.Id.Should().Be(account.Id);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CodexAccounts.InSingleAsync(account.Id);
            row!.DisplayName.Should().Be("dev@example.com (公司主力)", "同形态自定义名不被默认名升级覆盖");
            (await db.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be("dev@example.com (公司主力)");
        }
    }

    [Fact]
    public async Task Same_account_id_different_emails_coexist_as_two_accounts()
    {
        // chatgpt_account_id 标识 workspace：同一 team 的多个成员（不同邮箱）共享同一 account_id，
        // 单按 account_id 匹配会令后导入的成员覆盖先导入的成员（线上实际发生的覆盖 bug）。
        var provisioner = CreateProvisioner();

        var memberA = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-ws1", email: "a@example.com", plan: "team", token: "at-a"), CancellationToken.None);
        var memberB = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-ws1", email: "b@example.com", plan: "team", token: "at-b"), CancellationToken.None);

        memberA.Id.Should().NotBe(memberB.Id, "同 workspace 不同成员是两条凭证");
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accounts = await db.CodexAccounts.ToListAsync();
            accounts.Count.Should().Be(2, "同 account_id 不同邮箱不得折叠覆盖");

            var rowA = accounts.Single(a => a.Email == "a@example.com");
            rowA.AccountId.Should().Be("acc-ws1");
            rowA.AccessToken.Should().Be("at-a", "后导入的成员 b 不得覆盖成员 a 的凭证");
            rowA.DisplayName.Should().Be("a@example.com (team)");

            var rowB = accounts.Single(a => a.Email == "b@example.com");
            rowB.AccountId.Should().Be("acc-ws1", "两个成员的 workspace 头一致");
            rowB.AccessToken.Should().Be("at-b");
            rowB.LinkedSiteId.Should().NotBe(rowA.LinkedSiteId, "各自持有独立隐藏 Site");
        }
    }

    [Fact]
    public async Task Team_members_and_personal_workspace_mixed_import_keeps_all_rows()
    {
        // 线上事故完整复现：四份凭证 = 同邮箱 plus+team 双空间 + 同 team 另两个不同邮箱成员，
        // 全部导入后必须四条俱在（此前按 account_id 单键折叠只剩两条）。
        var provisioner = CreateProvisioner();

        await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-plus", email: "dev@example.com", plan: "plus", token: "at-plus"), CancellationToken.None);
        await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-ws1", email: "dev@example.com", plan: "team", token: "at-dev-team"), CancellationToken.None);
        await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-ws1", email: "mate1@example.com", plan: "team", token: "at-mate1"), CancellationToken.None);
        await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-ws1", email: "mate2@example.com", plan: "team", token: "at-mate2"), CancellationToken.None);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accounts = await db.CodexAccounts.ToListAsync();
            accounts.Count.Should().Be(4, "四份凭证四条账号，任何两次导入之间都不得互相覆盖");
            accounts.Single(a => a.AccountId == "acc-plus").AccessToken.Should().Be("at-plus");
            accounts.Single(a => a.Email == "dev@example.com" && a.AccountId == "acc-ws1").AccessToken.Should().Be("at-dev-team", "同邮箱的 team 行不被同 workspace 其它成员覆盖");
            accounts.Single(a => a.Email == "mate1@example.com").AccessToken.Should().Be("at-mate1");
            accounts.Single(a => a.Email == "mate2@example.com").AccessToken.Should().Be("at-mate2");
            (await db.Sites.Where(s => s.ManagedSource == "Codex").ToListAsync()).Count.Should().Be(4);
        }
    }

    [Fact]
    public async Task Email_change_creates_separate_credential_like_cpa()
    {
        // 上游改邮箱后重登：same account_id + 不同 email 与「同 workspace 另一成员登录」不可区分，
        // 按 CPA 语义新建独立凭证、旧行原样保留（其 refresh_token 已被新登录轮换掉，由用户手动删除），
        // 绝不能原位改写旧行的邮箱（那正是同 workspace 成员互相覆盖的路径）。
        var provisioner = CreateProvisioner();
        var first = await provisioner.ProvisionFromTokensAsync(
            Input(accountId: "acc-e", email: "old@example.com", plan: "plus", token: "at-1"), CancellationToken.None);

        var second = await provisioner.ProvisionFromTokensAsync(
            Input(accountId: "acc-e", email: "new@example.com", plan: "plus", token: "at-2"), CancellationToken.None);

        second.Id.Should().NotBe(first.Id, "不同 email 即不同身份键，新建凭证");
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accounts = await db.CodexAccounts.ToListAsync();
            accounts.Count.Should().Be(2);
            var oldRow = accounts.Single(a => a.Id == first.Id);
            oldRow.Email.Should().Be("old@example.com", "旧行不被改写邮箱");
            oldRow.AccessToken.Should().Be("at-1");
            oldRow.DisplayName.Should().Be("old@example.com (plus)", "旓名保持默认名形态");
            var newRow = accounts.Single(a => a.Id == second.Id);
            newRow.Email.Should().Be("new@example.com");
            newRow.AccessToken.Should().Be("at-2");
            newRow.DisplayName.Should().Be("new@example.com (plus)");
        }
    }

    [Fact]
    public async Task Mixed_identity_sequence_leaves_transient_row_unclaimed()
    {
        // 键漂移时序：带 id 供给先落库 → 同 email 无 id 供给后到——无法判断属于哪个 workspace，
        // 新建暂态行是正确语义（不认领已带身份的行）；再次带 id 登录命中自身更新，
        // 暂态行留存（可手动删除），不会与带身份行合并。
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
