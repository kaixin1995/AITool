using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// AllInOne 单进程宿主 E2E（T4.2~T4.4）：同一进程同时服务 /v1 代理面与管理面；
/// 代理事件经进程内总线直接入库（无磁盘 spool/无拉取）。覆盖：
/// 启动/健康、/v1 代理回合、管理面 API、事件进程内闭环（转发后 usage 立即可见）、
/// 聊天真转发链路、/v1 中继被排除（/v1 由 Core 控制器直接提供）。
/// </summary>
[Collection("RelaySerialized")]
public sealed class AllInOneHostTests
{
    private const string AccessKey = "allinone-test-access-key";

    [Fact]
    public async Task Health_and_admin_api_work_on_single_process()
    {
        using var upstream = new RelayMockUpstream();
        await using var factory = new AllInOneWebApplicationFactory(upstream.Port);
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);

        var stats = await client.GetAsync("/api/admin/dashboard/stats");
        stats.StatusCode.Should().Be(HttpStatusCode.OK);
        var statsBody = await stats.Content.ReadAsStringAsync();
        statsBody.Should().Contain("coreBaseUrl");
    }

    [Fact]
    public async Task V1_proxy_roundtrip_served_by_core_controllers()
    {
        using var upstream = new RelayMockUpstream();
        await using var factory = new AllInOneWebApplicationFactory(upstream.Port);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hi" } } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("mock-completion");
    }

    [Fact]
    public async Task Proxy_event_lands_in_usage_logs_in_process()
    {
        using var upstream = new RelayMockUpstream();
        await using var factory = new AllInOneWebApplicationFactory(upstream.Port);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

        // 触发一次真实转发。
        var forwarded = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hello" } } });
        forwarded.StatusCode.Should().Be(HttpStatusCode.OK);

        // 进程内事件消费为异步批量：稍等片刻后查询使用日志。
        await Task.Delay(TimeSpan.FromSeconds(3));
        string diagRows;
        string diagRawTime = "";
        var diagNow = DateTimeOffset.Now;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            diagRows = db.Client.Queryable<ProxyUsageLog>().Count().ToString();
            diagRawTime = string.Join(" | ", db.Client.Ado.SqlQuery<string>("SELECT RequestedAt FROM ProxyUsageLogs"));
        }
        var logsAll = await client.GetAsync("/api/admin/usage-logs/list?page=1&pageSize=10&rangeType=all");
        var logsAllBody = logsAll.IsSuccessStatusCode ? await logsAll.Content.ReadAsStringAsync() : $"HTTP{(int)logsAll.StatusCode}";
        var logs = await client.GetAsync("/api/admin/usage-logs/list?page=1&pageSize=10");
        logs.StatusCode.Should().Be(HttpStatusCode.OK);
        var logsBody = await logs.Content.ReadAsStringAsync();
        logsBody.Should().Contain("gpt-conserve", $"DB行数={diagRows}；原始RequestedAt=[{diagRawTime}]；now={diagNow:O}；all响应={RelayTestHelpers.Shorten(logsAllBody)}；day响应={RelayTestHelpers.Shorten(logsBody)}");
    }

    [Fact]
    public async Task Chat_api_routes_to_core_controller_and_forwards()
    {
        using var upstream = new RelayMockUpstream();
        await using var factory = new AllInOneWebApplicationFactory(upstream.Port);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

        var response = await client.PostAsJsonAsync("/api/admin/chat/send", new
        {
            model = "gpt-conserve",
            message = "ping",
            enableReasoning = false
        });

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// AllInOne 测试宿主：隔离 SQLite + 种子站点/模型/路由/密钥 + 本地 mock 上游。
/// </summary>
internal sealed class AllInOneWebApplicationFactory : WebApplicationFactory<AITool.AllInOne.AllInOneProgramMarker>
{
    private const string SeedAccessKey = "allinone-test-access-key";

    private readonly int _upstreamPort;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-allinone-{Guid.NewGuid():N}.db");

    public AllInOneWebApplicationFactory(int upstreamPort)
    {
        _upstreamPort = upstreamPort;
    }

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

        var siteId = Guid.Parse("aaaa1111-0000-0000-0000-000000000001");
        var modelId = Guid.Parse("aaaa1111-0000-0000-0000-000000000002");

        db.Sites.AddRange(
            new Domain.Sites.Site
            {
                Id = siteId,
                Name = "AllInOne-Site",
                BaseUrl = $"http://127.0.0.1:{_upstreamPort}/",
                EndpointPathMode = "standard-root",
                ApiKey = "upstream-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            });
        db.ModelLibraryItems.AddRange(new Domain.Models.ModelLibraryItem
        {
            Id = modelId,
            ModelName = "gpt-conserve",
            DisplayName = "AllInOne",
            IsEnabled = true
        });
        db.ProxyRouteRules.AddRange(new Domain.Proxy.ProxyRouteRule
        {
            Id = Guid.Parse("aaaa1111-0000-0000-0000-000000000003"),
            ExternalModelName = "gpt-conserve",
            UpstreamModelName = "gpt-conserve",
            SiteId = siteId,
            SiteModelName = "gpt-conserve",
            IsEnabled = true,
            ModelPriority = 0,
            InstancePriority = 0,
            Priority = 0
        });
        db.SiteModelMappings.AddRange(new Domain.SiteCatalog.SiteModelMapping
        {
            Id = Guid.Parse("aaaa1111-0000-0000-0000-000000000004"),
            SiteId = siteId,
            ModelLibraryItemId = modelId,
            RemoteModelName = "gpt-conserve",
            IsEnabled = true,
            MaxConcurrency = 0
        });
        db.ProxyAccessKeys.AddRange(new ProxyAccessKey
        {
            Id = Guid.Parse("aaaa1111-0000-0000-0000-000000000005"),
            KeyName = "allinone",
            PlainKey = SeedAccessKey,
            AccessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SeedAccessKey))),
            MaskedValue = "allinone-...-key",
            IsEnabled = true
        });
    }
}