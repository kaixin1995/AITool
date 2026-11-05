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
        services.AddSqlSugar($"Data Source={_databasePath};Pooling=False");
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
    public async Task Sync_waits_for_external_writer_before_collision_detection()
    {
        var (provisioner, sync, db) = CreateServices();
        var account = await provisioner.ProvisionFromTokensAsync(Input("external-writer", "external@p.com", "team", "at-1"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(account.LinkedSiteId, [("external-model", "external-model")], CancellationToken.None);
        var pool = new AccountPool { Name = "external-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();

        // An independent connection represents a writer in another process, bypassing both
        // in-process gates. Its uncommitted entry must become visible before sync checks names.
        using var external = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await external.OpenAsync();
        using var transaction = external.BeginTransaction();
        using var command = external.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProxyRouteEntries (Id, EntryName, CreatedAt) VALUES ($id, $name, $created)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$name", "external-model");
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();

        var enteredGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountPoolSyncService.SyncGateAcquiredAsync = _ => { enteredGate.TrySetResult(true); return Task.CompletedTask; };
        try
        {
            var syncing = Task.Run(() => sync.SyncPoolAsync(pool.Id, CancellationToken.None));
            await enteredGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);
            syncing.IsCompleted.Should().BeFalse("the database writer must be acquired before reading collision candidates");
            transaction.Commit();
            var result = await syncing.WaitAsync(TimeSpan.FromSeconds(10));
            result.SkippedModels.Should().Contain("external-model");
            (await db.ProxyRouteEntries.Where(e => e.EntryName == "external-model").ToListAsync())
                .Should().ContainSingle(e => e.ManagedByPoolId == null);
            (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "external-model").ToListAsync()).Should().BeEmpty();
        }
        finally
        {
            AccountPoolSyncService.SyncGateAcquiredAsync = null;
        }
    }

    [Fact]
    public async Task Delete_waits_for_external_writer_and_preserves_its_rule()
    {
        var (provisioner, sync, db) = CreateServices();
        var account = await provisioner.ProvisionFromTokensAsync(Input("delete-writer", "delete@p.com", "team", "at-1"), CancellationToken.None);
        await provisioner.UpsertRemoteModelsAsync(account.LinkedSiteId, [("delete-model", "delete-model")], CancellationToken.None);
        var pool = new AccountPool { Name = "delete-pool", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await sync.CreatePoolAsync(pool, null, CancellationToken.None);

        using var external = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await external.OpenAsync();
        using var transaction = external.BeginTransaction();
        using var command = external.CreateCommand();
        command.Transaction = transaction;
        // Copy a valid generated rule but remove the ownership marker, as a manual writer would.
        command.CommandText = "UPDATE ProxyRouteRules SET ManagedByPoolId = NULL, ManagedPoolMemberSiteId = NULL WHERE ExternalModelName = 'delete-model'";
        (await command.ExecuteNonQueryAsync()).Should().Be(1);

        var enteredGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountPoolSyncService.SyncGateAcquiredAsync = _ => { enteredGate.TrySetResult(true); return Task.CompletedTask; };
        try
        {
            var deleting = Task.Run(() => sync.DeletePoolAsync(pool.Id, CancellationToken.None));
            await enteredGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);
            deleting.IsCompleted.Should().BeFalse("delete must wait for the external writer before inspecting ownership");
            transaction.Commit();
            await deleting.WaitAsync(TimeSpan.FromSeconds(10));
            (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "delete-model").ToListAsync())
                .Should().ContainSingle(r => r.ManagedByPoolId == null);
            (await db.ProxyRouteEntries.Where(e => e.EntryName == "delete-model").ToListAsync())
                .Should().ContainSingle(e => e.ManagedByPoolId == null);
        }
        finally
        {
            AccountPoolSyncService.SyncGateAcquiredAsync = null;
        }
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
    public async Task Sync_reuses_one_rule_query_for_candidates_and_stale_entries()
    {
        var (provisioner, sync, db) = CreateServices();
        var account = await provisioner.ProvisionFromTokensAsync(Input("reuse", "reuse@example.test", "team", "token"), default);
        await provisioner.UpsertRemoteModelsAsync(account.LinkedSiteId, [("kept", "kept"), ("stale", "stale"), ("occupied", "occupied")], default);
        var manualCollision = new ProxyRouteRule { ExternalModelName = "occupied", UpstreamModelName = "occupied", SiteModelName = "occupied", SiteId = account.LinkedSiteId };
        await db.Client.Insertable(manualCollision).ExecuteCommandAsync();
        var pool = new AccountPool { Name = "reuse-rules", VendorKey = "codex", PlanType = "team", AutoIncludeMembers = true };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await sync.SyncPoolAsync(pool.Id, default);

        var retained = (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "kept").ToListAsync()).Single();
        retained.Priority = 42;
        await db.Client.Updateable(retained).UpdateColumns(r => r.Priority).ExecuteCommandAsync();
        var manualStale = new ProxyRouteRule { ExternalModelName = "stale", UpstreamModelName = "manual", SiteModelName = "manual", SiteId = account.LinkedSiteId };
        await db.Client.Insertable(manualStale).ExecuteCommandAsync();
        await db.Client.Deleteable<Domain.SiteCatalog.SiteModelMapping>().Where(m => m.RemoteModelName == "stale").ExecuteCommandAsync();

        var sqls = new List<string>();
        db.Client.Aop.OnLogExecuting = (sql, _) => sqls.Add(sql);
        AccountPoolSyncResult result;
        try
        {
            result = await sync.SyncPoolAsync(pool.Id, default);
        }
        finally
        {
            db.Client.Aop.OnLogExecuting = null;
        }

        var nameQueries = sqls.Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("ProxyRouteRules", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase)
            && sql[(sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase))..].Contains("ExternalModelName", StringComparison.OrdinalIgnoreCase))
            .ToList();
        nameQueries.Should().ContainSingle("候选模型和陈旧入口的规则应在同一批查询中获取");
        result.SkippedModels.Should().ContainSingle().Which.Should().Be("occupied");
        var keptRules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "kept").ToListAsync();
        keptRules.Should().ContainSingle().Which.Priority.Should().Be(42);
        var staleRules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "stale").ToListAsync();
        staleRules.Should().ContainSingle().Which.Id.Should().Be(manualStale.Id);
        (await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "stale")).ManagedByPoolId.Should().BeNull();
        (await db.ProxyRouteRules.Where(r => r.ExternalModelName == "occupied").ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(manualCollision.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(401)]
    public async Task Sync_queries_proxy_route_rules_with_scoped_where_clauses(int modelCount)
    {
        var (provisioner, sync, db) = CreateServices();
        var team = await provisioner.ProvisionFromTokensAsync(Input("scope-test", "st@example.test", "team", "token-st"), default);
        var models = Enumerable.Range(0, modelCount).Select(i => $"model-{i:D3}").ToArray();
        if (models.Length > 0)
            await provisioner.UpsertRemoteModelsAsync(team.LinkedSiteId, models.Select(m => (m, m)).ToArray(), default);

        var pool = new AccountPool { Name = "scope-check-pool", VendorKey = "codex", PlanType = "team" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new AccountPoolMember
        {
            PoolId = pool.Id,
            AccountRefId = team.Id,
            LinkedSiteId = team.LinkedSiteId,
            Priority = 0
        }).ExecuteCommandAsync();
        // A stale owned entry must be included alongside current candidate model names.
        if (modelCount > 0)
            await db.Client.Insertable(new ProxyRouteEntry { EntryName = "stale-model", ManagedByPoolId = pool.Id }).ExecuteCommandAsync();
        var unrelatedRules = new[]
        {
            new ProxyRouteRule { ExternalModelName = "unrelated-1", UpstreamModelName = "u1", SiteModelName = "u1", SiteId = Guid.NewGuid(), Priority = 1 },
            new ProxyRouteRule { ExternalModelName = "unrelated-2", UpstreamModelName = "u2", SiteModelName = "u2", SiteId = Guid.NewGuid(), Priority = 2 },
        };
        await db.Client.Insertable(unrelatedRules).ExecuteCommandAsync();

        var queries = new List<(string Sql, Dictionary<string, object?> Parameters)>();
        db.Client.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("ProxyRouteRules", StringComparison.OrdinalIgnoreCase))
                queries.Add((sql, parameters.ToDictionary(p => p.ParameterName, p => (object?)p.Value)));
        };
        AccountPoolSyncResult result;
        try
        {
            result = await sync.SyncPoolAsync(pool.Id, default);
        }
        finally
        {
            db.Client.Aop.OnLogExecuting = null;
        }

        result.RuleCount.Should().Be(modelCount);
        var expectedNames = models.ToList();
        if (modelCount > 0) expectedNames.Add("stale-model");
        var actualNames = new List<string>();
        var poolSnapshots = 0;
        var nameBatches = 0;
        foreach (var (sql, parameters) in queries)
        {
            var whereIndex = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            whereIndex.Should().BeGreaterThanOrEqualTo(0, "every rules query must be scoped");
            var predicate = sql[whereIndex..];
            // The mutation's pre-snapshot is scoped by pool ID, not by model name.
            var poolMatch = System.Text.RegularExpressions.Regex.Match(predicate,
                @"^WHERE\s*\(\s*`ManagedByPoolId`\s*=\s*(?<param>@\w+)\s*\)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (poolMatch.Success)
            {
                poolSnapshots++;
                parameters.Should().ContainKey(poolMatch.Groups["param"].Value);
                parameters[poolMatch.Groups["param"].Value].Should().NotBeNull();
                parameters[poolMatch.Groups["param"].Value]!.ToString().Should().Be(pool.Id.ToString());
                continue;
            }

            // Require the entire predicate to be an IN over ExternalModelName: an unrelated
            // WHERE, an OR bypass, or an IN on another column cannot satisfy this assertion.
            var nameMatch = System.Text.RegularExpressions.Regex.Match(predicate,
                @"^WHERE\s*\(\s*`ExternalModelName`\s+IN\s*\(\s*(?<values>(?:'(?:''|[^'])*'|@\w+)(?:\s*,\s*(?:'(?:''|[^'])*'|@\w+))*)\s*\)\s*\)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            nameMatch.Success.Should().BeTrue($"unexpected rules predicate: {predicate}");
            var values = System.Text.RegularExpressions.Regex.Matches(nameMatch.Groups["values"].Value, @"'(?:''|[^'])*'|@\w+");
            values.Count.Should().BeInRange(1, 400);
            nameBatches++;
            foreach (System.Text.RegularExpressions.Match value in values)
            {
                if (value.Value.StartsWith("@"))
                {
                    parameters.Should().ContainKey(value.Value);
                    actualNames.Add(parameters[value.Value]!.ToString()!);
                }
                else
                    actualNames.Add(value.Value[1..^1].Replace("''", "'"));
            }
        }
        poolSnapshots.Should().Be(1);
        nameBatches.Should().Be((expectedNames.Count + 399) / 400, "empty sets issue no name query and large sets are batched");
        actualNames.Should().BeEquivalentTo(expectedNames, "every candidate and stale model must be queried exactly once");

        (await db.ProxyRouteRules.Where(r => r.ExternalModelName.StartsWith("unrelated-")).ToListAsync()).Should().HaveCount(2);
        (await db.ProxyRouteRules.Where(r => r.ManagedByPoolId == pool.Id).ToListAsync()).Should().HaveCount(modelCount);
    }

    [Fact]
    public async Task Unmarked_entry_matching_pool_shape_is_preserved_through_sync_exclusion_and_deletion()
    {
        // 即使与旧版池产物完全同形，也可能是手工路由；不能推断归属。
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

        // 两条手工规则恰好覆盖全部成员站点，与旧版池产物无法区分。
        var legacyEntry = new ProxyRouteEntry { EntryName = "m1" };
        await db.Client.Insertable(legacyEntry).ExecuteCommandAsync();
        var manualRules = new[]
        {
            new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = teamA.LinkedSiteId, Priority = 0 },
            new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = teamB.LinkedSiteId, Priority = 1 },
        };
        await db.Client.Insertable(manualRules).ExecuteCommandAsync();

        async Task AssertPreservedAsync()
        {
            var entry = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "m1");
            entry.Id.Should().Be(legacyEntry.Id);
            entry.ManagedByPoolId.Should().BeNull();
            var rules = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
            rules.Select(r => (r.Id, r.SiteId, r.Priority)).Should().BeEquivalentTo(
                manualRules.Select(r => (r.Id, r.SiteId, r.Priority)));
            rules.Should().OnlyContain(r => r.ManagedByPoolId == null && r.ManagedPoolMemberSiteId == null);
        }

        var result = await sync.SyncPoolAsync(pool.Id, default);
        result.SkippedModels.Should().Contain("m1");
        await AssertPreservedAsync();

        var memberB = await db.AccountPoolMembers.FirstAsync(m => m.AccountRefId == teamB.Id);
        memberB.Excluded = true;
        await db.Client.Updateable(memberB).UpdateColumns(m => m.Excluded).ExecuteCommandAsync();
        (await sync.SyncPoolAsync(pool.Id, default)).SkippedModels.Should().Contain("m1");
        await AssertPreservedAsync();

        await sync.DeletePoolAsync(pool.Id, default);
        await AssertPreservedAsync();
    }

    [Theory]
    [InlineData("excluded")]
    [InlineData("deleted")]
    [InlineData("single")]
    public async Task Administrator_can_recover_legacy_entry_without_inference(string scenario)
    {
        var (provisioner, sync, db) = CreateServices();
        var a = await provisioner.ProvisionFromTokensAsync(Input("recover-a", "ra@p.com", "team", "ra"), default);
        var b = await provisioner.ProvisionFromTokensAsync(Input("recover-b", "rb@p.com", "team", "rb"), default);
        await provisioner.UpsertRemoteModelsAsync(a.LinkedSiteId, [("m1", "m1")], default);
        await provisioner.UpsertRemoteModelsAsync(b.LinkedSiteId, [("m1", "m1")], default);
        var pool = new AccountPool { Name = "recover", VendorKey = "codex", PlanType = "team" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new AccountPoolMember { PoolId = pool.Id, AccountRefId = a.Id, LinkedSiteId = a.LinkedSiteId }).ExecuteCommandAsync();
        if (scenario == "excluded")
            await db.Client.Insertable(new AccountPoolMember { PoolId = pool.Id, AccountRefId = b.Id, LinkedSiteId = b.LinkedSiteId, Excluded = true }).ExecuteCommandAsync();
        if (scenario == "deleted")
        {
            await db.Client.Insertable(new AccountPoolMember { PoolId = pool.Id, AccountRefId = b.Id, LinkedSiteId = b.LinkedSiteId }).ExecuteCommandAsync();
            await db.Client.Deleteable<AITool.Domain.Codex.CodexAccount>().Where(x => x.Id == b.Id).ExecuteCommandAsync();
        }
        var entry = new ProxyRouteEntry { EntryName = "m1" };
        var first = new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = a.LinkedSiteId, Priority = 7 };
        var stale = new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = b.LinkedSiteId };
        var manual = new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "other", SiteModelName = "other", SiteId = b.LinkedSiteId };
        var rules = scenario == "single" ? new[] { first } : new[] { first, stale, manual };
        await db.Client.Insertable(entry).ExecuteCommandAsync();
        await db.Client.Insertable(rules).ExecuteCommandAsync();
        (await sync.SyncPoolAsync(pool.Id, default)).SkippedModels.Should().Contain("m1");
        var ids = rules.Select(r => r.Id).ToArray();
        var selected = scenario == "single" ? new[] { first.Id } : new[] { first.Id, stale.Id };
        await sync.ClaimLegacyEntryAsync(pool.Id, entry.Id, ids, selected, default);
        (await db.ProxyRouteEntries.FirstAsync(e => e.Id == entry.Id)).ManagedByPoolId.Should().Be(pool.Id);
        (await sync.SyncPoolAsync(pool.Id, default)).SkippedModels.Should().BeEmpty();
        var remaining = await db.ProxyRouteRules.Where(r => r.ExternalModelName == "m1").ToListAsync();
        remaining.Should().ContainSingle(r => r.Id == first.Id && r.ManagedByPoolId == pool.Id && r.Priority == 7);
        if (scenario != "single")
        {
            remaining.Should().NotContain(r => r.Id == stale.Id);
            remaining.Should().ContainSingle(r => r.Id == manual.Id && r.ManagedByPoolId == null);
        }
    }

    [Fact]
    public async Task Legacy_claim_rejects_stale_snapshot_and_other_pool_rules_atomically()
    {
        var (_, sync, db) = CreateServices();
        var pool = new AccountPool { Name = "claim-guard", VendorKey = "codex" };
        var entry = new ProxyRouteEntry { EntryName = "m1" };
        var rule = new ProxyRouteRule { ExternalModelName = "m1", UpstreamModelName = "m1", SiteModelName = "m1", SiteId = Guid.NewGuid() };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(entry).ExecuteCommandAsync();
        await db.Client.Insertable(rule).ExecuteCommandAsync();
        await FluentActions.Invoking(() => sync.ClaimLegacyEntryAsync(pool.Id, entry.Id, [], [rule.Id], default)).Should().ThrowAsync<InvalidOperationException>();
        rule.ManagedByPoolId = Guid.NewGuid();
        await db.Client.Updateable(rule).UpdateColumns(r => r.ManagedByPoolId).ExecuteCommandAsync();
        await FluentActions.Invoking(() => sync.ClaimLegacyEntryAsync(pool.Id, entry.Id, [rule.Id], [rule.Id], default)).Should().ThrowAsync<InvalidOperationException>();
        (await db.ProxyRouteEntries.FirstAsync(e => e.Id == entry.Id)).ManagedByPoolId.Should().BeNull();
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

    [Fact]
    public async Task Reconciliation_background_pass_detects_updates_without_row_count_changes()
    {
        var (provisioner, _, db) = CreateServices();
        var account = await provisioner.ProvisionFromTokensAsync(Input("reconcile", "reconcile@p.com", "team", "at-reconcile"), default);
        await provisioner.UpsertRemoteModelsAsync(account.LinkedSiteId, [("m1", "m1")], default);
        var pool = new AccountPool { Name = "reconcile-pool", VendorKey = "codex", PlanType = "team" };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new AccountPoolMember
        {
            PoolId = pool.Id, AccountRefId = account.Id, LinkedSiteId = account.LinkedSiteId
        }).ExecuteCommandAsync();

        var reconciliation = new AccountPoolReconciliationService(
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AccountPoolReconciliationService>.Instance);
        await reconciliation.RunOnceAsync(default);
        (await db.ProxyRouteRules.Where(r => r.ManagedByPoolId == pool.Id).ToListAsync()).Should().ContainSingle();
        await reconciliation.StartAsync(default);
        try
        {
            async Task WaitForRuleCountAsync(int count)
            {
                // 后台 5s 轮询 + 全量并发下的调度/DB 竞争：等待型断言需要充分余量（曾 15s 在全量下超时）。
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while ((await db.ProxyRouteRules.Where(r => r.ManagedByPoolId == pool.Id).ToListAsync()).Count != count)
                    await Task.Delay(100, timeout.Token);
            }

            var mapping = await db.SiteModelMappings.FirstAsync(m => m.SiteId == account.LinkedSiteId);
            mapping.IsEnabled = false;
            await db.Client.Updateable(mapping).UpdateColumns(m => m.IsEnabled).ExecuteCommandAsync();
            await WaitForRuleCountAsync(0);

            mapping.IsEnabled = true;
            await db.Client.Updateable(mapping).UpdateColumns(m => m.IsEnabled).ExecuteCommandAsync();
            await WaitForRuleCountAsync(1);

            var member = await db.AccountPoolMembers.FirstAsync(m => m.PoolId == pool.Id);
            member.Excluded = true;
            await db.Client.Updateable(member).UpdateColumns(m => m.Excluded).ExecuteCommandAsync();
            await WaitForRuleCountAsync(0);
            reconciliation.GetStatus().PoolFailures.Should().BeEmpty();
            reconciliation.GetStatus().ScanFailure.Should().BeNull();
        }
        finally
        {
            await reconciliation.StopAsync(default);
            reconciliation.Dispose();
        }
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
