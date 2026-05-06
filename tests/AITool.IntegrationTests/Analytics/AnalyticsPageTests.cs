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
        html.Should().Contain("/api/admin/analytics/dashboard");
    }

    [Fact]
    public async Task Get_dashboard_returns_expected_summary_and_distributions()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all&bucketType=day");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);

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
}

internal sealed class AnalyticsWebApplicationFactory : WebApplicationFactory<Program>
{
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

        var firstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        db.Sites.AddRange(
            new AITool.Domain.Sites.Site
            {
                Id = firstSiteId,
                Name = "Alpha",
                BaseUrl = "https://alpha.example.com",
                ApiKey = "alpha-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new AITool.Domain.Sites.Site
            {
                Id = secondSiteId,
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
                ModelType = "chat",
                IsEnabled = true
            },
            new AITool.Domain.Models.ModelLibraryItem
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                ModelName = "reason-prod",
                DisplayName = "Reason Prod",
                ModelType = "chat",
                IsEnabled = true
            });

        var firstRequestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var secondRequestId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        db.ProxyUsageLogs.AddRange(
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                RequestId = firstRequestId,
                AccessKeyId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                ProtocolType = "OpenAI",
                RequestModel = "chat-prod",
                AttemptedModel = "gpt-5.5",
                TargetSiteId = firstSiteId,
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
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-1)
            },
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                RequestId = firstRequestId,
                AccessKeyId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                ProtocolType = "OpenAI",
                RequestModel = "chat-prod",
                AttemptedModel = "glm-5.1",
                TargetSiteId = secondSiteId,
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
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(1)
            },
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                RequestId = secondRequestId,
                AccessKeyId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                ProtocolType = "OpenAI",
                RequestModel = "reason-prod",
                AttemptedModel = "deepseek-r1",
                TargetSiteId = firstSiteId,
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
                RequestedAt = DateTimeOffset.UtcNow.AddHours(-4)
            });

        await db.SaveChangesAsync();
    }
}
