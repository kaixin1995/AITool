using System.Net;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 模型编辑页面集成测试，验证手动新增关联站点和内联删除行为。
/// <para>
/// 此测试从 AITool.IntegrationTests 迁移至此，因为模型管理页面
/// 已从 Web 宿主迁移到 Admin 宿主。
/// </para>
/// </summary>
public sealed class ModelEditPageTests
{
    /// <summary>
    /// 验证模型编辑页面展示手动新增关联站点的表单。
    /// </summary>
    [Fact]
    public async Task Get_model_edit_page_shows_manual_site_mapping_form()
    {
        await using var factory = new ModelEditPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/Admin/Models/Edit/{ModelEditPageWebApplicationFactory.ModelId}");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain("handler=AddMapping");
        html.Should().Contain("NewMapping.SiteId");
        html.Should().Contain("NewMapping.RemoteModelName");
        html.Should().Contain("默认使用当前模型名，也可手动调整");
        html.Should().Contain("value=\"gpt-manual\"");
        html.Should().Contain("Beta Site");
        html.Should().NotContain("Alpha Site</option>");
    }

    /// <summary>
    /// 验证模型列表页面使用内联删除行为而不是 confirm 弹窗。
    /// </summary>
    [Fact]
    public async Task Get_models_page_contains_inline_delete_behavior()
    {
        await using var factory = new ModelEditPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Models");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain("model-delete-form");
        html.Should().Contain("deleteModelInline");
        html.Should().Contain("X-Requested-With");
        html.Should().Contain("data-model-id");
        html.Should().NotContain("onclick=\"return confirm('确认删除该模型？')\"");
    }

    /// <summary>
    /// 验证通过 AJAX 删除模型会返回 JSON 而不是重定向页面。
    /// </summary>
    [Fact]
    public async Task Post_delete_model_ajax_returns_json_without_redirecting_page()
    {
        await using var factory = new ModelEditPageWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var getResponse = await client.GetAsync("/Admin/Models");
        var html = await getResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiForgeryToken(html);

        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token)
        ]);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Models?handler=Delete&modelId={ModelEditPageWebApplicationFactory.ModelId}")
        {
            Content = form
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri("http://localhost/Admin/Models");

        var postResponse = await client.SendAsync(request);
        var body = await postResponse.Content.ReadAsStringAsync();

        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        body.Should().Contain("success");
        body.Should().Contain("message");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modelExists = await db.ModelLibraryItems.AnyAsync(x => x.Id == ModelEditPageWebApplicationFactory.ModelId);
        modelExists.Should().BeFalse();
    }

    /// <summary>
    /// 验证通过表单提交新增站点映射会创建手动关联记录。
    /// </summary>
    [Fact]
    public async Task Post_add_mapping_creates_manual_site_mapping()
    {
        await using var factory = new ModelEditPageWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var getResponse = await client.GetAsync($"/Admin/Models/Edit/{ModelEditPageWebApplicationFactory.ModelId}");
        var html = await getResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiForgeryToken(html);

        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("NewMapping.SiteId", "22222222-2222-2222-2222-222222222222"),
            new KeyValuePair<string, string>("NewMapping.RemoteModelName", "gpt-manual-alpha"),
            new KeyValuePair<string, string>("NewMapping.IsEnabled", "true")
        ]);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Models/Edit/{ModelEditPageWebApplicationFactory.ModelId}?handler=AddMapping")
        {
            Content = form
        };
        request.Headers.Referrer = new Uri($"http://localhost/Admin/Models/Edit/{ModelEditPageWebApplicationFactory.ModelId}");

        var postResponse = await client.SendAsync(request);
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mapping = await db.SiteModelMappings.FirstOrDefaultAsync(x => x.SiteId == Guid.Parse("22222222-2222-2222-2222-222222222222") && x.RemoteModelName == "gpt-manual-alpha");
        mapping.Should().NotBeNull();
        mapping!.ModelLibraryItemId.Should().Be(ModelEditPageWebApplicationFactory.ModelId);
        mapping.LastStatus.Should().Be("manual");
        mapping.IsEnabled.Should().BeTrue();
    }

    /// <summary>
    /// 从 HTML 中提取防伪造令牌。
    /// </summary>
    private static string ExtractAntiForgeryToken(string html)
    {
        const string tokenName = "__RequestVerificationToken";
        var marker = $"name=\"{tokenName}\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        start += marker.Length;
        var end = html.IndexOf('"', start);
        end.Should().BeGreaterThan(start);
        return html[start..end];
    }
}

/// <summary>
/// 用于构建 ModelEditPageTests 对应的 Admin 测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class ModelEditPageWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    /// <summary>
    /// 测试使用的模型标识。
    /// </summary>
    internal static readonly Guid ModelId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-model-edit-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 配置模型编辑页面测试所需的数据库。
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

        db.ModelLibraryItems.Add(new AITool.Domain.Models.ModelLibraryItem
        {
            Id = ModelId,
            ModelName = "gpt-manual",
            DisplayName = "GPT Manual",
            IsEnabled = true
        });

        db.Sites.AddRange(
            new AITool.Domain.Sites.Site
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Alpha Site",
                BaseUrl = "https://alpha.example.com",
                ApiKey = "key-alpha",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new AITool.Domain.Sites.Site
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "Beta Site",
                BaseUrl = "https://beta.example.com",
                ApiKey = "key-beta",
                ProtocolType = "OpenAI",
                IsEnabled = true
            });

        db.SiteModelMappings.Add(new AITool.Domain.SiteCatalog.SiteModelMapping
        {
            SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ModelLibraryItemId = ModelId,
            RemoteModelName = "gpt-manual",
            LastStatus = "imported",
            IsEnabled = true
        });

        await db.SaveChangesAsync();
    }
}
