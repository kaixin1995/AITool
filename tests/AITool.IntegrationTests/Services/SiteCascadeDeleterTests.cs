using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
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
            ConnectionString = $"Data Source={dbPath}",
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
