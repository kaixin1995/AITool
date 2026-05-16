using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Pages.Admin.Sites;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.IntegrationTests.Sites;

/// <summary>
/// 站点管理测试，验证批量删除站点会同步删除关联映射
/// </summary>
public sealed class SiteBulkDeleteTests
{
    /// <summary>
    /// 验证批量删除站点时，会一并删除关联的模型映射。
    /// </summary>
    [Fact]
    public async Task OnPostBulkDeleteAsync_removes_selected_sites_and_related_mappings()
    {
        await using var db = CreateDbContext();
        await SeedAsync(db);
        var page = new IndexModel(db)
        {
            SelectedSiteIds =
            [
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222")
            ]
        };

        var result = await page.OnPostBulkDeleteAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();

        var remainingSites = await db.Sites.OrderBy(x => x.Name).ToListAsync();
        var remainingMappings = await db.SiteModelMappings.OrderBy(x => x.SiteId).ToListAsync();

        remainingSites.Should().ContainSingle();
        remainingSites[0].Name.Should().Be("Site C");
        remainingMappings.Should().ContainSingle();
        remainingMappings[0].SiteId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    }

    /// <summary>
    /// 创建当前测试要使用的数据库上下文。
    /// </summary>
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"aitool-site-bulk-delete-{Guid.NewGuid():N}.db")}")
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>
    /// 准备当前测试场景所需的数据。
    /// </summary>
    private static async Task SeedAsync(AppDbContext db)
    {
        var siteA = new Site
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Site A",
            BaseUrl = "https://a.example.com",
            ApiKey = "key-a",
            ProtocolType = "OpenAI",
            IsEnabled = true
        };
        var siteB = new Site
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Site B",
            BaseUrl = "https://b.example.com",
            ApiKey = "key-b",
            ProtocolType = "OpenAI",
            IsEnabled = true
        };
        var siteC = new Site
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Name = "Site C",
            BaseUrl = "https://c.example.com",
            ApiKey = "key-c",
            ProtocolType = "OpenAI",
            IsEnabled = true
        };

        db.Sites.AddRange(siteA, siteB, siteC);
        db.SiteModelMappings.AddRange(
            new SiteModelMapping
            {
                SiteId = siteA.Id,
                ModelLibraryItemId = Guid.NewGuid(),
                RemoteModelName = "gpt-a",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = siteB.Id,
                ModelLibraryItemId = Guid.NewGuid(),
                RemoteModelName = "gpt-b",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = siteC.Id,
                ModelLibraryItemId = Guid.NewGuid(),
                RemoteModelName = "gpt-c",
                LastStatus = "ok",
                IsEnabled = true
            });

        await db.SaveChangesAsync();
    }
}
