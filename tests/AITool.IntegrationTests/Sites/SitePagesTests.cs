using System.Net;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Sites;

// 站点页面集成测试，验证批量删除表单和站点目录入口可正常访问
public sealed class SitePagesTests
{
    [Fact]
    public async Task Get_sites_page_uses_button_formaction_instead_of_nested_forms()
    {
        await using var factory = new SitePagesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Sites");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("handler=Toggle");
        html.Should().Contain("handler=Delete");
        html.Should().NotContain("<form method=\"post\" asp-page-handler=\"Toggle\"");
        html.Should().NotContain("<form method=\"post\" asp-page-handler=\"Delete\"");
    }

    [Fact]
    public async Task Get_site_catalog_page_returns_ok()
    {
        await using var factory = new SitePagesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/SiteCatalog");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("站点管理");
    }
}

internal sealed class SitePagesWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid FirstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SecondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid ThirdSiteId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-site-pages-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        db.Sites.AddRange(
            new AITool.Domain.Sites.Site
            {
                Id = FirstSiteId,
                Name = "Site A",
                BaseUrl = "https://a.example.com",
                ApiKey = "key-a",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new AITool.Domain.Sites.Site
            {
                Id = SecondSiteId,
                Name = "Site B",
                BaseUrl = "https://b.example.com",
                ApiKey = "key-b",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new AITool.Domain.Sites.Site
            {
                Id = ThirdSiteId,
                Name = "Site C",
                BaseUrl = "https://c.example.com",
                ApiKey = "key-c",
                ProtocolType = "OpenAI",
                IsEnabled = true
            });

        db.SiteModelMappings.AddRange(
            new AITool.Domain.SiteCatalog.SiteModelMapping
            {
                SiteId = FirstSiteId,
                ModelLibraryItemId = Guid.NewGuid(),
                RemoteModelName = "gpt-a",
                LastStatus = "ok",
                IsEnabled = true
            },
            new AITool.Domain.SiteCatalog.SiteModelMapping
            {
                SiteId = SecondSiteId,
                ModelLibraryItemId = Guid.NewGuid(),
                RemoteModelName = "gpt-b",
                LastStatus = "ok",
                IsEnabled = true
            },
            new AITool.Domain.SiteCatalog.SiteModelMapping
            {
                SiteId = ThirdSiteId,
                ModelLibraryItemId = Guid.NewGuid(),
                RemoteModelName = "gpt-c",
                LastStatus = "ok",
                IsEnabled = true
            });

        await db.SaveChangesAsync();
    }
}
