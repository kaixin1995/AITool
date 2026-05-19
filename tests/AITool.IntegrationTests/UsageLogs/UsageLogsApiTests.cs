using System.Net;
using System.Text.Json;
using AITool.Domain.Models;
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

namespace AITool.IntegrationTests.UsageLogs;

/// <summary>
/// 使用日志集成测试，覆盖 Task 3 API 与 Task 4 页面基础文案
/// </summary>
public sealed class UsageLogsApiTests
{
    /// <summary>
    /// 验证日志列表接口会返回最新记录及其尝试相关字段。
    /// </summary>
    [Fact]
    public async Task Get_list_returns_latest_items_with_attempt_fields()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/list?rangeType=all");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();
        var latestItem = items[0];
        var fallbackAttemptItem = items.Single(x =>
            x.GetProperty("requestId").GetGuid() == UsageLogsWebApplicationFactory.RequestChainId &&
            x.GetProperty("attemptIndex").GetInt32() == 1);

        items.Should().HaveCount(4);
        latestItem.GetProperty("requestModel").GetString().Should().Be("summary-model");
        latestItem.GetProperty("cachedTokens").GetInt32().Should().Be(8704);
        latestItem.GetProperty("isStreaming").GetBoolean().Should().BeTrue();
        latestItem.GetProperty("firstTokenLatencyMs").GetInt32().Should().Be(5400);
        latestItem.GetProperty("totalDurationMs").GetInt32().Should().Be(8000);
        latestItem.GetProperty("streamDurationMs").GetInt32().Should().Be(2600);
        fallbackAttemptItem.GetProperty("requestModel").GetString().Should().Be("chat-prod");
        fallbackAttemptItem.GetProperty("attemptedModel").GetString().Should().Be("gpt-5.5");
        fallbackAttemptItem.GetProperty("siteModelName").GetString().Should().Be("gpt-5.5-a");
    }

    /// <summary>
    /// 验证日志列表接口支持按站点筛选记录。
    /// </summary>
    [Fact]
    public async Task Get_list_filters_by_site_id()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/usage-logs/list?rangeType=all&siteId={UsageLogsWebApplicationFactory.FirstSiteId}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items.Should().OnlyContain(x => x.GetProperty("siteName").GetString() == "Primary OpenAI");
    }

    /// <summary>
    /// 验证日志列表接口支持按状态筛选记录。
    /// </summary>
    [Fact]
    public async Task Get_list_filters_by_status()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/list?rangeType=all&status=fail");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items.Should().OnlyContain(x => x.GetProperty("status").GetString() == "fail");
    }

    /// <summary>
    /// 验证日志列表接口支持按模型关键字模糊搜索且忽略大小写。
    /// </summary>
    [Fact]
    public async Task Get_list_filters_by_model_keyword_case_insensitively()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/list?rangeType=all&modelKeyword=SuMmArY");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items.Should().OnlyContain(x =>
            x.GetProperty("requestModel").GetString()!.Contains("summary", StringComparison.OrdinalIgnoreCase)
            || x.GetProperty("attemptedModel").GetString()!.Contains("summary", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 验证请求详情接口会按请求标识聚合尝试记录，并按尝试序号排序。
    /// </summary>
    [Fact]
    public async Task Get_request_detail_groups_attempts_by_request_id_and_orders_by_attempt_index()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/usage-logs/request-detail/{UsageLogsWebApplicationFactory.RequestChainId}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("requestId").GetGuid().Should().Be(UsageLogsWebApplicationFactory.RequestChainId);
        document.RootElement.GetProperty("routeEntry").GetString().Should().Be("chat-prod");
        document.RootElement.GetProperty("protocolType").GetString().Should().Be("OpenAI");
        document.RootElement.GetProperty("forwardingMode").GetString().Should().Be("direct");
        document.RootElement.GetProperty("reasoningEffort").GetString().Should().BeEmpty();

        var attempts = document.RootElement.GetProperty("attempts").EnumerateArray().ToList();
        attempts.Should().HaveCount(2);
        document.RootElement.GetProperty("protocolType").GetString().Should().Be("OpenAI");
        attempts[0].GetProperty("attemptIndex").GetInt32().Should().Be(1);
        attempts[0].GetProperty("attemptedModel").GetString().Should().Be("gpt-5.5");
        attempts[0].GetProperty("forwardingMode").GetString().Should().Be("direct");
        attempts[0].GetProperty("siteModelName").GetString().Should().Be("gpt-5.5-a");
        attempts[0].GetProperty("siteName").GetString().Should().Be("Primary OpenAI");
        attempts[1].GetProperty("attemptIndex").GetInt32().Should().Be(2);
        attempts[1].GetProperty("attemptedModel").GetString().Should().Be("glm-5.1");
        attempts[1].GetProperty("forwardingMode").GetString().Should().Be("bridge");
        attempts[1].GetProperty("siteModelName").GetString().Should().Be("glm-5.1-a");
        attempts[1].GetProperty("siteName").GetString().Should().Be("Fallback GLM");
    }

    /// <summary>
    /// 验证汇总接口会基于最终结果日志统计请求级指标。
    /// </summary>
    [Fact]
    public async Task Get_summary_returns_request_level_metrics_from_final_result_logs()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/summary?rangeType=all");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(3);
        document.RootElement.GetProperty("failedRequests").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("successRate").GetDouble().Should().BeApproximately(66.67d, 0.01d);
        document.RootElement.GetProperty("totalTokens").GetInt32().Should().Be(8870);
        document.RootElement.GetProperty("maxDurationMs").GetInt32().Should().Be(8000);
    }

    /// <summary>
    /// 验证汇总接口支持按站点筛选统计结果。
    /// </summary>
    [Fact]
    public async Task Get_summary_filters_by_site_id()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/usage-logs/summary?rangeType=all&siteId={UsageLogsWebApplicationFactory.SecondSiteId}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("failedRequests").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("successRate").GetDouble().Should().BeApproximately(50d, 0.01d);
        document.RootElement.GetProperty("totalTokens").GetInt32().Should().Be(106);
        document.RootElement.GetProperty("maxDurationMs").GetInt32().Should().Be(3200);
    }

    /// <summary>
    /// 验证汇总接口支持按状态筛选统计结果。
    /// </summary>
    [Fact]
    public async Task Get_summary_filters_by_status()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/summary?rangeType=all&status=fail");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("failedRequests").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("successRate").GetDouble().Should().Be(0d);
        document.RootElement.GetProperty("totalTokens").GetInt32().Should().Be(99252);
        document.RootElement.GetProperty("maxDurationMs").GetInt32().Should().Be(8000);
    }

    /// <summary>
    /// 验证汇总接口支持按模型关键字模糊搜索且忽略大小写。
    /// </summary>
    [Fact]
    public async Task Get_summary_filters_by_model_keyword_case_insensitively()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/summary?rangeType=all&modelKeyword=SuMmArY");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("failedRequests").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("successRate").GetDouble().Should().BeApproximately(50d, 0.01d);
        document.RootElement.GetProperty("totalTokens").GetInt32().Should().Be(8764);
        document.RootElement.GetProperty("maxDurationMs").GetInt32().Should().Be(8000);
    }

    /// <summary>
    /// 验证使用日志页面会展示自动刷新、链路入口和汇总卡片文案。
    /// </summary>
    [Fact]
    public async Task Get_usage_logs_page_contains_filter_and_summary_ui_labels()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Admin/UsageLogs");

        html.Should().Contain("自动刷新");
        html.Should().Contain("自动刷新固定每 5 秒执行一次");
        html.Should().Contain("模型搜索");
        html.Should().Contain("首页");
        html.Should().Contain("末页");
        html.Should().Contain("跳转");
        html.Should().Contain("查看链路");
        html.Should().Contain("成功率");
        html.Should().Contain("总 Tokens");
        html.Should().Contain("用时/首字");
        html.Should().Contain("缓存");
        html.Should().Contain("状态筛选");
        html.Should().Contain("全部状态");
    }

    /// <summary>
    /// 验证模型健康页面会展示非最终结果里的失败尝试。
    /// </summary>
    [Fact]
    public async Task Get_model_health_page_includes_failed_attempt_from_fallback_chain()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Admin/ModelHealth?range=30d");

        html.Should().Contain("Primary OpenAI");
        html.Should().Contain("Fallback GLM");
        html.Should().Contain("· 失败 1 次");
        html.Should().Contain("· 成功 1 次");
        html.Should().Contain("health-status-bar-item fail");
    }
}

/// <summary>
/// 用于构建 UsageLogsWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class UsageLogsWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 带有两次尝试记录的请求标识。
    /// </summary>
    internal static readonly Guid RequestChainId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    /// <summary>
    /// 第一个测试站点的固定标识。
    /// </summary>
    internal static readonly Guid FirstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    /// <summary>
    /// 第二个测试站点的固定标识。
    /// </summary>
    internal static readonly Guid SecondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    /// <summary>
    /// 模型健康测试中监控模型的固定标识。
    /// </summary>
    private static readonly Guid MonitoredModelId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    /// <summary>
    /// 汇总统计中成功请求的固定标识。
    /// </summary>
    private static readonly Guid SummarySuccessRequestId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    /// <summary>
    /// 汇总统计中失败请求的固定标识。
    /// </summary>
    private static readonly Guid SummaryFailRequestId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-usage-logs-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 配置使用日志测试所需的数据库。
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
            new Site
            {
                Id = FirstSiteId,
                Name = "Primary OpenAI",
                BaseUrl = "https://primary.example.com",
                ApiKey = "site-key-1",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new Site
            {
                Id = SecondSiteId,
                Name = "Fallback GLM",
                BaseUrl = "https://fallback.example.com",
                ApiKey = "site-key-2",
                ProtocolType = "OpenAI",
                IsEnabled = true
            });

        db.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = MonitoredModelId,
            ModelName = "chat-prod",
            DisplayName = "主路由模型",
            IsEnabled = true
        });

        db.ModelHealthMonitors.Add(new ModelHealthMonitor
        {
            ModelLibraryItemId = MonitoredModelId
        });

        db.SiteModelMappings.AddRange(
            new SiteModelMapping
            {
                Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                SiteId = FirstSiteId,
                ModelLibraryItemId = MonitoredModelId,
                RemoteModelName = "gpt-5.5-a",
                LastStatus = "fail",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                SiteId = SecondSiteId,
                ModelLibraryItemId = MonitoredModelId,
                RemoteModelName = "glm-5.1-a",
                LastStatus = "success",
                IsEnabled = true
            });

        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                ExternalModelName = "chat-prod",
                UpstreamModelName = "gpt-5.5",
                SiteId = FirstSiteId,
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
                SiteId = SecondSiteId,
                SiteModelName = "glm-5.1-a",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 0,
                IsEnabled = true
            });

        db.ProxyUsageLogs.AddRange(
            new ProxyUsageLog
            {
                RequestId = RequestChainId,
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                ForwardingMode = "direct",
                RequestModel = "chat-prod",
                AttemptedModel = "gpt-5.5",
                TargetSiteId = FirstSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 2,
                AttemptIndex = 1,
                IsFinalResult = false,
                FallbackTriggered = true,
                ErrorMessage = "upstream timeout",
                InputTokens = 90548,
                CachedTokens = 8704,
                OutputTokens = 0,
                TotalTokens = 99252,
                IsStreaming = true,
                FirstTokenLatencyMs = 5400,
                StreamDurationMs = 2600,
                TotalDurationMs = 8000,
                RequestedAt = new DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero)
            },
            new ProxyUsageLog
            {
                RequestId = RequestChainId,
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                ForwardingMode = "bridge",
                RequestModel = "chat-prod",
                AttemptedModel = "glm-5.1",
                TargetSiteId = SecondSiteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 2,
                AttemptIndex = 2,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = string.Empty,
                InputTokens = 20,
                CachedTokens = 0,
                OutputTokens = 86,
                TotalTokens = 106,
                IsStreaming = true,
                FirstTokenLatencyMs = 1200,
                StreamDurationMs = 600,
                TotalDurationMs = 1800,
                RequestedAt = new DateTimeOffset(2026, 4, 28, 10, 0, 3, 200, TimeSpan.Zero)
            },
            new ProxyUsageLog
            {
                RequestId = SummarySuccessRequestId,
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "summary-model",
                AttemptedModel = "summary-model-upstream",
                TargetSiteId = FirstSiteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = string.Empty,
                InputTokens = 40,
                CachedTokens = 8704,
                OutputTokens = 20,
                TotalTokens = 8764,
                IsStreaming = true,
                FirstTokenLatencyMs = 5400,
                StreamDurationMs = 2600,
                TotalDurationMs = 8000,
                RequestedAt = new DateTimeOffset(2026, 4, 28, 10, 1, 0, TimeSpan.Zero)
            },
            new ProxyUsageLog
            {
                RequestId = SummaryFailRequestId,
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "summary-fail-model",
                AttemptedModel = "summary-fail-upstream",
                TargetSiteId = SecondSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = "rate limit",
                InputTokens = 0,
                CachedTokens = 0,
                OutputTokens = 0,
                TotalTokens = 0,
                IsStreaming = false,
                FirstTokenLatencyMs = 0,
                StreamDurationMs = 0,
                TotalDurationMs = 3200,
                RequestedAt = new DateTimeOffset(2026, 4, 28, 9, 59, 0, TimeSpan.Zero)
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 释放测试过程中创建的资源。
    /// </summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}

/// <summary>
/// 模型健康页面回归测试，验证同名模型的多站点日志不会相互串位。
/// </summary>
public sealed class ModelHealthPageTests
{
    /// <summary>
    /// 验证多个站点使用相同远程模型名时，每个站点只展示自己的错误和请求数量。
    /// </summary>
    [Fact]
    public async Task Get_model_health_page_keeps_logs_isolated_per_site_when_remote_model_names_match()
    {
        await using var factory = new ModelHealthRegressionWebApplicationFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/Admin/ModelHealth?range=30d");

        html.Should().Contain("Site A");
        html.Should().Contain("Site B");
        html.Should().Contain("Site C");
        CountOccurrences(html, "· 失败 1 次").Should().Be(1);
        CountOccurrences(html, "· 失败 2 次").Should().Be(1);
        CountOccurrences(html, "· 失败 3 次").Should().Be(1);
        CountOccurrences(html, "· 总请求 1 次").Should().Be(1);
        CountOccurrences(html, "· 总请求 2 次").Should().Be(1);
        CountOccurrences(html, "· 总请求 3 次").Should().Be(1);
    }

    /// <summary>
    /// 统计文本在页面中出现的次数，用于断言日志没有被重复分配到其他站点。
    /// </summary>
    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var startIndex = 0;
        while (true)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }
    }
}

/// <summary>
/// 用于构建 ModelHealthRegressionWebApplicationFactory 对应的测试宿主，并准备同名模型多站点场景数据。
/// </summary>
internal sealed class ModelHealthRegressionWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-model-health-regression-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 配置模型健康回归测试所需的数据库。
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

        var modelId = Guid.Parse("90909090-9090-9090-9090-909090909090");
        var siteAId = Guid.Parse("10101010-1010-1010-1010-101010101010");
        var siteBId = Guid.Parse("20202020-2020-2020-2020-202020202020");
        var siteCId = Guid.Parse("30303030-3030-3030-3030-303030303030");
        var timestamps = new[]
        {
            new DateTimeOffset(2026, 5, 17, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 17, 2, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 17, 3, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 17, 4, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 17, 5, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 17, 6, 0, 0, TimeSpan.Zero)
        };

        db.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = modelId,
            ModelName = "gpt-5.4",
            DisplayName = "GPT-5.4",
            IsEnabled = true
        });

        db.ModelHealthMonitors.Add(new ModelHealthMonitor
        {
            ModelLibraryItemId = modelId
        });

        db.Sites.AddRange(
            new Site
            {
                Id = siteAId,
                Name = "Site A",
                BaseUrl = "https://site-a.example.com",
                ApiKey = "site-a-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new Site
            {
                Id = siteBId,
                Name = "Site B",
                BaseUrl = "https://site-b.example.com",
                ApiKey = "site-b-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new Site
            {
                Id = siteCId,
                Name = "Site C",
                BaseUrl = "https://site-c.example.com",
                ApiKey = "site-c-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            });

        db.SiteModelMappings.AddRange(
            new SiteModelMapping
            {
                Id = Guid.Parse("40404040-4040-4040-4040-404040404040"),
                SiteId = siteAId,
                ModelLibraryItemId = modelId,
                RemoteModelName = "gpt-5.4",
                LastStatus = "fail",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                Id = Guid.Parse("50505050-5050-5050-5050-505050505050"),
                SiteId = siteBId,
                ModelLibraryItemId = modelId,
                RemoteModelName = "gpt-5.4",
                LastStatus = "fail",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                Id = Guid.Parse("60606060-6060-6060-6060-606060606060"),
                SiteId = siteCId,
                ModelLibraryItemId = modelId,
                RemoteModelName = "gpt-5.4",
                LastStatus = "fail",
                IsEnabled = true
            });

        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                Id = Guid.Parse("70707070-7070-7070-7070-707070707070"),
                ExternalModelName = "gpt-5.4",
                UpstreamModelName = "gpt-5.4",
                SiteId = siteAId,
                SiteModelName = "gpt-5.4",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("80808080-8080-8080-8080-808080808080"),
                ExternalModelName = "gpt-5.4",
                UpstreamModelName = "gpt-5.4",
                SiteId = siteBId,
                SiteModelName = "gpt-5.4",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("81818181-8181-8181-8181-818181818181"),
                ExternalModelName = "gpt-5.4",
                UpstreamModelName = "gpt-5.4",
                SiteId = siteCId,
                SiteModelName = "gpt-5.4",
                Priority = 2,
                ModelPriority = 2,
                InstancePriority = 0,
                IsEnabled = true
            });

        db.ProxyUsageLogs.AddRange(
            new ProxyUsageLog
            {
                Id = Guid.Parse("11110000-0000-0000-0000-000000000001"),
                RequestId = Guid.Parse("12120000-0000-0000-0000-000000000001"),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5.4",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteAId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                ErrorMessage = "site-a-timeout",
                TotalDurationMs = 1000,
                RequestedAt = timestamps[0]
            },
            new ProxyUsageLog
            {
                Id = Guid.Parse("11110000-0000-0000-0000-000000000002"),
                RequestId = Guid.Parse("12120000-0000-0000-0000-000000000002"),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5.4",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteBId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                ErrorMessage = "site-b-rate-limit",
                TotalDurationMs = 1100,
                RequestedAt = timestamps[1]
            },
            new ProxyUsageLog
            {
                Id = Guid.Parse("11110000-0000-0000-0000-000000000003"),
                RequestId = Guid.Parse("12120000-0000-0000-0000-000000000003"),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5.4",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteBId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                ErrorMessage = "site-b-rate-limit",
                TotalDurationMs = 1200,
                RequestedAt = timestamps[2]
            },
            new ProxyUsageLog
            {
                Id = Guid.Parse("11110000-0000-0000-0000-000000000004"),
                RequestId = Guid.Parse("12120000-0000-0000-0000-000000000004"),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5.4",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteCId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                ErrorMessage = "site-c-overloaded",
                TotalDurationMs = 1300,
                RequestedAt = timestamps[3]
            },
            new ProxyUsageLog
            {
                Id = Guid.Parse("11110000-0000-0000-0000-000000000005"),
                RequestId = Guid.Parse("12120000-0000-0000-0000-000000000005"),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5.4",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteCId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                ErrorMessage = "site-c-overloaded",
                TotalDurationMs = 1400,
                RequestedAt = timestamps[4]
            },
            new ProxyUsageLog
            {
                Id = Guid.Parse("11110000-0000-0000-0000-000000000006"),
                RequestId = Guid.Parse("12120000-0000-0000-0000-000000000006"),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5.4",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteCId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                ErrorMessage = "site-c-overloaded",
                TotalDurationMs = 1500,
                RequestedAt = timestamps[5]
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 释放测试过程中创建的资源。
    /// </summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
