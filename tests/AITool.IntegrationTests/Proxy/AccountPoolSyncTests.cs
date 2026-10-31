using AITool.Application.Codex;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 号池同步（生成式）回归。核心语义：对外照常按模型名调用——每个成员模型生成一个
/// 托管路由入口（ExternalModelName=模型名，ManagedByPoolId=池 Id），池做账号扇出：
/// 请求 gpt-5 时候选=持有 gpt-5 的全部成员（InstancePriority=池内静态顺序，运行时
/// 叠加配额排序/冷却）。池名只是管理标签，不占用路由命名空间。碰撞模型（已被手工
/// 路由占用）跳过不阻塞整池；白名单只给勾选模型生成入口；成员映射变化后陈旧入口
/// 被清理；删池连带删除它生成的入口与规则（手工路由不受影响）。
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
    public async Task Pool_exposes_each_model_as_managed_entry_with_member_fanout()
    {
        var (provisioner, sync, db) = CreateServices();
        var teamA = await provisioner.ProvisionFromTokensAsync(Input("acc-p1", "a@p.com", "team", "at-1"), CancellationToken.None);
        var teamB = await provisioner.ProvisionFromTokensAsync(Input("acc-p2", "b@p.com", "team", "at-2"), CancellationToken.None);
        // A、B 都有 m1；只有 A 有 m2。
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
        result.SkippedModels.Should().BeEmpty();

        // 每个模型一个托管入口（不是池名入口）。
        var m1Entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
        var m2Entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m2");
        m1Entry.ManagedByPoolId.Should().Be(pool.Id);
        m2Entry.ManagedByPoolId.Should().Be(pool.Id);
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "gpt-team-pool")).Should()
            .BeFalse("池名只是管理标签，不占用路由命名空间");

        // 请求 m1：候选=两个成员（B 优先）；请求 m2：候选=仅 A。请求池名：无候选。
        var m1Rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        m1Rules.Should().HaveCount(2);
        m1Rules.Single(r => r.SiteId == teamB.LinkedSiteId).InstancePriority.Should().Be(0, "池内静态顺序=InstancePriority 基线");
        m1Rules.Single(r => r.SiteId == teamA.LinkedSiteId).InstancePriority.Should().Be(1);
        m1Rules.All(r => r.UpstreamModelName == "m1" && r.SiteModelName == "m1").Should().BeTrue("对外名=上游名（透出）");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync())
            .Should().ContainSingle(r => r.SiteId == teamA.LinkedSiteId, "m2 只扇出到持有它的成员");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "gpt-team-pool").ToListAsync())
            .Should().BeEmpty("绝不产生跨模型回退的池名别名");
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
        await provisioner.ProvisionFromTokensAsync(Input("acc-p3", "c@p.com", "plus", "at-3"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(second.LinkedSiteId, [("m1", "m1")], CancellationToken.None);
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        var rows = await db.AccountPoolMembers.Where(m => m.PoolId == pool.Id).ToListAsync();
        rows.Select(m => m.AccountRefId).Should().BeEquivalentTo([first.Id, second.Id], "plus 账号不进 team 池");

        // 排除位：排除 second 后同步，m1 只扇出到 first；排除行保留可恢复。
        var secondRow = rows.Single(m => m.AccountRefId == second.Id);
        secondRow.Excluded = true;
        await db.Client.Updateable(secondRow).UpdateColumns(m => m.Excluded).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync())
            .Should().OnlyContain(r => r.SiteId == first.LinkedSiteId, "排除成员不参与扇出");
        (await db.AccountPoolMembers.Where(m => m.PoolId == pool.Id && m.AccountRefId == second.Id).ToListAsync())
            .Single().Excluded.Should().BeTrue("排除行保留（可恢复）");
    }

    [Fact]
    public async Task Models_occupied_by_manual_routes_are_skipped_not_overwritten()
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("acc-c1", "c@p.com", "team", "at-1"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(acc.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);

        // 手工路由已占用 m1（入口无托管标记 + 一条规则）。
        await db.Client.Insertable(new ProxyRouteEntry { EntryName = "m1" }).ExecuteCommandAsync();
        await db.Client.Insertable(new ProxyRouteRule
        {
            ExternalModelName = "m1",
            UpstreamModelName = "m1",
            SiteId = acc.LinkedSiteId,
            SiteModelName = "m1",
        }).ExecuteCommandAsync();

        var pool = new AccountPool { Name = "mixed-pool", VendorKey = "codex", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        var result = await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        result.SkippedModels.Should().BeEquivalentTo(["m1"], "被手工路由占用的模型跳过");
        result.ModelCount.Should().Be(1, "m2 照常托管，跳过不阻塞整池");

        var manualEntry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
        manualEntry.ManagedByPoolId.Should().BeNull("手工入口不被接管");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync())
            .Should().HaveCount(1, "手工规则绝不被池覆盖");
        var m2Entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m2");
        m2Entry.ManagedByPoolId.Should().Be(pool.Id);
    }

    [Fact]
    public async Task Delete_pool_cascades_to_managed_entries_and_rules_only()
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("acc-d1", "d@p.com", "team", "at-1"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(acc.LinkedSiteId, [("m1", "m1")], CancellationToken.None);

        // 手工路由（非池生成）：删池绝不能碰。
        await db.Client.Insertable(new ProxyRouteEntry { EntryName = "manual" }).ExecuteCommandAsync();
        await db.Client.Insertable(new ProxyRouteRule
        {
            ExternalModelName = "manual",
            UpstreamModelName = "manual",
            SiteId = acc.LinkedSiteId,
            SiteModelName = "manual",
        }).ExecuteCommandAsync();

        var pool = new AccountPool { Name = "del-pool", VendorKey = "codex", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().HaveCount(1);

        await sync.DeletePoolAsync(pool.Id, CancellationToken.None);

        (await db.AccountPools.ToListAsync()).Should().BeEmpty();
        (await db.AccountPoolMembers.ToListAsync()).Should().BeEmpty();
        // 池生成的入口与规则连带删除（用户语义：删池不留自动生成的入口）。
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "m1")).Should()
            .BeFalse("池生成的托管入口随池删除");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync())
            .Should().BeEmpty("池生成的规则随池删除");
        // 手工路由原样保留。
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "manual")).Should().BeTrue("手工入口不受删池影响");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "manual").ToListAsync())
            .Should().HaveCount(1, "手工规则不受删池影响");
    }

    [Fact]
    public async Task Model_whitelist_limits_entries_and_resync_cleans_deselected()
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("acc-w1", "w@p.com", "team", "at-1"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(acc.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);

        // 白名单只勾 m1：m2 不生成入口。
        var pool = new AccountPool
        {
            Name = "whitelist-pool",
            VendorKey = "codex",
            AutoIncludeMembers = true,
            ExposedModelsJson = """["m1"]""",
        };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        var result = await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        result.ModelCount.Should().Be(1, "白名单只放行 m1");
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "m2")).Should().BeFalse("未勾选的模型不生成入口");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync()).Should().BeEmpty();

        // 白名单改为 m2：m1 入口与规则被清理，m2 新建（陈旧清理复用同一机制）。
        pool.ExposedModelsJson = """["m2"]""";
        await db.Client.Updateable(pool).UpdateColumns(p => p.ExposedModelsJson).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "m1")).Should().BeFalse("取消勾选的模型入口被清理");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().BeEmpty();
        (await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m2")).ManagedByPoolId.Should().Be(pool.Id);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync()).Should().HaveCount(1);

        // 白名单清空 = 不限：m1 恢复生成（向后兼容旧池语义）。
        pool.ExposedModelsJson = null;
        await db.Client.Updateable(pool).UpdateColumns(p => p.ExposedModelsJson).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "m1" && e.ManagedByPoolId == pool.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Sync_after_mapping_changes_regenerates_and_cleans_stale_entries()
    {
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("acc-i1", "i@p.com", "team", "at-1"), CancellationToken.None);
        var b = await provisioner.ProvisionFromTokensAsync(Input("acc-i2", "j@p.com", "team", "at-2"), CancellationToken.None);

        var pool = new AccountPool { Name = "grow-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName.StartsWith("m")).ToListAsync()).Should().BeEmpty("尚无映射→无规则");

        // 批量导入（P1）后同步：两个成员都拿到 m1/m2。
        await provisioner.BatchUpsertRemoteModelsAsync(a.Id, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().HaveCount(2, "重复同步不产生重复规则");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync()).Should().HaveCount(2);

        // 成员映射收缩（m2 被移除）：再同步后 m2 的托管入口与规则被清理，m1 完好。
        await db.Client.Deleteable<Domain.SiteCatalog.SiteModelMapping>()
            .Where(m => m.RemoteModelName == "m2").ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, CancellationToken.None);

        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync()).Should().BeEmpty("消失模型的规则被清理");
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "m2" && e.ManagedByPoolId == pool.Id)).Should()
            .BeFalse("消失模型的托管入口被清理");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Concurrent_syncs_serialize_and_converge_cleanly()
    {
        // 「导入后自动 sync-all」与「手动同步」交错的回归护栏：同步全局串行化后，
        // 并发同步必须全部成功收敛（无 UNIQUE 异常泄漏、规则不重复不丢失）。
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("acc-r1", "r@p.com", "team", "at-1"), CancellationToken.None);
        var b = await provisioner.ProvisionFromTokensAsync(Input("acc-r2", "s@p.com", "team", "at-2"), CancellationToken.None);
        await provisioner.BatchUpsertRemoteModelsAsync(a.Id, [("m1", "m1")], CancellationToken.None);

        var pool = new AccountPool { Name = "race-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => sync.SyncPoolAsync(pool.Id, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.RuleCount == 2, "串行化后并发同步全部成功收敛");
        var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        rules.Should().HaveCount(2, "2 成员 × m1，并发同步不重复不丢失");
        rules.Select(r => r.SiteId).Should().BeEquivalentTo([a.LinkedSiteId, b.LinkedSiteId]);
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
