using System.Net;
using System.Net.Http.Headers;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Models;

/// <summary>
/// 验证模型编辑页支持手动新增关联站点。
/// </summary>
public sealed class ModelEditPageTests
{
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
        var mapping = await db.SiteModelMappings.FirstAsync(x => x.SiteId == Guid.Parse("22222222-2222-2222-2222-222222222222") && x.RemoteModelName == "gpt-manual-alpha");
        mapping.Should().NotBeNull();
        mapping!.ModelLibraryItemId.Should().Be(ModelEditPageWebApplicationFactory.ModelId);
        mapping.LastStatus.Should().Be("manual");
        mapping.IsEnabled.Should().BeTrue();
    }

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

internal sealed class ModelEditPageWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid ModelId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-model-edit-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
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
        SqlSugarSetup.InitializeDatabase(db.Client);

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
