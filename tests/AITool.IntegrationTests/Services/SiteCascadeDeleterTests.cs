using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using AITool.Web.Controllers.Admin;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using SqlSugar;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// SiteCascadeDeleter 单元测试：验证级联清理逻辑（删站点 → 清映射/规则/空入口）。
/// 这是 Sites/Models 删除共用的核心逻辑，必须有测试覆盖。
/// </summary>
public sealed class SiteCascadeDeleterTests
{
    /// <summary>
    /// 创建隔离的临时 SQLite 数据库 + AppDbContext + SiteCascadeDeleter。
    /// </summary>
    private static async Task<(AppDbContext db, SiteCascadeDeleter deleter, string dbPath)> CreateAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aitool-cascade-{Guid.NewGuid():N}.db");
        var sqlSugar = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={dbPath};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        SqlSugarSetup.InitializeDatabase(sqlSugar);
        var db = new AppDbContext(sqlSugar, new SemaphoreSlim(1, 1));
        var deleter = new SiteCascadeDeleter(db);
        return (db, deleter, dbPath);
    }

    /// <summary>
    /// 删除单个站点时应级联清理其映射、路由规则。
    /// </summary>
    [Fact]
    public async Task RemoveSitesAsync_single_site_cascades_mappings_and_rules()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var siteId = Guid.NewGuid();
            var modelId = Guid.NewGuid();
            SeedData(db, siteId, modelId, "test-model", "test-site");

            var deleted = await deleter.RemoveSitesAsync([siteId], default);

            deleted.Should().Be(1);
            (await db.Sites.CountAsync()).Should().Be(0);
            (await db.SiteModelMappings.CountAsync()).Should().Be(0);
            (await db.ProxyRouteRules.CountAsync()).Should().Be(0);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// 删除站点后，失去全部规则的路由入口应被清理。
    /// </summary>
    [Fact]
    public async Task RemoveSitesAsync_cleans_up_empty_route_entries()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var siteId = Guid.NewGuid();
            var modelId = Guid.NewGuid();
            db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = "lonely-entry" });
            db.Sites.Add(new Site { Id = siteId, Name = "S", BaseUrl = "https://x", ApiKey = "k" });
            db.ModelLibraryItems.Add(new ModelLibraryItem { Id = modelId, ModelName = "m" });
            db.SiteModelMappings.Add(new SiteModelMapping { SiteId = siteId, ModelLibraryItemId = modelId, RemoteModelName = "m" });
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = "lonely-entry", UpstreamModelName = "m",
                SiteId = siteId, SiteModelName = "m", IsEnabled = true
            });

            await deleter.RemoveSitesAsync([siteId], default);

            // lonely-entry 的唯一规则被删，入口应被清理。
            (await db.ProxyRouteEntries.CountAsync(x => x.EntryName == "lonely-entry")).Should().Be(0);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// 删除站点时，如果某路由入口还有其他站点的规则，入口应保留。
    /// </summary>
    [Fact]
    public async Task RemoveSitesAsync_keeps_route_entry_with_remaining_rules()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var siteA = Guid.NewGuid();
            var siteB = Guid.NewGuid();
            db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = "shared-entry" });
            db.Sites.Add(new Site { Id = siteA, Name = "A", BaseUrl = "https://a", ApiKey = "k" });
            db.Sites.Add(new Site { Id = siteB, Name = "B", BaseUrl = "https://b", ApiKey = "k" });
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = "shared-entry", UpstreamModelName = "m",
                SiteId = siteA, SiteModelName = "m", IsEnabled = true
            });
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = "shared-entry", UpstreamModelName = "m",
                SiteId = siteB, SiteModelName = "m", IsEnabled = true
            });

            await deleter.RemoveSitesAsync([siteA], default);

            // siteB 的规则还在，入口应保留。
            (await db.ProxyRouteEntries.CountAsync(x => x.EntryName == "shared-entry")).Should().Be(1);
            (await db.ProxyRouteRules.CountAsync()).Should().Be(1);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// 空集合返回 0，不报错。
    /// </summary>
    [Fact]
    public async Task RemoveSitesAsync_empty_returns_zero()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var deleted = await deleter.RemoveSitesAsync([], default);
            deleted.Should().Be(0);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// 不存在的站点 ID 返回 0。
    /// </summary>
    [Fact]
    public async Task RemoveSitesAsync_nonexistent_returns_zero()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var deleted = await deleter.RemoveSitesAsync([Guid.NewGuid()], default);
            deleted.Should().Be(0);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// CleanupEmptyRouteEntriesAsync 可单独调用清理空入口。
    /// </summary>
    [Fact]
    public async Task CleanupEmptyRouteEntriesAsync_removes_orphan_entries()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = "orphan" });
            db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = "alive" });
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = "alive", UpstreamModelName = "m",
                SiteId = Guid.NewGuid(), SiteModelName = "m", IsEnabled = true
            });

            await deleter.CleanupEmptyRouteEntriesAsync(["orphan", "alive"], default);

            (await db.ProxyRouteEntries.CountAsync(x => x.EntryName == "orphan")).Should().Be(0);
            (await db.ProxyRouteEntries.CountAsync(x => x.EntryName == "alive")).Should().Be(1);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task Site_delete_preserves_reference_parent_and_does_not_rebind_recreated_target()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var siteId = Guid.NewGuid();
            SeedData(db, siteId, Guid.NewGuid(), "target", "synthetic");
            var target = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "target");
            var parent = new ProxyRouteEntry { EntryName = "parent" };
            db.ProxyRouteEntries.Add(parent);
            // Deliberately matching sentinel fields prove that TargetKind, not site/name fields,
            // determines direct-site cascade membership. Disabled/broken references still count.
            var reference = new ProxyRouteRule
            {
                ExternalModelName = parent.EntryName,
                TargetKind = "RouteEntry",
                TargetRouteEntryId = target.Id,
                TargetRouteEntryName = target.EntryName,
                SiteId = siteId,
                SiteModelName = "target",
                IsEnabled = false,
            };
            db.ProxyRouteRules.Add(reference);
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = parent.EntryName, SiteId = siteId, SiteModelName = "target",
            });

            await deleter.RemoveSitesAsync([siteId], default);
            await deleter.CleanupEmptyRouteEntriesAsync([parent.EntryName], default);

            (await db.ProxyRouteEntries.AnyAsync(e => e.Id == target.Id)).Should().BeFalse();
            (await db.ProxyRouteEntries.AnyAsync(e => e.Id == parent.Id)).Should().BeTrue();
            (await db.ProxyRouteRules.ToListAsync()).Should().ContainSingle(r => r.Id == reference.Id);
            var recreated = new ProxyRouteEntry { EntryName = target.EntryName };
            db.ProxyRouteEntries.Add(recreated);
            recreated.Id.Should().NotBe(target.Id);
            (await db.ProxyRouteRules.FirstAsync(r => r.Id == reference.Id)).TargetRouteEntryId.Should().Be(target.Id);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task Cascade_failure_rolls_back_deleted_site_and_graph()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var siteId = Guid.NewGuid();
            SeedData(db, siteId, Guid.NewGuid(), "target", "synthetic");
            await db.Client.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_entry_delete BEFORE DELETE ON ProxyRouteEntries BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");

            var remove = () => deleter.RemoveSitesAsync([siteId], default);
            await remove.Should().ThrowAsync<Exception>();

            (await db.Sites.AnyAsync(s => s.Id == siteId)).Should().BeTrue();
            (await db.SiteModelMappings.CountAsync()).Should().Be(1);
            (await db.ProxyRouteRules.CountAsync()).Should().Be(1);
            (await db.ProxyRouteEntries.CountAsync()).Should().Be(1);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task Cleanup_inside_owned_graph_transaction_does_not_nest_gate_or_transaction()
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = "orphan" });
            using var lease = await RouteGraphMutationGate.AcquireAsync();
            await db.Client.Ado.BeginTranAsync();
            await deleter.CleanupEmptyRouteEntriesInGraphMutationAsync(["orphan"], default);
            await db.Client.Ado.RollbackTranAsync();
            (await db.ProxyRouteEntries.CountAsync()).Should().Be(1);
        }
        finally { Cleanup(dbPath); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_cascade_uses_only_direct_site_mapping_keys_not_model_or_reference_names(bool deleteMapping)
    {
        var (db, deleter, dbPath) = await CreateAsync();
        try
        {
            var siteId = Guid.NewGuid();
            var modelId = Guid.NewGuid();
            SeedData(db, siteId, modelId, "model-name", "synthetic");
            var mapping = await db.SiteModelMappings.FirstAsync(m => m.ModelLibraryItemId == modelId);
            var target = await db.ProxyRouteEntries.FirstAsync(e => e.EntryName == "model-name");
            db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = "parent" });
            var reference = new ProxyRouteRule
            {
                ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = target.Id,
                TargetRouteEntryName = target.EntryName, SiteId = siteId, SiteModelName = mapping.RemoteModelName,
            };
            db.ProxyRouteRules.Add(reference);
            var unrelated = new ProxyRouteRule
            {
                ExternalModelName = "model-name", SiteId = Guid.NewGuid(), SiteModelName = "unrelated-model",
            };
            db.ProxyRouteRules.Add(unrelated);
            using var services = new ServiceCollection().AddMemoryCache().BuildServiceProvider();
            var cache = new ProxyRequestMetadataCache(services.GetRequiredService<IMemoryCache>(), services.GetRequiredService<IServiceScopeFactory>());
            var controller = new ModelsApiController(db, cache, null!, null!, null!, deleter, null!, null!);

            if (deleteMapping) await controller.DeleteMapping(modelId, mapping.Id, default);
            else await controller.Delete(modelId, default);

            (await db.SiteModelMappings.CountAsync()).Should().Be(0);
            (await db.ProxyRouteRules.ToListAsync()).Select(r => r.Id).Should().BeEquivalentTo([reference.Id, unrelated.Id]);
            (await db.ProxyRouteEntries.AnyAsync(e => e.Id == target.Id)).Should().BeTrue("an unrelated leaf sharing the model name is not cascade-owned");
            (await db.ProxyRouteEntries.AnyAsync(e => e.EntryName == "parent")).Should().BeTrue();
            (await db.ProxyRouteRules.FirstAsync(r => r.Id == reference.Id)).TargetRouteEntryId.Should().Be(target.Id);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task Removing_site_leaf_clears_deferred_parent_credentials_even_when_source_entry_survives()
    {
        var (db, _, dbPath) = await CreateAsync();
        try
        {
            var source = new ProxyRouteEntry { EntryName = "source" };
            var parent = new ProxyRouteEntry { EntryName = "parent" };
            var site = new Site { Name = "synthetic", BaseUrl = "https://never-called.invalid", ApiKey = "synthetic-secret", SupportsOpenAi = true };
            var survivingSite = new Site { Name = "survivor", BaseUrl = "https://also-never-called.invalid", ApiKey = "surviving-secret", SupportsOpenAi = true };
            db.ProxyRouteEntries.AddRange(source, parent);
            db.Sites.AddRange(site, survivingSite);
            // A referenced source must contain only direct-site rows. A broken reference here
            // would make parent -> source Nested before deletion, so no parent snapshot exists.
            db.ProxyRouteRules.AddRange(
                new ProxyRouteRule { ExternalModelName = source.EntryName, SiteId = site.Id, SiteModelName = "leaf" },
                new ProxyRouteRule { ExternalModelName = source.EntryName, SiteId = survivingSite.Id, SiteModelName = "surviving-leaf" },
                new ProxyRouteRule { ExternalModelName = parent.EntryName, TargetKind = "RouteEntry", TargetRouteEntryId = source.Id });
            using var services = new ServiceCollection().AddMemoryCache().AddSingleton(db).BuildServiceProvider();
            var cache = new ProxyRequestMetadataCache(services.GetRequiredService<IMemoryCache>(), services.GetRequiredService<IServiceScopeFactory>());
            var before = await cache.CaptureEffectiveRouteTargetsAsync(source.EntryName, default);
            var activeParent = await cache.CaptureEffectiveRouteTargetsAsync(parent.EntryName, default);
            before.Should().HaveCount(2);
            activeParent.Should().HaveCount(2, "the fixture must capture a valid nonnested parent before deletion");
            cache.DeferRuntimeRouteTargetsRefresh(source.EntryName,
                [new ActiveRouteTargetSnapshot(new RouteTargetIdentity(site.Id, "leaf"), [1L])], before);

            await new SiteCascadeDeleter(db, cache).RemoveSitesAsync([site.Id], default);

            (await db.ProxyRouteEntries.AnyAsync(e => e.Id == source.Id)).Should().BeTrue("the remaining physical leaf keeps its source entry");
            var freshSource = await cache.CaptureEffectiveRouteTargetsAsync(source.EntryName, default);
            var freshParent = await cache.CaptureEffectiveRouteTargetsAsync(parent.EntryName, default);
            freshSource.Should().ContainSingle().Which.ApiKey.Should().Be("surviving-secret");
            freshParent.Should().ContainSingle().Which.ApiKey.Should().Be("surviving-secret");
            activeParent.Single(r => r.SiteId == site.Id).ApiKey.Should().Be("synthetic-secret", "already captured requests retain detached snapshots");
        }
        finally { Cleanup(dbPath); }
    }

    private static void SeedData(AppDbContext db, Guid siteId, Guid modelId, string modelName, string siteName)
    {
        db.Sites.Add(new Site { Id = siteId, Name = siteName, BaseUrl = "https://x", ApiKey = "k" });
        db.ModelLibraryItems.Add(new ModelLibraryItem { Id = modelId, ModelName = modelName });
        db.SiteModelMappings.Add(new SiteModelMapping { SiteId = siteId, ModelLibraryItemId = modelId, RemoteModelName = modelName });
        db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = modelName });
        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            ExternalModelName = modelName, UpstreamModelName = modelName,
            SiteId = siteId, SiteModelName = modelName, IsEnabled = true
        });
    }

    private static void Cleanup(string dbPath)
    {
        try { File.Delete(dbPath); } catch { }
        try { File.Delete(dbPath + "-wal"); } catch { }
        try { File.Delete(dbPath + "-shm"); } catch { }
    }
}
