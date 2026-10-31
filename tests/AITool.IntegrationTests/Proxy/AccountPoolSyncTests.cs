using AITool.Application.Codex;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 号池同步（生成式）回归：池成员展开成普通路由规则（ExternalModelName=池名），
/// 托管标记挂路由条目；自动纳入/排除位/名称碰撞保护（绝不覆盖手工路由）/
/// 删除解除托管保留规则/映射变更后再同步幂等。
/// </summary>
public sealed class AccountPoolSyncTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-account-pool-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public AccountPoolSyncTests()
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
    public async Task Manual_pool_generates_rules_per_model_per_member()
    {
        var (provisioner, sync, db) = CreateServices();
        var teamA = await provisioner.ProvisionFromTokensAsync(Input("acc-p1", "a@p.com", "team", "at-1"), CancellationToken.None);
        var teamB = await provisioner.ProvisionFromTokensAsync(Input("acc-p2", "b@p.com", "team", "at-2"), CancellationToken.None);
        // A、B 都有 m1；只有 A 有 m2（模型只路由到持有它的成员）。
        await provisioner.UpsertRemoteModelsAsync(teamA.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(teamB.LinkedSiteId, [("m1", "m1")], CancellationToken.None);

        var pool = new AccountPool { Name = "gpt-team-pool", VendorKey = "codex", PlanType = "team" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new[]
        {
            new AccountPoolMember { PoolId = pool.Id, AccountRefId = teamA.Id, LinkedSiteId = teamA.LinkedSiteId, Priority = 1 },
            new AccountPoolMember { PoolId = pool.Id, AccountRefId = teamB.Id, LinkedSiteId = teamB.LinkedSiteId, Priority = 0 },
        }).ExecuteCommandAsync();

        var result = await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        result.MemberCount.Should().Be(2);
        result.ModelCount.Should().Be(2);
        result.RuleCount.Should().Be(3, "m1×2 成员 + m2×1 成员");

        var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "gpt-team-pool").ToListAsync();
        rules.Should().HaveCount(3);
        var entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "gpt-team-pool");
        entry.ManagedByPoolId.Should().Be(pool.Id, "生成的入口挂池托管标记");

        var m1Rules = rules.Where(r => r.UpstreamModelName == "m1").ToList();
        m1Rules.Should().HaveCount(2);
        m1Rules.Single(r => r.SiteId == teamB.LinkedSiteId).InstancePriority.Should().Be(0, "池内静态顺序=InstancePriority 基线（B 优先）");
        m1Rules.Single(r => r.SiteId == teamA.LinkedSiteId).InstancePriority.Should().Be(1);
        rules.Should().ContainSingle(r => r.UpstreamModelName == "m2" && r.SiteId == teamA.LinkedSiteId, "m2 只路由到持有它的成员");
        rules.All(r => r.SiteModelName == r.UpstreamModelName).Should().BeTrue("对外名透出上游名");
    }

    [Fact]
    public async Task Auto_pool_materializes_new_members_and_respects_exclusions()
    {
        var (provisioner, sync, db) = CreateServices();
        var first = await provisioner.ProvisionFromTokensAsync(Input("acc-p1", "a@p.com", "team", "at-1"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(first.LinkedSiteId, [("m1", "m1")], CancellationToken.None);

        var pool = new AccountPool { Name = "auto-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        (await db.AccountPoolMembers.Where(m => m.PoolId == pool.Id).ToListAsync()).Should().HaveCount(1, "自动池物化匹配账号");

        // 新登录的同套餐账号：下次同步自动入池；异套餐不入。
        var second = await provisioner.ProvisionFromTokensAsync(Input("acc-p2", "b@p.com", "team", "at-2"), CancellationToken.None);
        var plusAcc = await provisioner.ProvisionFromTokensAsync(Input("acc-p3", "c@p.com", "plus", "at-3"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(second.LinkedSiteId, [("m1", "m1")], CancellationToken.None);
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        var rows = await db.AccountPoolMembers.Where(m => m.PoolId == pool.Id).ToListAsync();
        rows.Select(m => m.AccountRefId).Should().BeEquivalentTo([first.Id, second.Id], "plus 账号不进 team 池");

        // 排除位：排除 second 后同步，规则只剩 first；再同步也不会把排除的补回。
        var secondRow = rows.Single(m => m.AccountRefId == second.Id);
        secondRow.Excluded = true;
        await db.Client.Updateable(secondRow).UpdateColumns(m => m.Excluded).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "auto-pool").ToListAsync();
        rules.Should().OnlyContain(r => r.SiteId == first.LinkedSiteId, "排除成员不参与路由");
        (await db.AccountPoolMembers.Where(m => m.PoolId == pool.Id && m.AccountRefId == second.Id).ToListAsync())
            .Single().Excluded.Should().BeTrue("排除行保留（可恢复）");
    }

    [Fact]
    public async Task Manual_route_name_collision_is_rejected()
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("acc-c1", "c@p.com", "team", "at-1"), CancellationToken.None);

        // 手工路由已占用名称（入口无托管标记）。
        await db.Client.Insertable(new ProxyRouteEntry { EntryName = "taken-name" }).ExecuteCommandAsync();
        await db.Client.Insertable(new ProxyRouteRule
        {
            ExternalModelName = "taken-name",
            UpstreamModelName = "x",
            SiteId = acc.LinkedSiteId,
            SiteModelName = "x",
        }).ExecuteCommandAsync();

        var pool = new AccountPool { Name = "taken-name", VendorKey = "codex" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();

        var act = () => sync.SyncPoolAsync(pool.Id, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*手工路由*");

        var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "taken-name").ToListAsync();
        rules.Should().HaveCount(1, "手工规则绝不被池覆盖");
    }

    [Fact]
    public async Task Delete_pool_unmanages_entry_and_keeps_rules()
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("acc-d1", "d@p.com", "team", "at-1"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(acc.LinkedSiteId, [("m1", "m1")], CancellationToken.None);

        var pool = new AccountPool { Name = "del-pool", VendorKey = "codex", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "del-pool").ToListAsync()).Should().HaveCount(1);

        await sync.DeletePoolAsync(pool.Id, CancellationToken.None);

        (await db.AccountPools.ToListAsync()).Should().BeEmpty();
        (await db.AccountPoolMembers.ToListAsync()).Should().BeEmpty();
        var entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "del-pool");
        entry.ManagedByPoolId.Should().BeNull("删除池=解除托管");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "del-pool").ToListAsync())
            .Should().HaveCount(1, "生成规则保留，可继续手工维护");
    }

    [Fact]
    public async Task Sync_after_mapping_changes_regenerates_idempotently()
    {
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("acc-i1", "i@p.com", "team", "at-1"), CancellationToken.None);
        var b = await provisioner.ProvisionFromTokensAsync(Input("acc-i2", "j@p.com", "team", "at-2"), CancellationToken.None);

        var pool = new AccountPool { Name = "grow-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "grow-pool").ToListAsync()).Should().BeEmpty("尚无映射→无规则");

        // 批量导入（P1）后同步：两个成员都拿到 m1/m2，规则长出来且不重复。
        await provisioner.BatchUpsertRemoteModelsAsync(a.Id, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "grow-pool").ToListAsync();
        rules.Should().HaveCount(4, "2 模型 × 2 成员，重复同步不产生重复规则");
    }

    private (CodexAccountProvisioner Provisioner, AccountPoolSyncService Sync, AppDbContext Db) CreateServices()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = new ProxyRequestMetadataCache(
            _services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            new ServiceScopeFactoryAdapter(_services));
        return (
            new CodexAccountProvisioner(db, cache, new StubModelCatalog(), new SiteCascadeDeleter(db), NullLogger<CodexAccountProvisioner>.Instance),
            new AccountPoolSyncService(db, cache, NullLogger<AccountPoolSyncService>.Instance),
            db);
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
        public IReadOnlyList<string> GetModelsForPlan(string? planType) => [];
        public Task<CodexCatalogRefreshResult> TryRefreshFromRemoteAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexCatalogRefreshResult { Success = false, Error = "stub" });
    }
}
