using System.Net;
using System.Reflection;
using System.Text.Json;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Analytics;

/// <summary>
/// 可视化页面集成测试，验证页面入口和聚合接口都可正常工作。
/// </summary>
public sealed class AnalyticsPageTests
{
    /// <summary>
    /// 验证统计页面会展示图表区域和筛选条件。
    /// </summary>
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

    /// <summary>
    /// 验证页面时间范围筛选已移除“全部”入口及相关提示。
    /// </summary>
    [Fact]
    public async Task Get_analytics_page_hides_all_range_entry_and_notice()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Analytics");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("<option value=\"day\">按天</option>");
        html.Should().Contain("<option value=\"week\" selected>按周</option>");
        html.Should().Contain("<option value=\"month\">按月</option>");
        html.Should().Contain("<option value=\"custom\">指定时间范围</option>");
        html.Should().NotContain("<option value=\"all\">全部</option>");
        html.Should().NotContain("loadAllBtn");
        html.Should().NotContain("全部范围");
    }

    /// <summary>
    /// 验证按周范围会从本周周一开始，到今天结束后的右开区间边界。
    /// </summary>
    [Fact]
    public void Resolve_time_range_uses_current_week_start_and_end_of_today()
    {
        var now = DateTimeOffset.Now;
        var expectedStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset)
            .AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var expectedEnd = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(1);

        var (startTime, endTime) = ResolveTimeRangeCore("week");

        startTime.Should().Be(expectedStart);
        endTime.Should().Be(expectedEnd);
    }

    /// <summary>
    /// 验证按月范围会从本月 1 号开始，到今天结束后的右开区间边界。
    /// </summary>
    [Fact]
    public void Resolve_time_range_uses_current_month_start_and_end_of_today()
    {
        var now = DateTimeOffset.Now;
        var expectedStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);
        var expectedEnd = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(1);

        var (startTime, endTime) = ResolveTimeRangeCore("month");

        startTime.Should().Be(expectedStart);
        endTime.Should().Be(expectedEnd);
    }

    /// <summary>
    /// 验证按月统计在自动粒度下，首个趋势桶不会回退到上个月。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_month_range_does_not_start_from_previous_month()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var body = await GetDashboardBodyAsync(client, "/api/admin/analytics/dashboard?rangeType=month&bucketType=auto");

        using var document = JsonDocument.Parse(body);
        var firstLabel = document.RootElement.GetProperty("requestTrend")[0].GetProperty("label").GetString();

        firstLabel.Should().StartWith($"{DateTimeOffset.Now:MM}-01");
    }

    /// <summary>
    /// 验证按月统计在按周聚合时，会展示实际日期范围而不是生硬的“某日 周”。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_month_range_uses_week_bucket_date_range_labels()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var body = await GetDashboardBodyAsync(client, "/api/admin/analytics/dashboard?rangeType=month&bucketType=auto");

        using var document = JsonDocument.Parse(body);
        var firstLabel = document.RootElement.GetProperty("requestTrend")[0].GetProperty("label").GetString();

        firstLabel.Should().Be($"{DateTimeOffset.Now:MM}-01 ~ {DateTimeOffset.Now:MM}-07");
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
        root.GetProperty("summary").GetProperty("totalInputTokens").GetInt32().Should().Be(35);
        root.GetProperty("summary").GetProperty("totalCachedTokens").GetInt32().Should().Be(3);
        root.GetProperty("summary").GetProperty("totalOutputTokens").GetInt32().Should().Be(35);
        root.GetProperty("summary").GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);
        root.GetProperty("siteDistribution").GetArrayLength().Should().Be(2);
        root.GetProperty("modelDistribution").GetArrayLength().Should().Be(2);

        var glmPoint = root.GetProperty("modelDistribution")
            .EnumerateArray()
            .First(x => x.GetProperty("label").GetString() == "glm-5.1");
        glmPoint.GetProperty("totalTokens").GetInt32().Should().Be(30);
        glmPoint.GetProperty("inputTokens").GetInt32().Should().Be(12);
        glmPoint.GetProperty("cachedTokens").GetInt32().Should().Be(3);
        glmPoint.GetProperty("outputTokens").GetInt32().Should().Be(15);

        var glmCachePoint = root.GetProperty("modelCacheRatioDistribution")
            .EnumerateArray()
            .First(x => x.GetProperty("label").GetString() == "glm-5.1");
        glmCachePoint.GetProperty("inputTokens").GetInt32().Should().Be(12);
        glmCachePoint.GetProperty("cachedTokens").GetInt32().Should().Be(3);
        glmCachePoint.GetProperty("totalInputScope").GetInt32().Should().Be(15);
        glmCachePoint.GetProperty("cacheHitRate").GetDouble().Should().Be(20);
    }

    /// <summary>
    /// 验证按站点筛选时会基于尝试记录而不是最终结果统计数据。
    /// </summary>
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

    /// <summary>
    /// 验证按第二个站点筛选时，只返回该站点相关的统计结果。
    /// </summary>
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

    /// <summary>
    /// 验证模型筛选项来自模型库，模型分布统计基于实际尝试的模型。
    /// </summary>
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

    /// <summary>
    /// 验证统计接口支持按自定义时间范围和模型条件组合筛选。
    /// </summary>
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

    /// <summary>
    /// 验证自定义时间范围会包含结束时间所在分钟内的数据。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_custom_range_includes_selected_end_minute()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var target = factory.StreamSuccessRequestedAt;
        var minuteStart = new DateTimeOffset(target.Year, target.Month, target.Day, target.Hour, target.Minute, 0, target.Offset);
        var startTime = Uri.EscapeDataString(minuteStart.ToString("O"));
        var endTime = Uri.EscapeDataString(minuteStart.ToString("O"));

        var body = await GetDashboardBodyAsync(
            client,
            $"/api/admin/analytics/dashboard?rangeType=custom&bucketType=hour&startTime={startTime}&endTime={endTime}&protocolType=OpenAI&modelName=glm-5.1&siteId={AnalyticsWebApplicationFactory.SecondSiteId}");

        using var document = JsonDocument.Parse(body);
        var summary = document.RootElement.GetProperty("summary");
        summary.GetProperty("totalRequests").GetInt32().Should().Be(1);
        summary.GetProperty("totalTokens").GetInt32().Should().Be(30);
    }

    /// <summary>
    /// 验证自定义时间范围覆盖整天时，自动粒度会退化为按小时，避免趋势折线只剩单点。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_custom_full_day_uses_hour_bucket_for_auto_mode()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var target = factory.StreamSuccessRequestedAt;
        var dayStart = new DateTimeOffset(target.Year, target.Month, target.Day, 0, 0, 0, target.Offset);
        var dayEnd = dayStart.AddHours(23).AddMinutes(59);
        var startTime = Uri.EscapeDataString(dayStart.ToString("O"));
        var endTime = Uri.EscapeDataString(dayEnd.ToString("O"));

        var body = await GetDashboardBodyAsync(
            client,
            $"/api/admin/analytics/dashboard?rangeType=custom&bucketType=auto&startTime={startTime}&endTime={endTime}&protocolType=OpenAI&modelName=glm-5.1&siteId={AnalyticsWebApplicationFactory.SecondSiteId}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("appliedFilter").GetProperty("bucketType").GetString().Should().Be("hour");
        root.GetProperty("requestTrend").GetArrayLength().Should().BeGreaterThan(1);
        root.GetProperty("requestTrend")
            .EnumerateArray()
            .Sum(x => x.GetProperty("requestCount").GetInt32())
            .Should().Be(1);
        root.GetProperty("tokenTrend")
            .EnumerateArray()
            .Sum(x => x.GetProperty("totalTokens").GetInt32())
            .Should().Be(30);
        root.GetProperty("durationTrend")
            .EnumerateArray()
            .Max(x => x.GetProperty("averageTotalDurationMs").GetDouble())
            .Should().BeGreaterThan(0);
    }

    /// <summary>
    /// 验证图表中站点或模型标签缺失时会统一显示为 -。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_uses_dash_for_missing_site_or_model_labels()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ProxyUsageLogs.Add(new AITool.Domain.Proxy.ProxyUsageLog
            {
                RequestId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                AccessKeyId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                ProtocolType = "OpenAI",
                RequestModel = "chat-prod",
                AttemptedModel = "",
                TargetSiteId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                Status = "success",
                Source = "proxy",
                RetryCount = 0,
                AttemptIndex = 1,
                IsFinalResult = true,
                InputTokens = 5,
                OutputTokens = 5,
                TotalTokens = 10,
                TotalDurationMs = 120,
                RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            });
            await db.SaveChangesAsync();
        }

        var body = await GetDashboardBodyAsync(client, "/api/admin/analytics/dashboard?rangeType=all&bucketType=day");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("siteDistribution")
            .EnumerateArray()
            .Select(x => x.GetProperty("label").GetString())
            .Should().Contain("-");
        root.GetProperty("modelDistribution")
            .EnumerateArray()
            .Select(x => x.GetProperty("label").GetString())
            .Should().Contain("-");
    }

    /// <summary>
    /// 通过反射调用私有的时间范围解析逻辑，避免仅为测试扩大生产代码可见性。
    /// </summary>
    private static (DateTimeOffset StartTime, DateTimeOffset EndTime) ResolveTimeRangeCore(string rangeType)
    {
        var method = typeof(AITool.Web.Controllers.Admin.AnalyticsApiController)
            .GetMethod("ResolveTimeRange", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        return ((DateTimeOffset StartTime, DateTimeOffset EndTime))method!
            .Invoke(null, new object?[] { rangeType, null, null })!;
    }

    /// <summary>
    /// 后台统计允许先返回 pending，因此测试按短轮询等待最终结果。
    /// </summary>
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

/// <summary>
/// 用于构建 AnalyticsWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class AnalyticsWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 第一个测试站点的固定标识。
    /// </summary>
    internal static readonly Guid FirstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    /// <summary>
    /// 第二个测试站点的固定标识。
    /// </summary>
    internal static readonly Guid SecondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    /// <summary>
    /// 记录流式成功请求的时间，供自定义时间范围测试复用。
    /// </summary>
    internal DateTimeOffset StreamSuccessRequestedAt { get; private set; }
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-analytics-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 配置统计页面测试所需的数据库。
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
