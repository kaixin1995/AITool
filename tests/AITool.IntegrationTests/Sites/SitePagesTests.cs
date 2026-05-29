using System.Net;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Sites;

/// <summary>
/// 站点页面集成测试，验证批量删除表单和站点目录入口可正常访问
/// </summary>
public sealed class SitePagesTests
{
    /// <summary>
    /// 验证站点页面通过按钮的 formaction 提交操作，而不是嵌套表单。
    /// </summary>
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
        html.Should().Contain("btnPullAll");
        html.Should().Contain("btn-pull-site");
        html.Should().Contain("modelSelectModal");
        html.Should().Contain("/api/admin/site-catalog/fetch-models/");
        html.Should().Contain("/api/admin/site-catalog/fetch-all-models");
        html.Should().Contain("/api/admin/site-catalog/import-selected");
        html.Should().NotContain("接口路径");
        html.Should().NotContain("自动补 /v1");
        html.Should().NotContain("不补 /v1");
        html.Should().NotContain("<form method=\"post\" asp-page-handler=\"Toggle\"");
        html.Should().NotContain("<form method=\"post\" asp-page-handler=\"Delete\"");
    }

}

/// <summary>
/// 用于构建 SitePagesWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class SitePagesWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 第一个测试站点的固定标识。
    /// </summary>
    internal static readonly Guid FirstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    /// <summary>
    /// 第二个测试站点的固定标识。
    /// </summary>
    internal static readonly Guid SecondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    /// <summary>
    /// 第三个测试站点的固定标识。
    /// </summary>
    internal static readonly Guid ThirdSiteId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-site-pages-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 配置站点页面测试所需的数据库。
    /// </summary>
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

    /// <summary>
    /// 创建客户端后初始化当前测试场景的数据。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备当前测试场景所需的数据。
    /// </summary>
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
                BaseUrl = "https://b.example.com/api/coding/paas/v4",
                EndpointPathMode = AITool.Application.Sites.SiteEndpointPathResolver.VersionedBase,
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

        var firstModelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondModelId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        db.ModelLibraryItems.AddRange(
            new AITool.Domain.Models.ModelLibraryItem
            {
                Id = firstModelId,
                ModelName = "gpt-a",
                DisplayName = "GPT A",
                IsEnabled = true
            },
            new AITool.Domain.Models.ModelLibraryItem
            {
                Id = secondModelId,
                ModelName = "gpt-b",
                DisplayName = "GPT B",
                IsEnabled = true
            });

        db.SiteModelMappings.AddRange(
            new AITool.Domain.SiteCatalog.SiteModelMapping
            {
                SiteId = FirstSiteId,
                ModelLibraryItemId = firstModelId,
                RemoteModelName = "gpt-a",
                LastStatus = "ok",
                IsEnabled = true
            },
            new AITool.Domain.SiteCatalog.SiteModelMapping
            {
                SiteId = SecondSiteId,
                ModelLibraryItemId = secondModelId,
                RemoteModelName = "gpt-b",
                LastStatus = "ok",
                IsEnabled = true
            },
            new AITool.Domain.SiteCatalog.SiteModelMapping
            {
                SiteId = ThirdSiteId,
                ModelLibraryItemId = Guid.NewGuid(),
                RemoteModelName = "gpt-c",
                LastStatus = "unknown",
                IsEnabled = false
            });

        await db.SaveChangesAsync();
    }
}
