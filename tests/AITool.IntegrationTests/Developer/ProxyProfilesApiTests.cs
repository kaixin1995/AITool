using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.Developer;

public sealed class ProxyProfilesApiTests
{
    private const string BaseUrl = "/api/admin/developer/proxy-profiles";

    [Fact]
    public async Task Create_And_Get_And_Update_And_Delete_Proxy_Lifecycle()
    {
        await using var factory = new ProxyProfilesWebApplicationFactory();
        using var client = factory.CreateClient();

        // 1. Create
        var createPayload = new
        {
            key = "clash-test-node",
            name = "本地 Clash 节点测试",
            proxyUrl = "http://127.0.0.1:7890",
            description = "测试用代理",
            isEnabled = true,
            sortOrder = 10
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
        getDoc.RootElement.GetProperty("data").GetProperty("name").GetString().Should().Be("本地 Clash 节点测试");
        getDoc.RootElement.GetProperty("data").GetProperty("proxyUrl").GetString().Should().Be("http://127.0.0.1:7890");

        // 4. Update
        var updatePayload = new
        {
            key = "clash-test-node",
            name = "本地 Clash 节点 (更新后)",
            proxyUrl = "socks5://127.0.0.1:10808",
            description = "更新描述",
            isEnabled = false,
            sortOrder = 20
        };
        var updateRes = await client.PutAsJsonAsync($"{BaseUrl}/{createdId}", updatePayload);
        updateRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Verify Updated
        var getUpdatedRes = await client.GetAsync($"{BaseUrl}/{createdId}");
        var updatedDoc = await JsonDocument.ParseAsync(await getUpdatedRes.Content.ReadAsStreamAsync());
        updatedDoc.RootElement.GetProperty("data").GetProperty("proxyUrl").GetString().Should().Be("socks5://127.0.0.1:10808");
        updatedDoc.RootElement.GetProperty("data").GetProperty("isEnabled").GetBoolean().Should().BeFalse();

        // 6. Delete
        var deleteRes = await client.DeleteAsync($"{BaseUrl}/{createdId}");
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var verifyRes = await client.GetAsync($"{BaseUrl}/{createdId}");
        verifyRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_Invalid_Proxy_Url_Returns_BadRequest()
    {
        await using var factory = new ProxyProfilesWebApplicationFactory();
        using var client = factory.CreateClient();

        var invalidPayload = new
        {
            key = "invalid-proxy",
            name = "Invalid Proxy",
            proxyUrl = "ftp://127.0.0.1:21",
            isEnabled = true
        };

        var res = await client.PostAsJsonAsync(BaseUrl, invalidPayload);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TestConnectivity_Invalid_Url_Returns_BadRequest()
    {
        await using var factory = new ProxyProfilesWebApplicationFactory();
        using var client = factory.CreateClient();

        var invalidTest = new
        {
            proxyUrl = "invalid-url-without-scheme"
        };

        var res = await client.PostAsJsonAsync($"{BaseUrl}/test", invalidTest);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

file sealed class ProxyProfilesWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"aitool_proxy_test_{Guid.NewGuid():N}.db";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, _dbName);
            var connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";

            // Remove existing AppDbContext / ISqlSugarClient registrations
            var descriptors = services.Where(d =>
                d.ServiceType == typeof(AppDbContext) ||
                d.ServiceType.Name.Contains("SqlSugar")
            ).ToList();

            foreach (var d in descriptors)
            {
                services.Remove(d);
            }

            services.AddSingleton(new SqlSugar.ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = SqlSugar.DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = SqlSugar.InitKeyType.Attribute
            });
            services.AddSingleton<SqlSugar.ISqlSugarClient>(sp =>
            {
                var cfg = sp.GetRequiredService<SqlSugar.ConnectionConfig>();
                return new SqlSugar.SqlSugarClient(cfg);
            });
            services.AddSingleton<SemaphoreSlim>(_ => new SemaphoreSlim(1, 1));
            services.AddScoped<AppDbContext>();
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        // 网络代理池默认关闭：本组测试聚焦 API 行为本身，需显式开启。
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            DeveloperFeaturesEnabled = true,
            DeveloperProxyProfilesEnabled = true
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        var dbPath = Path.Combine(AppContext.BaseDirectory, _dbName);
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { }
        }
    }
}
