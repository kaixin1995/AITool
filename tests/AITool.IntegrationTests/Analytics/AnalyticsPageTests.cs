using System.Net;
using System.Text.Json;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Analytics;

// 可视化页面集成测试，验证页面入口和聚合接口都可正常工作。
public sealed class AnalyticsPageTests
{
    [Fact]
    public async Task Get_analytics_page_contains_chart_sections_and_filters()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Analytics");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("可视化分析");
        html.Should().Contain("requestTrendChart");
        html.Should().Contain("tokenTrendChart");
        html.Should().Contain("时间范围");
        html.Should().Contain("调用模型");
        html.Should().Contain("modelNameInput");
        html.Should().Contain("modelNameOptions");
        html.Should().Contain("/api/admin/analytics/dashboard");
    }

    [Fact]
    public async Task Get_dashboard_returns_expected_summary_and_distributions()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var body = await GetDashboardBodyAsync(client, "/api/admin/analytics/dashboard?rangeType=all&bucketType=day");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("summary").GetProperty("totalRequests").GetInt32().Should().Be(2);
        root.GetProperty("summary").GetProperty("successRequests").GetInt32().Should().Be(1);
        root.GetProperty("summary").GetProperty("failedRequests").GetInt32().Should().Be(1);
        root.GetProperty("summary").GetProperty("totalTokens").GetInt32().Should().Be(70);
        root.GetProperty("summary").GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);
        root.GetProperty("siteDistribution").GetArrayLength().Should().Be(2);
        root.GetProperty("modelDistribution").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Get_dashboard_filters_site_by_attempt_scope_instead_of_final_result_scope()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var body = await GetDashboardBodyAsync(client, $"/api/admin/analytics/dashboard?rangeType=all&bucketType=day&siteId={AnalyticsWebApplicationFactory.FirstSiteId}");

        using var document = JsonDocument.Parse(body);
        var summary = document.RootElement.GetProperty("summary");
        summary.GetProperty("totalRequests").GetInt32().Should().Be(2);
        summary.GetProperty("successRequests").GetInt32().Should().Be(0);
        summary.GetProperty("failedRequests").GetInt32().Should().Be(2);
        summary.GetProperty("totalTokens").GetInt32().Should().Be(50);
        summary.GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);

        var siteDistribution = document.RootElement.GetProperty("siteDistribution").EnumerateArray().ToList();
        siteDistribution.Should().HaveCount(1);
        siteDistribution[0].GetProperty("label").GetString().Should().Be("Alpha");
    }

    [Fact]
    public async Task Get_dashboard_filters_site_by_attempt_scope_for_second_site()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var body = await GetDashboardBodyAsync(client, $"/api/admin/analytics/dashboard?rangeType=all&bucketType=day&siteId={AnalyticsWebApplicationFactory.SecondSiteId}");

        using var document = JsonDocument.Parse(body);
        var summary = document.RootElement.GetProperty("summary");
        summary.GetProperty("totalRequests").GetInt32().Should().Be(1);
        summary.GetProperty("successRequests").GetInt32().Should().Be(1);
        summary.GetProperty("failedRequests").GetInt32().Should().Be(0);
        summary.GetProperty("totalTokens").GetInt32().Should().Be(30);

        var modelDistribution = document.RootElement.GetProperty("modelDistribution").EnumerateArray().ToList();
        modelDistribution.Should().HaveCount(1);
        modelDistribution[0].GetProperty("label").GetString().Should().Be("glm-5.1");
    }

    [Fact]
    public async Task Get_dashboard_uses_model_library_options_and_attempted_model_distribution()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var optionsResponse = await client.GetAsync("/api/admin/analytics/options");
        var optionsBody = await optionsResponse.Content.ReadAsStringAsync();
        optionsResponse.StatusCode.Should().Be(HttpStatusCode.OK, optionsBody);

        using var optionsDocument = JsonDocument.Parse(optionsBody);
        var models = optionsDocument.RootElement.GetProperty("models").EnumerateArray().Select(x => x.GetProperty("modelName").GetString()).ToList();
        models.Should().Contain("chat-prod");
        models.Should().Contain("reason-prod");
        models.Should().NotContain("glm-5.1");
        models.Should().NotContain("deepseek-r1");

        var body = await GetDashboardBodyAsync(client, "/api/admin/analytics/dashboard?rangeType=all&bucketType=day&modelName=glm-5.1");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("summary").GetProperty("totalRequests").GetInt32().Should().Be(1);
        root.GetProperty("modelDistribution")[0].GetProperty("label").GetString().Should().Be("glm-5.1");
    }

    [Fact]
    public async Task Get_dashboard_supports_custom_range_with_model_filter()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var targetDay = factory.StreamSuccessRequestedAt;
        var startTime = Uri.EscapeDataString(targetDay.ToString("O"));
        var endTime = Uri.EscapeDataString(targetDay.ToString("O"));
        var body = await GetDashboardBodyAsync(
            client,
            $"/api/admin/analytics/dashboard?rangeType=custom&bucketType=day&startTime={startTime}&endTime={endTime}&protocolType=OpenAI&modelName=glm-5.1&siteId={AnalyticsWebApplicationFactory.SecondSiteId}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var summary = root.GetProperty("summary");
        summary.GetProperty("totalRequests").GetInt32().Should().Be(1);
        summary.GetProperty("successRequests").GetInt32().Should().Be(1);
        summary.GetProperty("failedRequests").GetInt32().Should().Be(0);
        summary.GetProperty("totalTokens").GetInt32().Should().Be(30);
        summary.GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);

        var appliedFilter = root.GetProperty("appliedFilter");
        appliedFilter.GetProperty("rangeType").GetString().Should().Be("custom");

        root.GetProperty("siteDistribution").EnumerateArray().Should().ContainSingle();
        root.GetProperty("requestTrend")
            .EnumerateArray()
            .Sum(x => x.GetProperty("requestCount").GetInt32())
            .Should().Be(1);
        root.GetProperty("tokenTrend")
            .EnumerateArray()
            .Sum(x => x.GetProperty("totalTokens").GetInt32())
            .Should().Be(30);
    }

    // 后台统计允许先返回 pending，因此测试按短轮询等待最终结果。
    private static async Task<string> GetDashboardBodyAsync(HttpClient client, string url)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return body;
            }

            response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException("分析看板接口在预期时间内未返回最终结果");
    }
}

internal sealed class AnalyticsWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid FirstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SecondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal DateTimeOffset StreamSuccessRequestedAt { get; private set; }
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-analytics-{Guid.NewGuid():N}.db");

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
            new AITool.Domain.Sites.Site
            {
                Id = FirstSiteId,
                Name = "Alpha",
                BaseUrl = "https://alpha.example.com",
                ApiKey = "alpha-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new AITool.Domain.Sites.Site
            {
                Id = SecondSiteId,
                Name = "Beta",
                BaseUrl = "https://beta.example.com",
                ApiKey = "beta-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            });

        db.ModelLibraryItems.AddRange(
            new AITool.Domain.Models.ModelLibraryItem
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ModelName = "chat-prod",
                DisplayName = "Chat Prod",
                IsEnabled = true
            },
            new AITool.Domain.Models.ModelLibraryItem
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                ModelName = "reason-prod",
                DisplayName = "Reason Prod",
                IsEnabled = true
            });

        var firstRequestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var secondRequestId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var streamSuccessRequestedAt = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(1);
        StreamSuccessRequestedAt = streamSuccessRequestedAt;
        var fallbackFailureRequestedAt = streamSuccessRequestedAt.AddMinutes(-1);
        var singleFailureRequestedAt = DateTimeOffset.UtcNow.AddHours(-4);

        db.ProxyUsageLogs.AddRange(
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                RequestId = firstRequestId,
                AccessKeyId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                ProtocolType = "OpenAI",
                RequestModel = "chat-prod",
                AttemptedModel = "gpt-5.5",
                TargetSiteId = FirstSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                FallbackTriggered = true,
                ErrorMessage = "timeout",
                InputTokens = 10,
                OutputTokens = 0,
                TotalTokens = 10,
                TotalDurationMs = 400,
                FirstTokenLatencyMs = 0,
                RequestedAt = fallbackFailureRequestedAt
            },
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                RequestId = firstRequestId,
                AccessKeyId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                ProtocolType = "OpenAI",
                RequestModel = "chat-prod",
                AttemptedModel = "glm-5.1",
                TargetSiteId = SecondSiteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 2,
                IsFinalResult = true,
                InputTokens = 12,
                CachedTokens = 3,
                OutputTokens = 15,
                TotalTokens = 30,
                IsStreaming = true,
                FirstTokenLatencyMs = 120,
                StreamDurationMs = 380,
                TotalDurationMs = 500,
                RequestedAt = streamSuccessRequestedAt
            },
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                RequestId = secondRequestId,
                AccessKeyId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                ProtocolType = "OpenAI",
                RequestModel = "reason-prod",
                AttemptedModel = "deepseek-r1",
                TargetSiteId = FirstSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                ErrorMessage = "upstream 500",
                InputTokens = 20,
                OutputTokens = 20,
                TotalTokens = 40,
                TotalDurationMs = 900,
                FirstTokenLatencyMs = 300,
                RequestedAt = singleFailureRequestedAt
            });

        await db.SaveChangesAsync();
    }
}
