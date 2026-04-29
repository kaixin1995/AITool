using System.Net;
using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.UsageLogs;

// 使用日志集成测试，覆盖 Task 3 API 与 Task 4 页面基础文案
public sealed class UsageLogsApiTests
{
    [Fact]
    public async Task Get_list_returns_latest_items_with_attempt_fields()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/list");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.EnumerateArray().ToList();
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

    [Fact]
    public async Task Get_list_filters_by_site_id()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/usage-logs/list?siteId={UsageLogsWebApplicationFactory.FirstSiteId}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items.Should().OnlyContain(x => x.GetProperty("siteName").GetString() == "Primary OpenAI");
    }

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

        var attempts = document.RootElement.GetProperty("attempts").EnumerateArray().ToList();
        attempts.Should().HaveCount(2);
        attempts[0].GetProperty("attemptIndex").GetInt32().Should().Be(1);
        attempts[0].GetProperty("attemptedModel").GetString().Should().Be("gpt-5.5");
        attempts[0].GetProperty("siteModelName").GetString().Should().Be("gpt-5.5-a");
        attempts[0].GetProperty("siteName").GetString().Should().Be("Primary OpenAI");
        attempts[1].GetProperty("attemptIndex").GetInt32().Should().Be(2);
        attempts[1].GetProperty("attemptedModel").GetString().Should().Be("glm-5.1");
        attempts[1].GetProperty("siteModelName").GetString().Should().Be("glm-5.1-a");
        attempts[1].GetProperty("siteName").GetString().Should().Be("Fallback GLM");
    }

    [Fact]
    public async Task Get_summary_returns_request_level_metrics_from_final_result_logs()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/usage-logs/summary");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(3);
        document.RootElement.GetProperty("failedRequests").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("successRate").GetDouble().Should().BeApproximately(66.67d, 0.01d);
        document.RootElement.GetProperty("totalTokens").GetInt32().Should().Be(8870);
        document.RootElement.GetProperty("maxDurationMs").GetInt32().Should().Be(3200);
    }

    [Fact]
    public async Task Get_summary_filters_by_site_id()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/usage-logs/summary?siteId={UsageLogsWebApplicationFactory.SecondSiteId}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("totalRequests").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("failedRequests").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("successRate").GetDouble().Should().BeApproximately(50d, 0.01d);
        document.RootElement.GetProperty("totalTokens").GetInt32().Should().Be(106);
        document.RootElement.GetProperty("maxDurationMs").GetInt32().Should().Be(3200);
    }

    [Fact]
    public async Task Get_usage_logs_page_contains_task4_ui_labels()
    {
        await using var factory = new UsageLogsWebApplicationFactory();
        using var client = factory.CreateClient();

        // 验证页面包含自动刷新、链路入口和汇总卡片关键文案
        var html = await client.GetStringAsync("/Admin/UsageLogs");

        html.Should().Contain("自动刷新");
        html.Should().Contain("查看链路");
        html.Should().Contain("成功率");
        html.Should().Contain("总Token数");
        html.Should().Contain("总耗时");
        html.Should().Contain("用时/首字");
        html.Should().Contain("缓存");
    }
}

internal sealed class UsageLogsWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid RequestChainId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid FirstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SecondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SummarySuccessRequestId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SummaryFailRequestId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-usage-logs-{Guid.NewGuid():N}.db");

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

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

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

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
