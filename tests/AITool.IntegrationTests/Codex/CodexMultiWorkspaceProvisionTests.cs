using AITool.Application.Codex;
using AITool.Application.Networking;
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
    private readonly List<IServiceScope> _provisionerScopes = [];

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
    public async Task Provision_and_relogin_do_not_automatically_import_models()
    {
        var provisioner = CreateProvisioner();
        var first = await provisioner.ProvisionFromTokensAsync(Input(accountId: "manual-models", email: "manual@example.com", plan: "plus", token: "at-1"), CancellationToken.None);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.ModelLibraryItems.ToListAsync()).Should().BeEmpty();
        (await db.SiteModelMappings.ToListAsync()).Should().BeEmpty();

        await provisioner.UpsertRemoteModelsAsync(first.LinkedSiteId, [("chosen-model", "chosen-model")], CancellationToken.None);
        await provisioner.ProvisionFromTokensAsync(Input(accountId: "manual-models", email: "manual@example.com", plan: "pro", token: "at-2"), CancellationToken.None);
        (await db.ModelLibraryItems.ToListAsync()).Select(x => x.ModelName).Should().Equal("chosen-model");
        (await db.SiteModelMappings.ToListAsync()).Should().ContainSingle(x => x.RemoteModelName == "chosen-model");
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

        // 相同刷新凭证证明是同一授权，可安全补写 workspace ID。
        var claimed = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-old", email: "old@example.com", plan: "plus", token: "at-old-2"), CancellationToken.None);
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
            legacyRow.AccessToken.Should().Be("at-old-2");
            accounts.Single(a => a.Id == withId.Id).AccessToken.Should().Be("at-new");
        }
    }

    [Fact]
    public async Task Login_with_account_id_requires_credential_proof_to_claim_legacy_row()
    {
        var provisioner = CreateProvisioner();

        // 存量：老版本登录落库的无 accountId 行（refresh_token 即将被重新授权轮换掉）。
        var legacy = await provisioner.ProvisionFromTokensAsync(Input(accountId: null, email: "dev@example.com", plan: null, token: "at-old"), CancellationToken.None);

        // 相同邮箱但不同凭证无法确认 workspace，不能认领，也不能新建可能重复刷新凭证的行。
        var action = () => provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-plus", email: "dev@example.com", plan: "plus", token: "at-new"), CancellationToken.None);
        var error = await action.Should().ThrowAsync<CodexWorkspaceRequiredException>();
        error.Which.Message.Should().Contain("人工");

        await using var verifyScope = _services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await verifyDb.CodexAccounts.ToListAsync()).Should().ContainSingle();
        var unchanged = await verifyDb.CodexAccounts.InSingleAsync(legacy.Id);
        unchanged!.AccountId.Should().BeNull();
        unchanged.AccessToken.Should().Be("at-old");
        (await verifyDb.Sites.InSingleAsync(legacy.LinkedSiteId))!.ApiKey.Should().Be("at-old");

        var claimed = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-plus", email: "dev@example.com", plan: "plus", token: "at-old"), CancellationToken.None);
        claimed.Id.Should().Be(legacy.Id, "相同刷新凭证可证明归属");
        claimed.LinkedSiteId.Should().Be(legacy.LinkedSiteId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.CodexAccounts.ToListAsync()).Count.Should().Be(1);
            var row = await db.CodexAccounts.InSingleAsync(legacy.Id);
            row!.AccountId.Should().Be("acc-plus", "认领后补写身份");
            row.AccessToken.Should().Be("at-old");
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

    [Theory]
    [InlineData("Codex 账号 (公司主力)")]
    [InlineData("dev@example.com (公司主力)")]
    public async Task Unmarked_legacy_custom_suffix_is_preserved(string name)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = new Site { Name = name, BaseUrl = "https://example.com", ApiKey = "at-old", ManagedSource = "Codex" };
        await db.Client.Insertable(site).ExecuteCommandAsync();
        var account = new CodexAccount
        {
            DisplayName = name, Email = "dev@example.com", PlanType = "plus", AccountId = null,
            IsDisplayNameCustom = false, LinkedSiteId = site.Id, AccessToken = "at-old", RefreshToken = "rt-at-new",
        };
        await db.Client.Insertable(account).ExecuteCommandAsync();

        var result = await CreateProvisioner().ProvisionFromTokensAsync(
            Input("acc-l", "dev@example.com", "plus", "at-new"), CancellationToken.None);

        result.Id.Should().Be(account.Id);
        var saved = await db.CodexAccounts.InSingleAsync(account.Id);
        saved.DisplayName.Should().Be(name);
        saved.AccountId.Should().Be("acc-l");
        saved.AccessToken.Should().Be("at-new");
        (await db.Sites.InSingleAsync(site.Id)).Name.Should().Be(name);
    }

    [Theory]
    [InlineData("Codex 账号 (plus)")]
    [InlineData("OAuth 账号 (plus)")]
    public async Task Legacy_fallback_with_previous_plan_upgrades_when_email_arrives(string name)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = new Site { Name = name, BaseUrl = "https://example.com", ApiKey = "at-old", ManagedSource = "Codex" };
        await db.Client.Insertable(site).ExecuteCommandAsync();
        var account = new CodexAccount
        {
            DisplayName = name, Email = null, PlanType = "plus", AccountId = null,
            IsDisplayNameCustom = false, LinkedSiteId = site.Id, AccessToken = "at-old", RefreshToken = "rt-at-new",
        };
        await db.Client.Insertable(account).ExecuteCommandAsync();

        // 已确认账号的网络操作上下文是无邮箱旧行取得新邮箱时的安全原位更新入口。
        var operation = new NetworkOperationContext(new AccountIdentity("codex", account.Id), "test",
            new AccountNetworkSelection(NetworkMode.Direct));
        var result = await CreateProvisioner().ProvisionFromTokensAsync(
            Input("acc-l", "dev@example.com", "pro", "at-new"), CancellationToken.None, operation);

        result.Id.Should().Be(account.Id);
        var saved = await db.CodexAccounts.InSingleAsync(account.Id);
        saved.DisplayName.Should().Be("dev@example.com (pro)");
        saved.IsDisplayNameCustom.Should().BeFalse();
        (await db.Sites.InSingleAsync(site.Id)).Name.Should().Be("dev@example.com (pro)");
    }

    [Theory]
    [InlineData("dev@example.com (plus)", "plus", false, "dev@example.com (pro)")]
    [InlineData("dev@example.com", "plus", false, "dev@example.com (pro)")]
    [InlineData("OAuth 账号", "plus", false, "dev@example.com (pro)")]
    [InlineData("dev@example.com (plus)", null, false, "dev@example.com (plus)")]
    [InlineData("dev@example.com (plus)", "plus", true, "dev@example.com (plus)")]
    public async Task Legacy_name_whitelist_requires_old_plan_and_respects_custom_flag(
        string name, string? oldPlan, bool isCustom, string expected)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = new Site { Name = name, BaseUrl = "https://example.com", ApiKey = "old", ManagedSource = "Codex" };
        await db.Client.Insertable(site).ExecuteCommandAsync();
        var account = new CodexAccount
        {
            DisplayName = name, Email = "dev@example.com", AccountId = "acc-l", PlanType = oldPlan,
            IsDisplayNameCustom = isCustom, LinkedSiteId = site.Id, AccessToken = "old", RefreshToken = "old-rt",
        };
        await db.Client.Insertable(account).ExecuteCommandAsync();

        var result = await CreateProvisioner().ProvisionFromTokensAsync(
            Input("acc-l", "dev@example.com", "pro", "new"), CancellationToken.None);
        result.Id.Should().Be(account.Id);
        var saved = await db.CodexAccounts.InSingleAsync(account.Id);
        saved.DisplayName.Should().Be(expected);
        saved.IsDisplayNameCustom.Should().Be(isCustom);
        (await db.Sites.InSingleAsync(site.Id)).Name.Should().Be(expected);
    }

    [Fact]
    public async Task Ambiguous_legacy_rows_are_not_claimed()
    {
        var provisioner = CreateProvisioner();

        // 两条同邮箱无身份存量（历史重复）+ 一条别的邮箱无身份存量。
        await provisioner.ProvisionFromTokensAsync(Input(accountId: null, email: "dev@example.com", plan: null, token: "at-a"), CancellationToken.None);
        await SeedLegacyRowAsync("dev@example.com", "at-b");
        await SeedLegacyRowAsync("other@example.com", "at-c");

        // 无凭证可确认归属时拒绝，保留所有旧行并提示人工处理。
        var action = () => provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-x", email: "dev@example.com", plan: "team", token: "at-new"), CancellationToken.None);
        var error = await action.Should().ThrowAsync<CodexWorkspaceRequiredException>();
        error.Which.Message.Should().Contain("人工");

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.CodexAccounts.ToListAsync();
            rows.Count.Should().Be(3, "无法确认归属时不新建账号");
            rows.Count(a => a.AccountId == "acc-x").Should().Be(0);
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
    public async Task Missing_email_requires_a_unique_user_id_in_the_same_workspace_to_update()
    {
        static string Token(string userId)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["https://api.openai.com/auth"] = new Dictionary<string, string> { ["chatgpt_user_id"] = userId },
            });
            return "header." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        }

        static CodexProvisionInput WithoutEmail(string workspace, string accessToken, string? userId) => new()
        {
            AccountId = workspace,
            AccessToken = accessToken,
            RefreshToken = "refresh-" + accessToken,
            IdToken = userId == null ? "invalid-token" : Token(userId),
        };

        var provisioner = CreateProvisioner();
        var a = await provisioner.ProvisionFromTokensAsync(WithoutEmail("ws", "a1", "user-a"), CancellationToken.None);
        var b = await provisioner.ProvisionFromTokensAsync(WithoutEmail("ws", "b1", "user-b"), CancellationToken.None);
        var otherWorkspace = await provisioner.ProvisionFromTokensAsync(WithoutEmail("other", "other1", "user-a"), CancellationToken.None);
        var unknown1 = await provisioner.ProvisionFromTokensAsync(WithoutEmail("ws", "unknown1", null), CancellationToken.None);
        var unknown2 = await provisioner.ProvisionFromTokensAsync(WithoutEmail("ws", "unknown2", null), CancellationToken.None);
        var updated = await provisioner.ProvisionFromTokensAsync(WithoutEmail("ws", "a2", "user-a"), CancellationToken.None);

        a.Id.Should().NotBe(b.Id);
        otherWorkspace.Id.Should().NotBe(a.Id);
        unknown1.Id.Should().NotBe(unknown2.Id);
        updated.Id.Should().Be(a.Id);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accounts = await db.CodexAccounts.ToListAsync();
        accounts.Should().HaveCount(5);
        accounts.Single(x => x.Id == a.Id).AccessToken.Should().Be("a2");
        accounts.Single(x => x.Id == b.Id).AccessToken.Should().Be("b1");
        accounts.Single(x => x.Id == otherWorkspace.Id).AccessToken.Should().Be("other1");
        accounts.Single(x => x.Id == unknown1.Id).RefreshToken.Should().Be("refresh-unknown1");
        accounts.Single(x => x.Id == unknown2.Id).RefreshToken.Should().Be("refresh-unknown2");
        (await db.Sites.InSingleAsync(b.LinkedSiteId))!.ApiKey.Should().Be("b1");
    }

    [Theory]
    [InlineData("Dev@Example.COM")]
    [InlineData("  dev@example.com  ")]
    public async Task Email_case_variation_keeps_single_credential(string email)
    {
        var provisioner = CreateProvisioner();
        var first = await provisioner.ProvisionFromTokensAsync(Input("acc-case", "dev@example.com", "plus", "at-1"), CancellationToken.None);
        var second = await provisioner.ProvisionFromTokensAsync(Input("acc-case", email, "plus", "at-2"), CancellationToken.None);

        second.Id.Should().Be(first.Id);
        second.LinkedSiteId.Should().Be(first.LinkedSiteId);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accounts = await db.CodexAccounts.ToListAsync();
        accounts.Should().ContainSingle();
        accounts[0].AccessToken.Should().Be("at-2");
        accounts[0].Email.Should().Be(email, "归一化仅用于比较，不改变落库邮箱");
        (await db.Sites.Where(s => s.ManagedSource == "Codex").ToListAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("acc-legacy")]
    public async Task Email_case_variation_claims_legacy_row(string? accountId)
    {
        await SeedLegacyRowAsync("Legacy@Example.com", "at-legacy");
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var legacy = (await db.CodexAccounts.ToListAsync()).Single();
        var provisioner = CreateProvisioner();

        // 带 workspace 的认领须使用相同 refresh token 证明归属。
        var claimed = await provisioner.ProvisionFromTokensAsync(
            Input(accountId, "legacy@example.com", "plus", "at-legacy"), CancellationToken.None);

        claimed.Id.Should().Be(legacy.Id);
        (await db.CodexAccounts.ToListAsync()).Should().ContainSingle();
        (await db.Sites.Where(s => s.ManagedSource == "Codex").ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_workspace_rejection_is_case_insensitive()
    {
        var provisioner = CreateProvisioner();
        var first = await provisioner.ProvisionFromTokensAsync(Input("acc-mix", "mix@example.com", "team", "at-1"), CancellationToken.None);
        var action = () => provisioner.ProvisionFromTokensAsync(Input(null, "MIX@example.com", null, "at-2"), CancellationToken.None);

        await action.Should().ThrowAsync<CodexWorkspaceRequiredException>();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accounts = await db.CodexAccounts.ToListAsync();
        accounts.Should().ContainSingle();
        accounts[0].Id.Should().Be(first.Id);
        accounts[0].AccessToken.Should().Be("at-1");
        (await db.Sites.Where(s => s.ManagedSource == "Codex").ToListAsync()).Should().ContainSingle();
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
    public async Task Missing_workspace_is_rejected_when_same_email_has_identified_accounts()
    {
        var provisioner = CreateProvisioner();
        var first = await provisioner.ProvisionFromTokensAsync(
            Input("acc-mix", "mix@example.com", "team", "at-id"), CancellationToken.None);
        await provisioner.ProvisionFromTokensAsync(
            Input("acc-other", "mix@example.com", "plus", "at-other"), CancellationToken.None);
        // 即使历史上已有无 ID 行，也不能继续写入可能属于已识别 workspace 的凭证。
        await SeedLegacyRowAsync("mix@example.com", "at-legacy");

        var action = () => provisioner.ProvisionFromTokensAsync(
            Input(null, "mix@example.com", null, "at-new"), CancellationToken.None);
        await action.Should().ThrowAsync<CodexWorkspaceRequiredException>();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.CodexAccounts.ToListAsync();
        rows.Should().HaveCount(3);
        rows.Single(a => a.Id == first.Id).AccessToken.Should().Be("at-id");
        rows.Single(a => a.AccountId == "acc-other").AccessToken.Should().Be("at-other");
        rows.Single(a => a.AccountId == null).AccessToken.Should().Be("at-legacy");
        (await db.Sites.Where(s => s.ManagedSource == "Codex").ToListAsync()).Should().HaveCount(3);
    }

    [Fact]
    public async Task Concurrent_mixed_identity_provisions_do_not_create_duplicate_credentials()
    {
        var provisioner = CreateProvisioner();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CodexAccount?> Provision(string? id)
        {
            await ready.Task;
            try
            {
                return await provisioner.ProvisionFromTokensAsync(
                    Input(id, "race-mixed@example.com", "team", "same-token"), CancellationToken.None);
            }
            catch (CodexWorkspaceRequiredException) when (id == null)
            {
                return null;
            }
        }

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() => Provision(i % 2 == 0 ? "acc-race" : null))).ToArray();
        ready.SetResult();
        var results = await Task.WhenAll(tasks);
        results.Where(a => a != null).Select(a => a!.Id).Distinct().Should().HaveCount(1);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.CodexAccounts.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].AccountId.Should().Be("acc-race");
        rows[0].RefreshToken.Should().Be("rt-same-token");
        (await db.Sites.Where(s => s.ManagedSource == "Codex").ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Batch_apply_models_skips_unverified_same_plan_accounts()
    {
        // 同套餐不同 workspace 的权限不一定相同，不能同步其他账号。
        var provisioner = CreateProvisioner();
        var teamA = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-b1", email: "a@example.com", plan: "team", token: "at-1"), CancellationToken.None);
        var teamB = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-b2", email: "b@example.com", plan: "team", token: "at-2"), CancellationToken.None);
        var plus = await provisioner.ProvisionFromTokensAsync(Input(accountId: "acc-b3", email: "c@example.com", plan: "plus", token: "at-3"), CancellationToken.None);

        var results = await provisioner.BatchUpsertRemoteModelsAsync(
            teamA.Id,
            [("gpt-5", "gpt-5"), ("gpt-5-codex", "gpt-5-codex")],
            CancellationToken.None);

        results.Should().HaveCount(2, "原分组内的未验证账号应报告跳过");
        results.Single(r => r.AccountId == teamA.Id).Ok.Should().BeTrue();
        results.Single(r => r.AccountId == teamB.Id).Ok.Should().BeFalse();
        results.Single(r => r.AccountId == teamB.Id).Error.Should().Contain("权限").And.Contain("跳过");

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mappingsA = await db.SiteModelMappings.Where(m => m.SiteId == teamA.LinkedSiteId).ToListAsync();
            mappingsA.Select(m => m.RemoteModelName).Should().Contain(["gpt-5", "gpt-5-codex"], "勾选的模型映射到组内健康成员");
            var mappingsB = await db.SiteModelMappings.Where(m => m.SiteId == teamB.LinkedSiteId).ToListAsync();
            mappingsB.Select(m => m.RemoteModelName).Should().NotContain("gpt-5-codex", "同套餐不能证明模型权限一致");
            var mappingsPlus = await db.SiteModelMappings.Where(m => m.SiteId == plus.LinkedSiteId).ToListAsync();
            mappingsPlus.Select(m => m.RemoteModelName).Should().NotContain("gpt-5-codex", "plus 账号不被批量波及（模型清单按 plan 分组）");
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
        _provisionerScopes.Add(scope);
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
        try
        {
            for (var i = _provisionerScopes.Count - 1; i >= 0; i--)
                _provisionerScopes[i].Dispose();
            _provisionerScopes.Clear();
        }
        finally
        {
            try { _services.Dispose(); }
            finally { TryDelete(_databasePath); }
        }
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
