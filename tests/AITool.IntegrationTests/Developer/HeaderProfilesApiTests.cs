using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.Developer;

public sealed class HeaderProfilesApiTests
{
    private const string BaseUrl = "/api/admin/developer/header-profiles";

    [Fact]
    public async Task List_Returns_BuiltIn_Profiles()
    {
        await using var factory = new HeaderProfilesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(BaseUrl);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var profiles = await response.Content.ReadFromJsonAsync<List<HeaderProfileDto>>(jsonOptions);
        profiles.Should().NotBeNull();
        profiles!.Count.Should().BeGreaterThanOrEqualTo(5);

        profiles.Should().Contain(p => p.Key == "OpenCode" && p.IsBuiltIn);
        profiles.Should().Contain(p => p.Key == "ClaudeCode" && p.IsBuiltIn);
        profiles.Should().Contain(p => p.Key == "CodexCli" && p.IsBuiltIn);
        profiles.Should().Contain(p => p.Key == "Antigravity" && p.IsBuiltIn);
        profiles.Should().Contain(p => p.Key == "GeminiCli" && p.IsBuiltIn);
    }

    [Fact]
    public async Task Create_And_Get_And_Update_And_Delete_Custom_Profile_Lifecycle()
    {
        await using var factory = new HeaderProfilesWebApplicationFactory();
        using var client = factory.CreateClient();

        // 1. Create
        var createPayload = new
        {
            key = "custom-cursor-test",
            name = "Cursor IDE Test",
            description = "Cursor testing emulation",
            headersJson = "{\"User-Agent\": \"cursor/0.40.0\", \"x-cursor-session\": \"${guid}\"}",
            isEnabled = true,
            sortOrder = 50
        };

        var createRes = await client.PostAsJsonAsync(BaseUrl, createPayload);
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var createDoc = await JsonDocument.ParseAsync(await createRes.Content.ReadAsStreamAsync());
        var createdId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetString();
        createdId.Should().NotBeNullOrEmpty();

        // 2. Duplicate key creation -> Conflict (409)
        var dupRes = await client.PostAsJsonAsync(BaseUrl, createPayload);
        dupRes.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 3. Get Detail
        var getRes = await client.GetAsync($"{BaseUrl}/{createdId}");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getDoc = await JsonDocument.ParseAsync(await getRes.Content.ReadAsStreamAsync());
        getDoc.RootElement.GetProperty("data").GetProperty("name").GetString().Should().Be("Cursor IDE Test");

        // 4. Update
        var updatePayload = new
        {
            key = "custom-cursor-test",
            name = "Cursor IDE Test Renamed",
            description = "Updated description",
            headersJson = "{\"User-Agent\": \"cursor/0.41.0\"}",
            isEnabled = true,
            sortOrder = 60
        };
        var updateRes = await client.PutAsJsonAsync($"{BaseUrl}/{createdId}", updatePayload);
        updateRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Delete
        var deleteRes = await client.DeleteAsync($"{BaseUrl}/{createdId}");
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var verifyRes = await client.GetAsync($"{BaseUrl}/{createdId}");
        verifyRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_BuiltIn_Profile_Returns_BadRequest()
    {
        await using var factory = new HeaderProfilesWebApplicationFactory();
        using var client = factory.CreateClient();

        var listRes = await client.GetAsync(BaseUrl);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var profiles = await listRes.Content.ReadFromJsonAsync<List<HeaderProfileDto>>(jsonOptions);
        var builtIn = profiles!.First(p => p.IsBuiltIn);

        var deleteRes = await client.DeleteAsync($"{BaseUrl}/{builtIn.Id}");
        deleteRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_Evaluates_Dynamic_Placeholders()
    {
        await using var factory = new HeaderProfilesWebApplicationFactory();
        using var client = factory.CreateClient();

        var previewReq = new
        {
            headersJson = "{\"x-uuid\": \"${guid}\", \"x-nano\": \"${nanoid:12}\", \"x-mod\": \"${model}\"}",
            modelName = "claude-3-7-sonnet"
        };

        var res = await client.PostAsJsonAsync($"{BaseUrl}/preview", previewReq);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var headers = doc.RootElement.GetProperty("data").GetProperty("previewHeaders");
        var uuid = headers.GetProperty("x-uuid").GetString();
        var nano = headers.GetProperty("x-nano").GetString();
        var mod = headers.GetProperty("x-mod").GetString();

        Guid.TryParse(uuid, out _).Should().BeTrue();
        nano.Should().HaveLength(12);
        mod.Should().Be("claude-3-7-sonnet");
    }

    private sealed class HeaderProfileDto
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; }
        public bool IsEnabled { get; set; }
    }
}

internal sealed class HeaderProfilesWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"headerprofiles_{Guid.NewGuid():N}.db");

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
        Seed();
    }

    private void Seed()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_databasePath))
        {
            try { File.Delete(_databasePath); } catch { }
        }
    }
}
