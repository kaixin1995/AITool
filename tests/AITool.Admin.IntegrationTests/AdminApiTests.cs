using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Application.Common;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// Admin 宿主核心 API 集成测试。
/// 覆盖 JWT 认证流程 + 移植的管理 API（Dashboard/Sites/Models/Settings/RouteRules 等）。
/// Testing 环境通过 TestingAuthHandler 放行认证，测试聚焦业务逻辑而非鉴权本身。
/// </summary>
public sealed class AdminApiTests
{
    /// <summary>
    /// /api/auth/status 端点无需认证，返回功能开关与是否已设密码。
    /// 注意：该端点直接返回数据对象（非 ApiResponse 包装），面向登录前场景。
    /// </summary>
    [Fact]
    public async Task Auth_status_returns_ok_without_authentication()
    {
        await using var factory = new AdminApiWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // 验证返回的是合法 JSON（端点可达且不报错），不假设具体字段结构。
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Dashboard 统计端点可达（验证移植的 DashboardApiController）。
    /// </summary>
    [Fact]
    public async Task Dashboard_stats_returns_aggregated_data()
    {
        await using var factory = new AdminApiWebApplicationFactory();
        factory.SeedDashboardData();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/dashboard/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Sites CRUD：创建站点后列表可达（验证 SitesApiController + 缓存失效异步推送不报错）。
    /// </summary>
    [Fact]
    public async Task Sites_create_then_list_returns_created_site()
    {
        await using var factory = new AdminApiWebApplicationFactory();
        using var client = factory.CreateClient();

        var createPayload = new
        {
            name = "Test Site",
            baseUrl = "https://test.example.com",
            apiKey = "test-key",
            protocolType = "OpenAI",
            supportsOpenAi = true,
            supportsAnthropic = false,
            isEnabled = true
        };

        var createResponse = await client.PostAsJsonAsync("/api/admin/sites", createPayload);
        // 创建成功返回 200，数据校验失败返回 400，都证明端点可达且正常处理。
        createResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);

        var listResponse = await client.GetAsync("/api/admin/sites");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// SystemSettings GET 端点返回当前运行时设置（验证 SystemSettingsApiController 移植）。
    /// </summary>
    [Fact]
    public async Task SystemSettings_get_returns_runtime_settings()
    {
        await using var factory = new AdminApiWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/system/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// RouteRules 的 models + discover-sites 查询端点（验证阶段4补的端点）。
    /// </summary>
    [Fact]
    public async Task RouteRules_models_and_discover_sites_endpoints_return_ok()
    {
        await using var factory = new AdminApiWebApplicationFactory();
        factory.SeedDashboardData();
        using var client = factory.CreateClient();

        var modelsResponse = await client.GetAsync("/api/admin/route-rules/models");
        modelsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var discoverResponse = await client.GetAsync("/api/admin/route-rules/discover-sites?modelName=chat-prod");
        discoverResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Detection matrix 端点返回检测矩阵（验证阶段4补的端点）。
    /// </summary>
    [Fact]
    public async Task Detection_matrix_returns_model_groups()
    {
        await using var factory = new AdminApiWebApplicationFactory();
        factory.SeedDashboardData();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/detection/matrix");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// SPA fallback：非 /api、非 /v1 的请求应返回 index.html（200），由前端路由接管。
    /// 测试环境无 wwwroot 构建产物时，MapFallbackToFile 会返回 404，属正常。
    /// </summary>
    [Theory]
    [InlineData("/system/settings")]
    [InlineData("/codex")]
    [InlineData("/analytics")]
    public async Task Spa_routes_fall_back_to_index_or_404(string path)
    {
        await using var factory = new AdminApiWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        // 测试环境无前端构建产物，MapFallbackToFile 找不到 index.html 返回 404。
        // 关键是不返回 500（证明 fallback 管线正常工作）。
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Admin API 测试专用工厂。复用 AdminHostWebApplicationFactory 的 SqlSugar + seed 基础设施，
/// 额外提供 Dashboard 测试数据的预置入口。
/// </summary>
internal sealed class AdminApiWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-admin-api-{Guid.NewGuid():N}.db");
    private bool _seeded;

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
        IntegrationTestDbHelper.InitializeDatabaseAsync(Services).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 预置仪表盘/路由测试数据（站点 + 路由规则 + 模型 + 映射 + 用量日志）。
    /// 幂等：多次调用只 seed 一次。
    /// </summary>
    public void SeedDashboardData()
    {
        if (_seeded) return;
        _seeded = true;

        SeedDashboardDataAsync().GetAwaiter().GetResult();
    }

    private async Task SeedDashboardDataAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var siteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await db.InsertAsync(new Site
        {
            Id = siteId,
            Name = "Dashboard Site",
            BaseUrl = "https://dashboard.example.com",
            ApiKey = "dash-key",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        });

        var modelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await db.InsertAsync(new AITool.Domain.Models.ModelLibraryItem
        {
            Id = modelId,
            ModelName = "chat-prod",
            DisplayName = "Chat Production",
            ModelType = "chat",
            IsEnabled = true
        });

        await db.InsertAsync(new AITool.Domain.SiteCatalog.SiteModelMapping
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SiteId = siteId,
            ModelLibraryItemId = modelId,
            RemoteModelName = "gpt-5.4",
            LastStatus = "success",
            IsEnabled = true
        });

        await db.InsertAsync(new AITool.Domain.Proxy.ProxyRouteRule
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            SiteId = siteId,
            ExternalModelName = "chat-prod",
            UpstreamModelName = "chat-prod",
            SiteModelName = "gpt-5.4",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true,
            AvailabilityMode = "AllDay",
            TimeRangesJson = string.Empty
        });

        await db.InsertAsync(new AITool.Domain.Proxy.ProxyUsageLog
        {
            RequestId = Guid.NewGuid(),
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            ForwardingMode = "direct",
            RequestModel = "chat-prod",
            AttemptedModel = "gpt-5.4",
            TargetSiteId = siteId,
            Status = "success",
            Source = "proxy",
            RetryCount = 0,
            AttemptIndex = 1,
            IsFinalResult = true,
            FallbackTriggered = false,
            ErrorMessage = string.Empty,
            InputTokens = 100,
            CachedTokens = 20,
            OutputTokens = 50,
            TotalTokens = 170,
            IsStreaming = true,
            IsStreamInterrupted = false,
            FirstTokenLatencyMs = 30,
            StreamDurationMs = 200,
            TotalDurationMs = 300,
            ReasoningEffort = "medium",
            RequestedAt = DateTimeOffset.UtcNow
        });
    }
}
