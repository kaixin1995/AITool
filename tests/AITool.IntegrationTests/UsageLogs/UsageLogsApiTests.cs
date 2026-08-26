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

        items.Should().HaveCount(5);
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

        items.Should().HaveCount(3);
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
    /// 验证日志列表接口支持按流式/非流式筛选。
    /// </summary>
    [Theory]
    [InlineData(true, 4)]
    [InlineData(false, 1)]
    public async Task Get_list_filters_by_is_streaming(bool isStreaming, int expectedCount)
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/usage-logs/list?rangeType=all&isStreaming={isStreaming.ToString().ToLowerInvariant()}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(expectedCount);
        items.Should().OnlyContain(x => x.GetProperty("isStreaming").GetBoolean() == isStreaming);
    }

    /// <summary>
    /// 验证日志摘要接口支持按流式/非流式筛选。
    /// </summary>
    [Theory]
    [InlineData(true, 4)]
    [InlineData(false, 1)]
    public async Task Get_summary_filters_by_is_streaming(bool isStreaming, int expectedTotalRequests)
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/usage-logs/summary?rangeType=all&isStreaming={isStreaming.ToString().ToLowerInvariant()}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(expectedTotalRequests);
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
    /// 验证汇总接口会基于日志条数统计成功、失败和 Token 指标。
    /// </summary>
    [Fact]
    public async Task Get_summary_returns_attempt_level_metrics_from_usage_logs()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/summary?rangeType=all");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(5);
        document.RootElement.GetProperty("failedRequests").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("successRate").GetDouble().Should().Be(60d);
        document.RootElement.GetProperty("totalTokens").GetInt32().Should().Be(108164);
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
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
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
        SqlSugarSetup.InitializeDatabase(db.Client);

        var logBaseTime = DateTimeOffset.UtcNow.AddDays(-1);

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
                RequestedAt = logBaseTime.AddMinutes(0)
            },
            new ProxyUsageLog
            {
                RequestId = Guid.Parse("b1b1b1b1-b1b1-b1b1-b1b1-b1b1b1b1b1b1"),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                ForwardingMode = "direct",
                RequestModel = "chat-prod",
                AttemptedModel = "gpt-5.5",
                TargetSiteId = FirstSiteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = string.Empty,
                InputTokens = 12,
                CachedTokens = 0,
                OutputTokens = 30,
                TotalTokens = 42,
                IsStreaming = true,
                FirstTokenLatencyMs = 800,
                StreamDurationMs = 400,
                TotalDurationMs = 1200,
                RequestedAt = logBaseTime.AddMinutes(0)
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
                RequestedAt = logBaseTime.AddMinutes(0).AddSeconds(3).AddMilliseconds(200)
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
                RequestedAt = logBaseTime.AddMinutes(1)
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
                RequestedAt = logBaseTime.AddMinutes(-1)
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
