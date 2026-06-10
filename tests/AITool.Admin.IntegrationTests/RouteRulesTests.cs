using System.Net;
using System.Text;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 路由规则管理集成测试，验证路由主入口和规则条目的增删改查。
/// <para>
/// 此测试从 AITool.IntegrationTests.Proxy.ProxyFallbackFlowTests 迁移至此，
/// 因为路由规则管理页面和 API 已从 Web 宿主迁移到 Admin 宿主。
/// 只迁移纯 Admin 操作的测试用例，不涉及代理转发的测试仍保留在原处。
/// </para>
/// </summary>
public sealed class RouteRulesTests
{
    /// <summary>
    /// 验证主路由入口列表会返回入口名称和候选数量。
    /// </summary>
    [Fact]
    public async Task Get_entries_returns_master_entry_names_with_candidate_counts()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/route-rules/entries");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"entryName\":\"chat-prod\"");
        body.Should().Contain("\"candidateCount\":2");
    }

    /// <summary>
    /// 验证新建空入口后，入口列表中能够立即看到该记录。
    /// </summary>
    [Fact]
    public async Task Post_entries_creates_empty_master_entry_visible_in_entry_list()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var createResponse = await client.PostAsync(
            "/api/admin/route-rules/entries",
            new StringContent("{\"entryName\":\"auto\"}", Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await client.GetAsync("/api/admin/route-rules/entries");
        var listBody = await listResponse.Content.ReadAsStringAsync();

        listBody.Should().Contain("\"entryName\":\"auto\"");
        listBody.Should().Contain("\"candidateCount\":0");
    }

    /// <summary>
    /// 验证删除入口时会一并移除该入口下的全部路由规则。
    /// </summary>
    [Fact]
    public async Task Delete_entry_removes_all_rules_for_that_master_entry()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var deleteResponse = await client.PostAsync(
            "/api/admin/route-rules/entries/delete",
            new StringContent("{\"entryName\":\"chat-prod\"}", Encoding.UTF8, "application/json"));

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.ProxyRouteRules.CountAsync(x => x.ExternalModelName == "chat-prod");

        remaining.Should().Be(0);
    }

    /// <summary>
    /// 验证保存路由时支持为同一入口配置多组上游模型。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_accepts_multiple_upstream_model_groups()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"},{\"upstreamModelName\":\"glm-5.1\",\"siteId\":\"22222222-2222-2222-2222-222222222222\",\"siteModelName\":\"glm-5.1-a\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rules = await db.ProxyRouteRules
            .Where(x => x.ExternalModelName == "chat-prod")
            .OrderBy(x => x.Priority)
            .ToListAsync();

        rules.Should().HaveCount(2);
        rules[0].UpstreamModelName.Should().Be("gpt-5.5");
        rules[0].ModelPriority.Should().Be(0);
        rules[0].InstancePriority.Should().Be(0);
        rules[1].UpstreamModelName.Should().Be("glm-5.1");
        rules[1].ModelPriority.Should().Be(1);
        rules[1].InstancePriority.Should().Be(0);
    }

    /// <summary>
    /// 验证未传时间配置的候选规则会按全天可用保存，兼容旧页面和旧调用。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_defaults_missing_availability_to_all_day()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rule = await db.ProxyRouteRules.SingleAsync(x => x.ExternalModelName == "chat-prod");

        rule.AvailabilityMode.Should().Be("AllDay");
        rule.TimeRangesJson.Should().BeEmpty();
    }

    /// <summary>
    /// 验证时间配置保存后能从列表接口以小写字段重新读回，保证页面刷新后仍可解析。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_persists_availability_time_range_for_reload()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\",\"availabilityMode\":\"Unavailable\",\"timeRangesJson\":\"[{\\\"start\\\":\\\"14:00\\\",\\\"end\\\":\\\"18:59\\\"}]\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rule = await db.ProxyRouteRules.SingleAsync(x => x.ExternalModelName == "chat-prod");

        rule.AvailabilityMode.Should().Be("Unavailable");
        rule.TimeRangesJson.Should().Contain("\"start\":\"14:00\"");
        rule.TimeRangesJson.Should().Contain("\"end\":\"18:59\"");

        var listResponse = await client.GetAsync("/api/admin/route-rules/list?modelName=chat-prod");
        var listBody = await listResponse.Content.ReadAsStringAsync();

        listBody.Should().Contain("\"availabilityMode\":\"Unavailable\"");
        listBody.Should().Contain("\\\"start\\\":\\\"14:00\\\"");
        listBody.Should().Contain("\\\"end\\\":\\\"18:59\\\"");
    }

    /// <summary>
    /// 验证规则列表在首次读取后，再次保存路由仍会立即返回最新顺序。
    /// </summary>
    [Fact]
    public async Task Get_route_rule_list_refreshes_immediately_after_save()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var warmupResponse = await client.GetAsync("/api/admin/route-rules/list?modelName=chat-prod");
        var warmupBody = await warmupResponse.Content.ReadAsStringAsync();
        warmupResponse.StatusCode.Should().Be(HttpStatusCode.OK, warmupBody);
        warmupBody.IndexOf("gpt-5.5-a", StringComparison.Ordinal).Should().BeLessThan(warmupBody.IndexOf("glm-5.1-a", StringComparison.Ordinal));

        var saveResponse = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"glm-5.1\",\"siteId\":\"22222222-2222-2222-2222-222222222222\",\"siteModelName\":\"glm-5.1-a\"},{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"}]}",
                Encoding.UTF8,
                "application/json"));
        saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var refreshedResponse = await client.GetAsync("/api/admin/route-rules/list?modelName=chat-prod");
        var refreshedBody = await refreshedResponse.Content.ReadAsStringAsync();
        refreshedResponse.StatusCode.Should().Be(HttpStatusCode.OK, refreshedBody);
        refreshedBody.IndexOf("glm-5.1-a", StringComparison.Ordinal).Should().BeLessThan(refreshedBody.IndexOf("gpt-5.5-a", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证路由页面包含搜索框，并且不会直接渲染调试用协议表达式。
    /// </summary>
    [Fact]
    public async Task Get_routes_page_contains_search_box_and_hides_protocol_rendering_text()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Routes");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("搜索站点或模型");
        html.Should().NotContain("item.protocolType");
    }

    /// <summary>
    /// 验证路由规则页面会串行保存拖拽结果，避免快速拖动时旧顺序覆盖新顺序。
    /// </summary>
    [Fact]
    public async Task Get_routes_page_serializes_queue_save_requests()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Routes");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("var _pendingRouteSave = null;");
        html.Should().Contain("var _routeSaveInFlight = false;");
        html.Should().Contain("function flushRouteSaveQueue()");
        html.Should().Contain("if (_routeSaveInFlight || !_pendingRouteSave)");
        html.Should().Contain("var saveRequest = _pendingRouteSave;");
        html.Should().Contain("function syncEntryCandidateCount(entryName, candidateCount)");
    }

    /// <summary>
    /// 验证同一个站点可以在同一入口中配置多条不同的候选规则。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_allows_same_site_to_appear_multiple_times()
    {
        await using var factory = new RouteRulesWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"},{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-b\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rules = await db.ProxyRouteRules
            .Where(x => x.ExternalModelName == "chat-prod")
            .OrderBy(x => x.Priority)
            .ToListAsync();

        rules.Should().HaveCount(2);
        rules[0].SiteId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        rules[1].SiteId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        rules[0].SiteModelName.Should().Be("gpt-5.5-a");
        rules[1].SiteModelName.Should().Be("gpt-5.5-b");
    }
}

/// <summary>
/// 用于构建 RouteRulesTests 对应的 Admin 测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class RouteRulesWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-route-rules-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 重写测试宿主依赖，接入隔离数据库。
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
    /// 在客户端配置完成后执行测试数据初始化。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备当前测试场景所需的数据，与原始 ProxyFallbackFlowTests 种子数据保持一致。
    /// </summary>
    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var firstSite = new Site
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Primary OpenAI",
            BaseUrl = "https://invalid-primary.example.com",
            ApiKey = "upstream-key-1",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };
        var secondSite = new Site
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Fallback GLM",
            BaseUrl = "https://invalid-fallback.example.com",
            ApiKey = "upstream-key-2",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };
        var thirdSite = new Site
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Name = "Primary OpenAI Replica",
            BaseUrl = "https://invalid-replica.example.com",
            ApiKey = "upstream-key-3",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };

        var routeRules = new[]
        {
            new ProxyRouteRule
            {
                ExternalModelName = "chat-prod",
                UpstreamModelName = "gpt-5.5",
                SiteId = firstSite.Id,
                SiteModelName = "gpt-5.5-a",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                ExternalModelName = "chat-prod",
                UpstreamModelName = "glm-5.1",
                SiteId = secondSite.Id,
                SiteModelName = "glm-5.1-a",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 0,
                IsEnabled = true
            }
        };

        db.Sites.AddRange(firstSite, secondSite, thirdSite);
        db.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = "chat-prod"
        });
        db.SiteModelMappings.AddRange(
            new SiteModelMapping
            {
                SiteId = firstSite.Id,
                ModelLibraryItemId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                RemoteModelName = "gpt-5.5-a",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = thirdSite.Id,
                ModelLibraryItemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                RemoteModelName = "gpt-5.5-b",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = secondSite.Id,
                ModelLibraryItemId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                RemoteModelName = "glm-5.1-a",
                LastStatus = "ok",
                IsEnabled = true
            });
        db.ProxyRouteRules.AddRange(routeRules);
        db.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 9,
            ProxyRetryCount = 2,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true
        });
        await db.SaveChangesAsync();
    }
}
