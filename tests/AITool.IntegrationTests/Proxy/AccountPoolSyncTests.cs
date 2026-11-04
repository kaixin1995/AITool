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
        services.AddScoped<AccountPoolSyncService>();
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

        // 新生成规则按账号名称排序；请求 m2 的候选只有 A。
        var m1Rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        m1Rules.Should().HaveCount(2);
        m1Rules.Single(r => r.SiteId == teamA.LinkedSiteId).InstancePriority.Should().Be(0, "新规则按账号名称排序");
        m1Rules.Single(r => r.SiteId == teamB.LinkedSiteId).InstancePriority.Should().Be(1);
        m1Rules.All(r => r.UpstreamModelName == "m1" && r.SiteModelName == "m1").Should().BeTrue("对外名=上游名（透出）");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync())
            .Should().ContainSingle(r => r.SiteId == teamA.LinkedSiteId, "m2 只扇出到持有它的成员");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "gpt-team-pool").ToListAsync())
            .Should().BeEmpty("绝不产生跨模型回退的池名别名");
    }

    [Fact]
    public async Task Auto_pool_without_plan_filter_includes_mixed_plans_and_fans_out_by_mapping()
    {
        var (provisioner, sync, db) = CreateServices();
        var team = await provisioner.ProvisionFromTokensAsync(Input("mixed-team", "team@p.com", "team", "at-team"), CancellationToken.None);
        var plus = await provisioner.ProvisionFromTokensAsync(Input("mixed-plus", "plus@p.com", "plus", "at-plus"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(team.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(plus.LinkedSiteId, [("m1", "m1")], CancellationToken.None);

        (await sync.ListVendorAccountsAsync("codex", "team", CancellationToken.None))
            .Select(a => a.AccountId).Should().BeEquivalentTo([team.Id]);
        (await sync.ListVendorAccountsAsync("codex", null, CancellationToken.None))
            .Select(a => a.AccountId).Should().BeEquivalentTo([team.Id, plus.Id]);

        var pool = new AccountPool { Name = "mixed-pool", VendorKey = "codex", PlanType = null, AutoIncludeMembers = true };
        var result = await sync.CreatePoolAsync(pool, null, CancellationToken.None);

        result.MemberCount.Should().Be(2);
        result.ModelCount.Should().Be(2);
        result.RuleCount.Should().Be(3);
        (await db.AccountPoolMembers.Where(m => m.PoolId == pool.Id).ToListAsync())
            .Select(m => m.AccountRefId).Should().BeEquivalentTo([team.Id, plus.Id]);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync())
            .Select(r => r.SiteId).Should().BeEquivalentTo([team.LinkedSiteId, plus.LinkedSiteId]);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync())
            .Should().ContainSingle(r => r.SiteId == team.LinkedSiteId);
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
        (await db.SiteModelMappings.Where(m => m.SiteId == a.LinkedSiteId || m.SiteId == b.LinkedSiteId).ToListAsync())
            .Should().BeEmpty("账号供给不应自动导入模型");
        (await db.ProxyRouteRules.ToListAsync()).Should().BeEmpty("尚无映射→无规则");

        // 逐账号导入模型后同步：两个成员都拿到 m1/m2。
        var importsA = await provisioner.BatchUpsertRemoteModelsAsync(a.Id, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);
        importsA.Single(r => r.AccountId == a.Id).Ok.Should().BeTrue();
        var importsB = await provisioner.BatchUpsertRemoteModelsAsync(b.Id, [("m1", "m1"), ("m2", "m2")], CancellationToken.None);
        importsB.Single(r => r.AccountId == b.Id).Ok.Should().BeTrue();
        var importedMappings = await db.SiteModelMappings.Where(m => m.SiteId == a.LinkedSiteId || m.SiteId == b.LinkedSiteId).ToListAsync();
        importedMappings.Select(m => (m.SiteId, m.RemoteModelName)).Should().BeEquivalentTo(
            new[] { (a.LinkedSiteId, "m1"), (a.LinkedSiteId, "m2"), (b.LinkedSiteId, "m1"), (b.LinkedSiteId, "m2") });
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
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1")], CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(b.LinkedSiteId, [("m1", "m1")], CancellationToken.None);

        var pool = new AccountPool { Name = "race-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();

        var firstSyncAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSync = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSyncWaiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var currentInsideCriticalSection = 0;
        var maxObservedConcurrency = 0;
        var totalAcquiredCount = 0;
        var syncCount = 0;

        AccountPoolSyncService.SyncGateAcquiredAsync = async _ =>
        {
            var count = Interlocked.Increment(ref currentInsideCriticalSection);
            Interlocked.Increment(ref totalAcquiredCount);

            // 记录最大并发数
            int initialMax;
            do
            {
                initialMax = Volatile.Read(ref maxObservedConcurrency);
                if (count <= initialMax) break;
            } while (Interlocked.CompareExchange(ref maxObservedConcurrency, count, initialMax) != initialMax);

            try
            {
                var index = Interlocked.Increment(ref syncCount);
                if (index == 1)
                {
                    firstSyncAcquired.TrySetResult(true);
                    // 阻塞首次同步以保持锁
                    await releaseFirstSync.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                Interlocked.Decrement(ref currentInsideCriticalSection);
            }
        };

        AccountPoolSyncService.SyncGateWaitStartingAsync = _ =>
        {
            if (firstSyncAcquired.Task.IsCompleted)
            {
                secondSyncWaiting.TrySetResult(true);
            }
            return Task.CompletedTask;
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = cts.Token;

            // 启动第一次同步
            var firstTask = sync.SyncPoolAsync(pool.Id, ct);

            // 等待首次同步成功获取 _syncGate
            await firstSyncAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // 在确认首次同步已持有锁的情况下启动第二次同步
            var secondTask = sync.SyncPoolAsync(pool.Id, ct);

            // 等待第二次同步进入 SyncGateWaitStartingAsync
            await secondSyncWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // 断言第二次尚未完成且当前同步区并发/计数符合预期
            secondTask.IsCompleted.Should().BeFalse("第二次同步在首次同步未释放锁时应当被阻塞等待");
            totalAcquiredCount.Should().Be(1, "第二次同步尚未获取锁，进入同步区的总次数仍应为 1");
            maxObservedConcurrency.Should().Be(1, "互斥锁保护下同时在同步区的调用数最多为 1");

            // 释放首次同步，让后续同步继续推进
            releaseFirstSync.TrySetResult(true);

            var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));

            totalAcquiredCount.Should().Be(2, "两次调用都成功进入同步区");
            maxObservedConcurrency.Should().Be(1, "最大并发始终为 1");

            results.Should().OnlyContain(r => r.RuleCount == 2, "串行化后并发同步全部成功收敛");
            var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
            rules.Should().HaveCount(2, "2 成员 × m1，并发同步不重复不丢失");
            rules.Select(r => r.SiteId).Should().BeEquivalentTo([a.LinkedSiteId, b.LinkedSiteId]);
        }
        finally
        {
            AccountPoolSyncService.SyncGateAcquiredAsync = null;
            AccountPoolSyncService.SyncGateWaitStartingAsync = null;
            firstSyncAcquired.TrySetCanceled();
            releaseFirstSync.TrySetCanceled();
            secondSyncWaiting.TrySetCanceled();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleted_pool_targets_leave_incoming_references_bound_to_old_id(bool deletePool)
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("ref-target", "synthetic@example.test", "team", "synthetic-token"), default);
        await provisioner.UpsertRemoteModelsAsync(acc.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], default);
        var pool = new AccountPool { Name = "reference-pool", VendorKey = "codex", AutoIncludeMembers = true };
        await sync.CreatePoolAsync(pool, null, default);
        var original = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
        var parent = new ProxyRouteEntry { EntryName = "parent" };
        var reference = new ProxyRouteRule
        {
            ExternalModelName = parent.EntryName,
            TargetKind = "RouteEntry",
            TargetRouteEntryId = original.Id,
            TargetRouteEntryName = original.EntryName,
            SiteId = Guid.Empty,
            SiteModelName = string.Empty,
            UpstreamModelName = string.Empty,
            IsEnabled = false,
        };
        await db.Client.Insertable(parent).ExecuteCommandAsync();
        await db.Client.Insertable(reference).ExecuteCommandAsync();

        if (deletePool) await sync.DeletePoolAsync(pool.Id, default);
        else await sync.UpdateModelsAsync(pool.Id, "[\"m2\"]", default);

        (await db.ProxyRouteEntries.AnyAsync(e => e.Id == original.Id)).Should().BeFalse();
        (await db.ProxyRouteRules.AnyAsync(r => r.ExternalModelName == "m1")).Should().BeFalse();
        (await db.ProxyRouteEntries.AnyAsync(e => e.Id == parent.Id)).Should().BeTrue();
        var retained = await db.ProxyRouteRules.FirstAsync(r => r.Id == reference.Id);
        retained.TargetRouteEntryId.Should().Be(original.Id);
        retained.TargetRouteEntryName.Should().Be("m1");

        if (deletePool)
            await sync.CreatePoolAsync(new AccountPool { Name = "reference-pool", VendorKey = "codex", AutoIncludeMembers = true }, null, default);
        else
            await sync.UpdateModelsAsync(pool.Id, null, default);

        var recreated = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
        recreated.Id.Should().NotBe(original.Id);
        (await db.ProxyRouteRules.FirstAsync(r => r.Id == reference.Id)).TargetRouteEntryId.Should().Be(original.Id,
            "recreating the display name must never rebind an incoming reference");
    }

    [Fact]
    public async Task Member_edit_and_sync_roll_back_together_when_graph_write_fails()
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("rollback", "synthetic@example.test", "team", "synthetic-token"), default);
        await provisioner.UpsertRemoteModelsAsync(acc.LinkedSiteId, [("m1", "m1")], default);
        var pool = new AccountPool { Name = "rollback-pool", VendorKey = "codex" };
        await sync.CreatePoolAsync(pool, [acc.Id], default);
        await db.Client.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_entry_delete BEFORE DELETE ON ProxyRouteEntries BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");

        var edit = () => sync.ReplaceMembersAsync(pool.Id, [], default);
        await edit.Should().ThrowAsync<Exception>();

        (await db.AccountPoolMembers.Where(m => m.PoolId == pool.Id).ToListAsync()).Should().ContainSingle(m => m.AccountRefId == acc.Id);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().ContainSingle();
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "m1")).Should().BeTrue();
    }

    [Fact]
    public async Task Whitelist_edit_and_sync_roll_back_together_when_graph_write_fails()
    {
        var (provisioner, sync, db) = CreateServices();
        var acc = await provisioner.ProvisionFromTokensAsync(Input("whitelist-rollback", "synthetic@example.test", "team", "synthetic-token"), default);
        await provisioner.UpsertRemoteModelsAsync(acc.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], default);
        var pool = new AccountPool { Name = "rollback-pool", VendorKey = "codex", AutoIncludeMembers = true };
        await sync.CreatePoolAsync(pool, null, default);
        await db.Client.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_entry_delete BEFORE DELETE ON ProxyRouteEntries BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");

        var edit = () => sync.UpdateModelsAsync(pool.Id, "[\"m2\"]", default);
        await edit.Should().ThrowAsync<Exception>();

        (await db.AccountPools.FirstAsync(p => p.Id == pool.Id)).ExposedModelsJson.Should().BeNull();
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().ContainSingle();
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "m1")).Should().BeTrue();
    }

    [Fact]
    public async Task Model_reselection_preserves_retained_rules_and_sorts_new_rules_by_name()
    {
        var (provisioner, sync, db) = CreateServices();
        var z = await provisioner.ProvisionFromTokensAsync(Input("sort-z", "z@example.test", "team", "token-z"), default);
        var a = await provisioner.ProvisionFromTokensAsync(Input("sort-a", "a@example.test", "team", "token-a"), default);
        await provisioner.UpsertRemoteModelsAsync(z.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], default);
        var pool = new AccountPool { Name = "preserve", VendorKey = "codex", ExposedModelsJson = "[\"m1\"]" };
        await sync.CreatePoolAsync(pool, [z.Id, a.Id], default);
        var before = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        var first = before.Single(r => r.SiteId == z.LinkedSiteId);
        var second = before.Single(r => r.SiteId == a.LinkedSiteId);
        first.Priority = 2;
        first.InstancePriority = 3;
        first.ModelPriority = 4;
        first.IsEnabled = false;
        first.AvailabilityMode = "DailyRanges";
        first.TimeRangesJson = "[{\"start\":\"09:00\",\"end\":\"17:00\"}]";
        second.Priority = 8;
        second.InstancePriority = 9;
        await db.Client.Updateable(new[] { first, second }).ExecuteCommandAsync();

        var result = await sync.UpdateModelsAsync(pool.Id, "[\"m1\",\"m2\"]", default);

        result.RuleCount.Should().Be(4);
        var after = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        after.Should().BeEquivalentTo(new[] { first, second });
        var generated = (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync())
            .OrderBy(r => r.InstancePriority).ToList();
        generated.Select(r => r.SiteId).Should().Equal(a.LinkedSiteId, z.LinkedSiteId);
        generated.Select(r => r.Priority).Should().Equal(0, 1);

        await sync.SyncPoolAsync(pool.Id, default);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().BeEquivalentTo(after);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m2").ToListAsync()).Should().BeEquivalentTo(generated);
    }

    [Fact]
    public async Task Sync_preserves_manual_other_model_and_reference_rules_but_removes_excluded_pool_members()
    {
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("manual-a", "a@example.test", "team", "token-a"), default);
        var b = await provisioner.ProvisionFromTokensAsync(Input("manual-b", "b@example.test", "team", "token-b"), default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], default);
        await provisioner.UpsertRemoteModelsAsync(b.LinkedSiteId, [("m1", "m1")], default);
        var pool = new AccountPool { Name = "manual-rules", VendorKey = "codex", ExposedModelsJson = "[\"m1\"]" };
        await sync.CreatePoolAsync(pool, [a.Id, b.Id], default);
        var externalRule = new ProxyRouteRule
        {
            ExternalModelName = "m1", UpstreamModelName = "custom", SiteModelName = "remote-custom",
            SiteId = Guid.NewGuid(), Priority = 7, ModelPriority = 3, InstancePriority = 9,
            IsEnabled = false, AvailabilityMode = "DailyRanges", TimeRangesJson = "[]"
        };
        var sameSiteOtherModel = new ProxyRouteRule
        {
            ExternalModelName = "m1", UpstreamModelName = "custom", SiteModelName = "remote-custom",
            SiteId = a.LinkedSiteId, Priority = 8, ModelPriority = 3, InstancePriority = 10
        };
        var reference = new ProxyRouteRule
        {
            ExternalModelName = "m1", UpstreamModelName = "reference", TargetKind = "RouteEntry",
            TargetRouteEntryId = Guid.NewGuid(), TargetRouteEntryName = "reference", Priority = 9
        };
        var externalSameModel = new ProxyRouteRule
        {
            ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1",
            SiteId = Guid.NewGuid(), Priority = 12, InstancePriority = 12
        };
        await db.Client.Insertable(new[] { externalRule, sameSiteOtherModel, reference, externalSameModel }).ExecuteCommandAsync();

        await sync.UpdateModelsAsync(pool.Id, "[\"m1\",\"m2\"]", default);
        await sync.ReplaceMembersAsync(pool.Id, [(a.Id, false), (b.Id, true)], default);

        var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        rules.Where(r => r.Id == externalRule.Id || r.Id == sameSiteOtherModel.Id || r.Id == reference.Id || r.Id == externalSameModel.Id)
            .Should().BeEquivalentTo(new[] { externalRule, sameSiteOtherModel, reference, externalSameModel });
        rules.Should().ContainSingle(r => r.SiteId == a.LinkedSiteId && r.UpstreamModelName == "m1");
        rules.Should().NotContain(r => r.SiteId == b.LinkedSiteId && r.UpstreamModelName == "m1");
    }

    [Fact]
    public async Task Ownership_survives_target_edits_and_preserves_manual_rules_on_exclusion_and_removal()
    {
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("owner-a", "owner-a@example.test", "team", "token-a"), default);
        var b = await provisioner.ProvisionFromTokensAsync(Input("owner-b", "owner-b@example.test", "team", "token-b"), default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1")], default);
        await provisioner.UpsertRemoteModelsAsync(b.LinkedSiteId, [("m1", "m1")], default);
        var pool = new AccountPool { Name = "ownership", VendorKey = "codex" };
        await sync.CreatePoolAsync(pool, [a.Id, b.Id], default);
        var generated = await db.ProxyRouteRules.FirstAsync(r => r.ExternalModelName == "m1" && r.SiteId == b.LinkedSiteId);
        generated.ManagedByPoolId.Should().Be(pool.Id);
        generated.ManagedPoolMemberSiteId.Should().Be(b.LinkedSiteId);
        generated.UpstreamModelName = "edited";
        generated.SiteModelName = "edited";
        generated.SiteId = a.LinkedSiteId;
        await db.Client.Updateable(generated).ExecuteCommandAsync();
        var manual = new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = b.LinkedSiteId };
        await db.Client.Insertable(manual).ExecuteCommandAsync();

        await sync.SyncPoolAsync(pool.Id, default);
        (await db.ProxyRouteRules.Where(r => r.Id == generated.Id).ToListAsync()).Should().ContainSingle();
        await sync.ReplaceMembersAsync(pool.Id, [(a.Id, false), (b.Id, true)], default);
        (await db.ProxyRouteRules.Where(r => r.Id == generated.Id).ToListAsync()).Should().BeEmpty();
        (await db.ProxyRouteRules.Where(r => r.Id == manual.Id).ToListAsync()).Should().ContainSingle();

        await sync.ReplaceMembersAsync(pool.Id, [], default);
        (await db.ProxyRouteRules.Where(r => r.Id == manual.Id).ToListAsync()).Should().ContainSingle();
        var entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
        entry.ManagedByPoolId.Should().BeNull();
        await sync.DeletePoolAsync(pool.Id, default);
        (await db.ProxyRouteRules.Where(r => r.Id == manual.Id).ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task New_members_append_in_name_order_without_changing_existing_configuration()
    {
        var (provisioner, sync, db) = CreateServices();
        var existing = await provisioner.ProvisionFromTokensAsync(Input("existing", "m@example.test", "team", "token-m"), default);
        await provisioner.UpsertRemoteModelsAsync(existing.LinkedSiteId, [("m1", "m1")], default);
        var pool = new AccountPool { Name = "append", VendorKey = "codex", AutoIncludeMembers = true };
        await sync.CreatePoolAsync(pool, null, default);
        var original = await db.ProxyRouteRules.FirstAsync(r => r.ExternalModelName == "m1");
        original.Priority = 11;
        original.InstancePriority = 15;
        original.IsEnabled = false;
        await db.Client.Updateable(original).ExecuteCommandAsync();
        var z = await provisioner.ProvisionFromTokensAsync(Input("new-z", "z@example.test", "team", "token-z"), default);
        var a = await provisioner.ProvisionFromTokensAsync(Input("new-a", "a@example.test", "team", "token-a"), default);
        await provisioner.UpsertRemoteModelsAsync(z.LinkedSiteId, [("m1", "m1")], default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1")], default);

        await sync.SyncPoolAsync(pool.Id, default);

        var rules = (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync())
            .OrderBy(r => r.InstancePriority).ToList();
        rules.Select(r => r.SiteId).Should().Equal(existing.LinkedSiteId, a.LinkedSiteId, z.LinkedSiteId);
        rules.Select(r => r.InstancePriority).Should().Equal(15, 16, 17);
        rules.Single(r => r.Id == original.Id).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Reconciliation_detects_account_and_mapping_changes_without_browser_calls()
    {
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("reconcile-a", "ra@p.com", "team", "at-ra"), default);
        var pool = new AccountPool { Name = "reconcile", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        var worker = new AccountPoolReconciliationService(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AccountPoolReconciliationService>.Instance);
        await worker.RunOnceAsync(default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1")], default);
        await worker.RunOnceAsync(default);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().ContainSingle();

        var b = await provisioner.ProvisionFromTokensAsync(Input("reconcile-b", "rb@p.com", "team", "at-rb"), default);
        await provisioner.UpsertRemoteModelsAsync(b.LinkedSiteId, [("m1", "m1")], default);
        await worker.RunOnceAsync(default);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().HaveCount(2);

        await db.Client.Deleteable<Domain.SiteCatalog.SiteModelMapping>().Where(m => m.SiteId == b.LinkedSiteId).ExecuteCommandAsync();
        await worker.RunOnceAsync(default);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().ContainSingle(r => r.SiteId == a.LinkedSiteId);
        await db.Client.Deleteable<Domain.Codex.CodexAccount>().Where(x => x.Id == a.Id).ExecuteCommandAsync();
        await worker.RunOnceAsync(default);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().BeEmpty();
        worker.GetStatus().PoolFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconciliation_skips_full_pass_when_fingerprint_unchanged()
    {
        // 轻量快速路径：行数指纹无变化的轮次只做 COUNT 聚合查询，不加载全表/不逐池签名/不重跑同步。
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("fp-a", "fa@p.com", "team", "at-fa"), default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1")], default);
        var pool = new AccountPool { Name = "fp-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        var worker = new AccountPoolReconciliationService(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AccountPoolReconciliationService>.Instance);
        await worker.RunOnceAsync(default);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync()).Should().ContainSingle();

        var skipSqls = new List<string>();
        db.Client.Aop.OnLogExecuting = (sql, _) => skipSqls.Add(sql);
        await worker.RunOnceAsync(default, forceFull: false);
        db.Client.Aop.OnLogExecuting = null;
        skipSqls.Should().NotBeEmpty("快速路径仍执行指纹 COUNT 查询");
        skipSqls.All(sql => sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("指纹不变的轮次只允许 COUNT 聚合查询，不得全量加载");

        // 指纹变化（新增一行账号）→ 非强制轮也做全量对账。
        var b = await provisioner.ProvisionFromTokensAsync(Input("fp-b", "fb@p.com", "team", "at-fb"), default);
        await provisioner.UpsertRemoteModelsAsync(b.LinkedSiteId, [("m1", "m1")], default);
        await worker.RunOnceAsync(default, forceFull: false);
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync())
            .Should().HaveCount(2, "指纹变化的非强制轮做全量对账并同步新成员");
    }

    [Fact]
    public async Task Reconciliation_reports_failures_and_retries_without_another_change()
    {
        var (_, _, db) = CreateServices();
        var pool = new AccountPool { Name = "retry", VendorKey = "codex", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();

        var worker = new AccountPoolReconciliationService(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AccountPoolReconciliationService>.Instance);
        // SQLite trigger injects a transient transactional failure.
        await db.Client.Ado.ExecuteCommandAsync("CREATE TRIGGER fail_pool_sync BEFORE UPDATE ON AccountPools BEGIN SELECT RAISE(FAIL, 'injected failure'); END;");
        await worker.RunOnceAsync(default);
        worker.GetStatus().PoolFailures.Should().ContainKey(pool.Id);
        await db.Client.Ado.ExecuteCommandAsync("DROP TRIGGER fail_pool_sync;");
        await worker.RunOnceAsync(default);
        worker.GetStatus().PoolFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_all_isolates_pools_and_manual_routes_and_invalidates_deleted_sources()
    {
        var (provisioner, _, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("scope-a", "a@scope.test", "team", "a"), default);
        var b = await provisioner.ProvisionFromTokensAsync(Input("scope-b", "b@scope.test", "plus", "b"), default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("pool-a", "pool-a")], default);
        await provisioner.UpsertRemoteModelsAsync(b.LinkedSiteId, [("pool-b", "pool-b")], default);
        var poolA = new AccountPool { Name = "scope-a", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        var poolB = new AccountPool { Name = "scope-b", VendorKey = "codex", PlanType = "plus", AutoIncludeMembers = true };
        await db.Client.Insertable(new[] { poolA, poolB }).ExecuteCommandAsync();
        var manualEntry = new ProxyRouteEntry { EntryName = "manual" };
        var manualRule = new ProxyRouteRule { ExternalModelName = "manual", SiteId = a.LinkedSiteId, SiteModelName = "manual", UpstreamModelName = "manual" };
        await db.Client.Insertable(manualEntry).ExecuteCommandAsync();
        await db.Client.Insertable(manualRule).ExecuteCommandAsync();
        var cache = new ProxyRequestMetadataCache(_services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), new ServiceScopeFactoryAdapter(_services));
        var sync = new AccountPoolSyncService(db, cache, NullLogger<AccountPoolSyncService>.Instance);
        await sync.SyncAllPoolsAsync(default);
        var poolBEntry = (await db.ProxyRouteEntries.Where(e => e.ManagedByPoolId == poolB.Id).ToListAsync()).Single();
        var poolBRule = (await db.ProxyRouteRules.Where(r => r.ManagedByPoolId == poolB.Id).ToListAsync()).Single();
        foreach (var name in new[] { "pool-a", "pool-b", "manual" })
        {
            var routes = await cache.CaptureEffectiveRouteTargetsAsync(name, default);
            routes.Should().ContainSingle();
            cache.DeferRuntimeRouteTargetsRefresh(name, new[] { new ActiveRouteTargetSnapshot(new RouteTargetIdentity(routes[0].SiteId, routes[0].SiteModelName), new long[] { 1 }) }, routes);
        }
        await db.Client.Deleteable<Domain.SiteCatalog.SiteModelMapping>().Where(m => m.SiteId == a.LinkedSiteId).ExecuteCommandAsync();
        var results = await sync.SyncAllPoolsAsync(default);
        results.Should().HaveCount(2);
        (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "pool-a")).Should().BeFalse();
        (await db.ProxyRouteRules.AnyAsync(r => r.ManagedByPoolId == poolA.Id)).Should().BeFalse();
        (await db.ProxyRouteEntries.AnyAsync(e => e.Id == poolBEntry.Id)).Should().BeTrue();
        (await db.ProxyRouteRules.AnyAsync(r => r.Id == poolBRule.Id)).Should().BeTrue();
        (await db.ProxyRouteEntries.AnyAsync(e => e.Id == manualEntry.Id)).Should().BeTrue();
        (await db.ProxyRouteRules.AnyAsync(r => r.Id == manualRule.Id)).Should().BeTrue();
        (await cache.CaptureEffectiveRouteTargetsAsync("pool-a", default)).Should().BeEmpty("deleted sources must clear deferred snapshots");
        (await cache.CaptureEffectiveRouteTargetsAsync("pool-b", default)).Should().ContainSingle();
        (await cache.CaptureEffectiveRouteTargetsAsync("manual", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Sync_queries_proxy_route_rules_with_scoped_where_clauses()
    {
        var (provisioner, sync, db) = CreateServices();
        var team = await provisioner.ProvisionFromTokensAsync(Input("scope-test", "st@example.test", "team", "token-st"), default);
        await provisioner.UpsertRemoteModelsAsync(team.LinkedSiteId, [("m1", "m1"), ("m2", "m2")], default);

        var pool = new AccountPool { Name = "scope-check-pool", VendorKey = "codex", PlanType = "team" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new AccountPoolMember
        {
            PoolId = pool.Id,
            AccountRefId = team.Id,
            LinkedSiteId = team.LinkedSiteId,
            Priority = 0
        }).ExecuteCommandAsync();

        // 插入若干无关的手工路由规则，确保不会被无 WHERE 全表扫描
        var unrelatedRules = new[]
        {
            new ProxyRouteRule { ExternalModelName = "unrelated-1", UpstreamModelName = "u1", SiteModelName = "u1", SiteId = Guid.NewGuid(), Priority = 1 },
            new ProxyRouteRule { ExternalModelName = "unrelated-2", UpstreamModelName = "u2", SiteModelName = "u2", SiteId = Guid.NewGuid(), Priority = 2 },
        };
        await db.Client.Insertable(unrelatedRules).ExecuteCommandAsync();

        var executedSqls = new List<string>();
        db.Client.Aop.OnLogExecuting = (sql, _) =>
        {
            executedSqls.Add(sql);
        };

        try
        {
            var result = await sync.SyncPoolAsync(pool.Id, default);
            result.RuleCount.Should().Be(2);

            // 过滤出针对 ProxyRouteRules 表的 SELECT 语句
            var ruleSelectQueries = executedSqls
                .Where(sql => sql.Contains("ProxyRouteRules", StringComparison.OrdinalIgnoreCase)
                    && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                .ToList();

            ruleSelectQueries.Should().NotBeEmpty("同步过程中应当执行针对 ProxyRouteRules 的查询");
            foreach (var query in ruleSelectQueries)
            {
                query.Should().Contain("WHERE", "针对 ProxyRouteRules 的查询必须包含 WHERE 子句，禁止全表扫描");
            }

            // 同步后 unrelated 规则原样保留，池规则/计数正确
            var dbUnrelated = await db.ProxyRouteRules
                .Where(r => r.ExternalModelName.StartsWith("unrelated-"))
                .ToListAsync();
            dbUnrelated.Should().HaveCount(2);

            var poolRules = await db.ProxyRouteRules
                .Where(r => r.ManagedByPoolId == pool.Id)
                .ToListAsync();
            poolRules.Should().HaveCount(2);
        }
        finally
        {
            db.Client.Aop.OnLogExecuting = null;
        }
    }

    [Fact]
    public async Task Legacy_unmarked_pool_entry_is_reclaimed_and_member_exclusion_cleans_rules()
    {
        // 升级自愈：旧版本生成的池入口/规则没有 ManagedByPoolId 标记（否则池整体失管：
        // 模型被当"被占用"跳过、排除成员/删池也无法清理）。同步时按严格形态条件重新认领，
        // 认领后排除成员 → 既有 obsolete 清理逻辑接管。
        var (provisioner, sync, db) = CreateServices();
        var teamA = await provisioner.ProvisionFromTokensAsync(Input("legacy-a", "la@p.com", "team", "lt-1"), default);
        var teamB = await provisioner.ProvisionFromTokensAsync(Input("legacy-b", "lb@p.com", "team", "lt-2"), default);
        await provisioner.UpsertRemoteModelsAsync(teamA.LinkedSiteId, [("m1", "m1")], default);
        await provisioner.UpsertRemoteModelsAsync(teamB.LinkedSiteId, [("m1", "m1")], default);

        var pool = new AccountPool { Name = "legacy-pool", VendorKey = "codex", PlanType = "team" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new[]
        {
            new AccountPoolMember { PoolId = pool.Id, AccountRefId = teamA.Id, LinkedSiteId = teamA.LinkedSiteId, Priority = 0 },
            new AccountPoolMember { PoolId = pool.Id, AccountRefId = teamB.Id, LinkedSiteId = teamB.LinkedSiteId, Priority = 1 },
        }).ExecuteCommandAsync();

        // 模拟旧版本产物：无标记入口 + 直连同形规则（全部指向当前成员站点）。
        var legacyEntry = new ProxyRouteEntry { EntryName = "m1" };
        await db.Client.Insertable(legacyEntry).ExecuteCommandAsync();
        await db.Client.Insertable(new[]
        {
            new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = teamA.LinkedSiteId, Priority = 0 },
            new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = teamB.LinkedSiteId, Priority = 0 },
        }).ExecuteCommandAsync();

        var result = await sync.SyncPoolAsync(pool.Id, default);
        result.SkippedModels.Should().BeEmpty("存量入口应被重新认领而不是当占用跳过");

        var claimedEntry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
        claimedEntry.ManagedByPoolId.Should().Be(pool.Id, "无标记的池存量入口被重新认领");
        var claimedRules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        claimedRules.Should().HaveCount(2);
        claimedRules.All(r => r.ManagedByPoolId == pool.Id).Should().BeTrue("存量规则一并打标");

        // 认领后排除成员 B → 再次同步应清理 B 的规则（修复 ec72856 的存量漏删面）。
        var memberB = await db.AccountPoolMembers.FirstAsync(m => m.AccountRefId == teamB.Id);
        memberB.Excluded = true;
        await db.Client.Updateable(memberB).UpdateColumns(m => m.Excluded).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, default);

        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync())
            .Should().ContainSingle(r => r.SiteId == teamA.LinkedSiteId, "排除成员的存量规则在认领后可被正常清理");
    }

    [Fact]
    public async Task Legacy_entry_with_non_member_site_rule_is_not_claimed()
    {
        // 保守防线：入口下存在指向非成员站点的规则（可能是手工添加）→ 整个入口不认领，保持手工语义。
        var (provisioner, sync, db) = CreateServices();
        var team = await provisioner.ProvisionFromTokensAsync(Input("legacy-guard", "lg@p.com", "team", "lt-3"), default);
        await provisioner.UpsertRemoteModelsAsync(team.LinkedSiteId, [("m1", "m1")], default);

        var pool = new AccountPool { Name = "legacy-guard-pool", VendorKey = "codex", PlanType = "team" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new AccountPoolMember
        {
            PoolId = pool.Id, AccountRefId = team.Id, LinkedSiteId = team.LinkedSiteId, Priority = 0
        }).ExecuteCommandAsync();

        var otherSite = new Site { Name = "manual-site", BaseUrl = "https://manual.invalid", ApiKey = "k", IsEnabled = true };
        await db.Client.Insertable(otherSite).ExecuteCommandAsync();
        await db.Client.Insertable(new ProxyRouteEntry { EntryName = "m1" }).ExecuteCommandAsync();
        await db.Client.Insertable(new[]
        {
            // 一条指向成员站点（同形）+ 一条指向非成员站点（可能是手工规则）。
            new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = team.LinkedSiteId, Priority = 0 },
            new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = otherSite.Id, Priority = 1 },
        }).ExecuteCommandAsync();

        var result = await sync.SyncPoolAsync(pool.Id, default);
        result.SkippedModels.Should().Contain("m1", "含非成员站点规则的入口不被认领，按占用跳过");

        var entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
        entry.ManagedByPoolId.Should().BeNull("可能包含手工规则的入口保持未认领");
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1" && r.ManagedByPoolId != null).ToListAsync())
            .Should().BeEmpty("规则不被打标");
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
