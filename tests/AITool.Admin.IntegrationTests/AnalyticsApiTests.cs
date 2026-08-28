using System.Net;
using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Admin.IntegrationTests.Analytics;

/// <summary>
/// Analytics 看板接口集成测试，覆盖请求级归并和最终记录筛选口径。
/// </summary>
public sealed class AnalyticsApiTests
{
    /// <summary>
    /// 验证重试链只计为一个请求，并按最终记录统计结果，同时保留回退链路。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_deduplicates_request_chain_and_counts_final_results()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var summary = root.GetProperty("summary");

        summary.GetProperty("totalRequests").GetInt32().Should().Be(2);
        summary.GetProperty("successRequests").GetInt32().Should().Be(1);
        summary.GetProperty("failedRequests").GetInt32().Should().Be(1);
        summary.GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);
        root.GetProperty("requestTrend").EnumerateArray().Sum(x => x.GetProperty("requestCount").GetInt32()).Should().Be(2);
        root.GetProperty("siteDistribution").EnumerateArray().Should().HaveCount(2);
        root.GetProperty("modelDistribution").EnumerateArray().Select(x => x.GetProperty("label").GetString()).Should()
            .Contain("final-model")
            .And.Contain("failed-model");
        root.GetProperty("fallbackTrend").EnumerateArray().Sum(x => x.GetProperty("fallbackCount").GetInt32()).Should().Be(1);
    }

    /// <summary>
    /// 验证来源、访问密钥和协议细分均按最终请求聚合，并对 fallback 请求去重。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_returns_final_request_breakdowns_without_exposing_access_keys()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var source = root.GetProperty("sourceBreakdown").EnumerateArray().Single();
        source.GetProperty("key").GetString().Should().Be("proxy");
        source.GetProperty("label").GetString().Should().Be("代理");
        source.GetProperty("requestCount").GetInt32().Should().Be(2);
        source.GetProperty("successCount").GetInt32().Should().Be(1);
        source.GetProperty("failedCount").GetInt32().Should().Be(1);
        source.GetProperty("successRate").GetDouble().Should().Be(50d);
        source.GetProperty("totalTokens").GetInt64().Should().Be(50);
        source.GetProperty("averageTotalDurationMs").GetDouble().Should().Be(250d);
        source.GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);

        var accessKeys = root.GetProperty("accessKeyBreakdown").EnumerateArray().ToList();
        accessKeys.Should().HaveCount(2);
        var accessKeyIds = accessKeys.Select(x => x.GetProperty("key").GetString()).ToList();
        accessKeyIds.Should().Contain(AnalyticsWebApplicationFactory.FinalAccessKeyId.ToString());
        accessKeyIds.Should().Contain(AnalyticsWebApplicationFactory.SingleFailureAccessKeyId.ToString());
        var accessKeyLabels = accessKeys.Select(x => x.GetProperty("label").GetString()).ToList();
        accessKeyLabels.Should().Contain("Final Client");
        accessKeyLabels.Should().Contain("Failure Client");
        body.Should().NotContain("final-secret");
        body.Should().NotContain("failure-secret");

        var protocols = root.GetProperty("protocolBreakdown").EnumerateArray().ToList();
        protocols.Should().HaveCount(1);
        protocols[0].GetProperty("key").GetString().Should().Be("OpenAI");
        protocols[0].GetProperty("label").GetString().Should().Be("OpenAI");
        protocols[0].GetProperty("requestCount").GetInt32().Should().Be(2);
        protocols[0].GetProperty("successCount").GetInt32().Should().Be(1);
        protocols[0].GetProperty("failedCount").GetInt32().Should().Be(1);
        protocols[0].GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// 验证失败分类和状态码分布只统计最终失败请求，并且响应不包含错误正文。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_returns_failure_categories_and_status_codes_without_error_body()
    {
        await using var factory = new AnalyticsWebApplicationFactory(includeTask6Fixtures: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var categories = root.GetProperty("failureReasonBreakdown").EnumerateArray().ToList();
        var categoryKeys = categories.Select(x => x.GetProperty("key").GetString()).ToList();
        categoryKeys.Should().Contain("authentication");
        categoryKeys.Should().Contain("rate-limit");
        categoryKeys.Should().Contain("upstream-error");
        categoryKeys.Should().Contain("timeout");
        categoryKeys.Should().Contain("stream-interrupted");
        categoryKeys.Should().Contain("other");
        categories.Should().HaveCount(6);
        categories.Should().OnlyContain(x => x.GetProperty("requestCount").GetInt32() == 1);

        var statuses = root.GetProperty("statusCodeBreakdown").EnumerateArray().ToList();
        var statusKeys = statuses.Select(x => x.GetProperty("key").GetString()).ToList();
        statusKeys.Should().Contain("401");
        statusKeys.Should().Contain("429");
        statusKeys.Should().Contain("502");
        statusKeys.Should().Contain("400");
        statusKeys.Should().Contain("500");
        statusKeys.Should().Contain("no-response");
        statuses.Should().HaveCount(6);
        statuses.Should().OnlyContain(x => x.GetProperty("requestCount").GetInt32() == 1);

        body.Should().NotContain("auth-error-body");
        body.Should().NotContain("rate-error-body");
        body.Should().NotContain("upstream-error-body");
        body.Should().NotContain("timeout-error-body");
        body.Should().NotContain("stream-error-body");
        body.Should().NotContain("unknown-error-body");
    }

    /// <summary>
    /// 验证看板优先使用已经持久化的错误分类，不因重新解析错误摘要而改变分类。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_prefers_persisted_error_category()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ProxyUsageLogs.Add(new ProxyUsageLog
            {
                RequestId = Guid.NewGuid(),
                AccessKeyId = AnalyticsWebApplicationFactory.SingleFailureAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = "persisted-category-model",
                AttemptedModel = "persisted-category-model",
                TargetSiteId = AnalyticsWebApplicationFactory.FirstSiteId,
                Status = "fail",
                Source = "proxy",
                AttemptIndex = 1,
                IsFinalResult = true,
                ErrorMessage = "timeout body",
                ErrorCategory = "upstream-error",
                HttpStatusCode = 400,
                RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all&modelName=persisted-category-model");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var categories = document.RootElement.GetProperty("failureReasonBreakdown").EnumerateArray().ToList();
        categories.Should().ContainSingle();
        categories[0].GetProperty("key").GetString().Should().Be("upstream-error");
    }

    /// <summary>
    /// 验证回退链路按 RequestId 去重，使用首末站点信息，并限制最多返回 20 条。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_returns_deduplicated_fallback_chains_with_top_twenty_limit()
    {
        await using var factory = new AnalyticsWebApplicationFactory(includeFallbackChainLimitFixtures: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var chains = root.GetProperty("fallbackChainDistribution").EnumerateArray().ToList();

        chains.Should().HaveCount(20);
        chains.Should().Contain(x =>
            x.GetProperty("firstSiteLabel").GetString() == "-"
            && x.GetProperty("finalSiteLabel").GetString() == "-");
        var originalChain = chains.Single(x =>
            x.GetProperty("firstSiteKey").GetString() == AnalyticsWebApplicationFactory.FirstSiteId.ToString());
        originalChain.GetProperty("firstSiteLabel").GetString().Should().Be("Primary Site");
        originalChain.GetProperty("finalSiteKey").GetString().Should().Be(AnalyticsWebApplicationFactory.SecondSiteId.ToString());
        originalChain.GetProperty("finalSiteLabel").GetString().Should().Be("Fallback Site");
        originalChain.GetProperty("requestCount").GetInt32().Should().Be(1);
        originalChain.GetProperty("successCount").GetInt32().Should().Be(1);
        originalChain.GetProperty("successRate").GetDouble().Should().Be(100d);
        originalChain.GetProperty("averageAttemptCount").GetDouble().Should().Be(2d);
    }

    /// <summary>
    /// 验证延迟百分位数只基于筛选后的最终请求，并返回固定样本的分位数和样本数。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_returns_latency_percentiles_for_filtered_final_requests()
    {
        await using var factory = new AnalyticsWebApplicationFactory(includePercentileFixtures: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all&modelName=percentile-model");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var percentiles = root.GetProperty("latencyPercentiles");
        var totalDuration = percentiles.GetProperty("totalDuration");
        totalDuration.GetProperty("p50").GetDouble().Should().Be(20d);
        totalDuration.GetProperty("p95").GetDouble().Should().Be(40d);
        totalDuration.GetProperty("p99").GetDouble().Should().Be(40d);
        totalDuration.GetProperty("sampleCount").GetInt32().Should().Be(4);

        var firstTokenLatency = percentiles.GetProperty("firstTokenLatency");
        firstTokenLatency.GetProperty("p50").GetDouble().Should().Be(2d);
        firstTokenLatency.GetProperty("p95").GetDouble().Should().Be(4d);
        firstTokenLatency.GetProperty("p99").GetDouble().Should().Be(4d);
        firstTokenLatency.GetProperty("sampleCount").GetInt32().Should().Be(4);
    }

    /// <summary>
    /// 验证没有匹配请求时延迟百分位数返回零值而不是抛出异常。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_returns_empty_latency_percentiles_without_matching_requests()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all&modelName=missing-percentile-model");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var percentiles = document.RootElement.GetProperty("latencyPercentiles");
        percentiles.GetProperty("totalDuration").GetProperty("p50").GetDouble().Should().Be(0d);
        percentiles.GetProperty("totalDuration").GetProperty("sampleCount").GetInt32().Should().Be(0);
        percentiles.GetProperty("firstTokenLatency").GetProperty("p99").GetDouble().Should().Be(0d);
        percentiles.GetProperty("firstTokenLatency").GetProperty("sampleCount").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// 验证显式来源大小写不同于查询参数时，筛选仍能命中历史日志。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_matches_source_filter_case_insensitively_for_historical_logs()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ProxyUsageLogs.Add(new ProxyUsageLog
            {
                RequestId = Guid.NewGuid(),
                AccessKeyId = AnalyticsWebApplicationFactory.FinalAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = "mixed-case-source-model",
                AttemptedModel = "mixed-case-source-model",
                TargetSiteId = AnalyticsWebApplicationFactory.SecondSiteId,
                Status = "success",
                Source = "Proxy",
                IsFinalResult = true,
                RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            "/api/admin/analytics/dashboard?rangeType=all&modelName=mixed-case-source-model&source=proxy");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("summary").GetProperty("totalRequests").GetInt32().Should().Be(1);
        root.GetProperty("sourceBreakdown").EnumerateArray().Single().GetProperty("key").GetString()
            .Should().Be("proxy");
    }

    /// <summary>
    /// 验证站点、模型、访问密钥和协议筛选均应用于请求最终记录，而不是中间失败尝试。
    /// </summary>
    [Fact]
    public async Task Get_dashboard_applies_filters_to_final_record_not_intermediate_attempt()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var finalRecordResponse = await client.GetAsync(
            $"/api/admin/analytics/dashboard?rangeType=all&protocolType=OpenAI&modelName=final-model&source=proxy&siteId={AnalyticsWebApplicationFactory.SecondSiteId}&accessKeyId={AnalyticsWebApplicationFactory.FinalAccessKeyId}");
        var finalRecordBody = await finalRecordResponse.Content.ReadAsStringAsync();

        finalRecordResponse.StatusCode.Should().Be(HttpStatusCode.OK, finalRecordBody);

        using (var finalRecordDocument = JsonDocument.Parse(finalRecordBody))
        {
            var finalSummary = finalRecordDocument.RootElement.GetProperty("summary");
            finalSummary.GetProperty("totalRequests").GetInt32().Should().Be(1);
            finalSummary.GetProperty("successRequests").GetInt32().Should().Be(1);
            finalSummary.GetProperty("failedRequests").GetInt32().Should().Be(0);
            finalSummary.GetProperty("fallbackRequestCount").GetInt32().Should().Be(1);
            finalRecordDocument.RootElement.GetProperty("appliedFilter").GetProperty("source").GetString().Should().Be("proxy");
        }

        var intermediateResponse = await client.GetAsync(
            $"/api/admin/analytics/dashboard?rangeType=all&protocolType=Anthropic&modelName=first-attempt-model&source=proxy&siteId={AnalyticsWebApplicationFactory.FirstSiteId}&accessKeyId={AnalyticsWebApplicationFactory.InitialAccessKeyId}");
        var intermediateBody = await intermediateResponse.Content.ReadAsStringAsync();

        intermediateResponse.StatusCode.Should().Be(HttpStatusCode.OK, intermediateBody);

        using var intermediateDocument = JsonDocument.Parse(intermediateBody);
        var intermediateSummary = intermediateDocument.RootElement.GetProperty("summary");
        intermediateSummary.GetProperty("totalRequests").GetInt32().Should().Be(0);
        intermediateSummary.GetProperty("successRequests").GetInt32().Should().Be(0);
        intermediateSummary.GetProperty("failedRequests").GetInt32().Should().Be(0);
        intermediateDocument.RootElement.GetProperty("requestTrend").EnumerateArray().Sum(x => x.GetProperty("requestCount").GetInt32()).Should().Be(0);
    }
}

/// <summary>
/// 为 Analytics 集成测试提供隔离数据库和两条请求场景。
/// </summary>
internal sealed class AnalyticsWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    internal static readonly Guid RequestChainId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid SingleFailureRequestId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid FirstSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SecondSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid InitialAccessKeyId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid FinalAccessKeyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly Guid SingleFailureAccessKeyId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly bool _includeTask6Fixtures;
    private readonly bool _includeFallbackChainLimitFixtures;
    private readonly bool _includePercentileFixtures;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-analytics-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 创建 Analytics 测试宿主，可按测试场景追加 Task 6 数据。
    /// </summary>
    public AnalyticsWebApplicationFactory(
        bool includeTask6Fixtures = false,
        bool includeFallbackChainLimitFixtures = false,
        bool includePercentileFixtures = false)
    {
        _includeTask6Fixtures = includeTask6Fixtures;
        _includeFallbackChainLimitFixtures = includeFallbackChainLimitFixtures;
        _includePercentileFixtures = includePercentileFixtures;
    }

    /// <summary>
    /// 配置测试环境和隔离的 SqlSugar 数据库。
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
    /// 创建客户端后写入当前测试所需的请求日志。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备一条跨站点回退请求和一条单次失败请求。
    /// </summary>
    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        db.Sites.AddRange(
            new Site
            {
                Id = FirstSiteId,
                Name = "Primary Site",
                BaseUrl = "https://primary.example.com",
                ApiKey = "primary-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new Site
            {
                Id = SecondSiteId,
                Name = "Fallback Site",
                BaseUrl = "https://fallback.example.com",
                ApiKey = "fallback-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            });

        db.ProxyAccessKeys.AddRange(
            new ProxyAccessKey
            {
                Id = FinalAccessKeyId,
                KeyName = "Final Client",
                PlainKey = "final-secret",
                AccessKeyHash = "final-hash",
                MaskedValue = "sk-***inal",
                IsEnabled = true
            },
            new ProxyAccessKey
            {
                Id = SingleFailureAccessKeyId,
                KeyName = "Failure Client",
                PlainKey = "failure-secret",
                AccessKeyHash = "failure-hash",
                MaskedValue = "sk-***ure",
                IsEnabled = true
            },
            new ProxyAccessKey
            {
                Id = InitialAccessKeyId,
                KeyName = "Initial Client",
                PlainKey = "initial-secret",
                AccessKeyHash = "initial-hash",
                MaskedValue = "sk-***ial",
                IsEnabled = true
            });

        var requestTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        db.ProxyUsageLogs.AddRange(
            new ProxyUsageLog
            {
                RequestId = RequestChainId,
                AccessKeyId = InitialAccessKeyId,
                ProtocolType = "Anthropic",
                RequestModel = "client-model",
                AttemptedModel = "first-attempt-model",
                TargetSiteId = FirstSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 2,
                AttemptIndex = 1,
                IsFinalResult = false,
                FallbackTriggered = true,
                ErrorMessage = "upstream timeout",
                InputTokens = 10,
                TotalTokens = 10,
                TotalDurationMs = 100,
                RequestedAt = requestTime
            },
            new ProxyUsageLog
            {
                RequestId = RequestChainId,
                AccessKeyId = FinalAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = "client-model",
                AttemptedModel = "final-model",
                TargetSiteId = SecondSiteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 2,
                AttemptIndex = 2,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = string.Empty,
                InputTokens = 20,
                OutputTokens = 30,
                TotalTokens = 50,
                TotalDurationMs = 200,
                RequestedAt = requestTime.AddSeconds(1)
            },
            new ProxyUsageLog
            {
                RequestId = SingleFailureRequestId,
                AccessKeyId = SingleFailureAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = "failed-request-model",
                AttemptedModel = "failed-model",
                TargetSiteId = FirstSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = false,
                FallbackTriggered = false,
                ErrorMessage = "rate limit",
                TotalDurationMs = 300,
                RequestedAt = requestTime.AddSeconds(2)
            });

        if (_includeTask6Fixtures)
        {
            // 删除基础场景的单次失败，避免干扰 Task 6 六种分类的精确断言。
            db.Client.Deleteable<ProxyUsageLog>()
                .Where(x => x.RequestId == SingleFailureRequestId)
                .ExecuteCommand();
            AddTask6FailureFixtures(db, requestTime);
        }

        if (_includeFallbackChainLimitFixtures)
        {
            AddFallbackChainLimitFixtures(db, requestTime);
        }

        if (_includePercentileFixtures)
        {
            AddPercentileFixtures(db, requestTime);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 写入六种最终失败分类测试数据，错误正文仅用于验证不会被返回。
    /// </summary>
    private static void AddTask6FailureFixtures(AppDbContext db, DateTimeOffset requestTime)
    {
        var fixtures = new[]
        {
            (401, "auth-error-body", false),
            (429, "rate-error-body", false),
            (502, "upstream-error-body", false),
            (0, "timeout-error-body deadline exceeded", false),
            (500, "stream-error-body", true),
            (400, "unknown-error-body", false)
        };
        var logs = new List<ProxyUsageLog>();

        for (var index = 0; index < fixtures.Length; index++)
        {
            var (statusCode, errorMessage, interrupted) = fixtures[index];
            logs.Add(new ProxyUsageLog
            {
                RequestId = Guid.NewGuid(),
                AccessKeyId = SingleFailureAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = $"failure-{index}",
                AttemptedModel = $"failure-{index}",
                TargetSiteId = FirstSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = errorMessage,
                HttpStatusCode = statusCode == 0 ? 0 : statusCode,
                IsStreamInterrupted = interrupted,
                TotalDurationMs = 100 + index,
                RequestedAt = requestTime.AddMinutes(index + 1)
            });
        }

        db.ProxyUsageLogs.AddRange(logs);
    }

    /// <summary>
    /// 写入 20 条额外的不同首末站点链路，验证回退链路 Top 20 限制。
    /// </summary>
    private static void AddFallbackChainLimitFixtures(AppDbContext db, DateTimeOffset requestTime)
    {
        var logs = new List<ProxyUsageLog>();
        for (var index = 0; index < 20; index++)
        {
            var requestId = Guid.NewGuid();
            var firstSiteId = Guid.NewGuid();
            var finalSiteId = Guid.NewGuid();
            if (index > 0)
            {
                db.Sites.AddRange(
                    new Site
                    {
                        Id = firstSiteId,
                        Name = $"ZZ First {index:00}",
                        BaseUrl = $"https://first-{index}.example.com",
                        ApiKey = $"first-key-{index}",
                        ProtocolType = "OpenAI",
                        IsEnabled = true
                    },
                    new Site
                    {
                        Id = finalSiteId,
                        Name = $"ZZ Final {index:00}",
                        BaseUrl = $"https://final-{index}.example.com",
                        ApiKey = $"final-key-{index}",
                        ProtocolType = "OpenAI",
                        IsEnabled = true
                    });
            }

            logs.Add(new ProxyUsageLog
            {
                RequestId = requestId,
                AccessKeyId = FinalAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = $"chain-{index}",
                AttemptedModel = $"chain-first-{index}",
                TargetSiteId = firstSiteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 2,
                AttemptIndex = 1,
                IsFinalResult = false,
                FallbackTriggered = true,
                ErrorMessage = "fallback required",
                RequestedAt = requestTime.AddSeconds(index + 10)
            });
            logs.Add(new ProxyUsageLog
            {
                RequestId = requestId,
                AccessKeyId = FinalAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = $"chain-{index}",
                AttemptedModel = $"chain-final-{index}",
                TargetSiteId = finalSiteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 2,
                AttemptIndex = 2,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = string.Empty,
                TotalDurationMs = 200,
                RequestedAt = requestTime.AddSeconds(index + 10).AddSeconds(1)
            });
        }

        db.ProxyUsageLogs.AddRange(logs);
    }

    /// <summary>
    /// 写入固定延迟样本，供百分位数集成测试按模型筛选。
    /// </summary>
    private static void AddPercentileFixtures(AppDbContext db, DateTimeOffset requestTime)
    {
        var logs = new List<ProxyUsageLog>();
        for (var index = 0; index < 4; index++)
        {
            logs.Add(new ProxyUsageLog
            {
                RequestId = Guid.NewGuid(),
                AccessKeyId = FinalAccessKeyId,
                ProtocolType = "OpenAI",
                RequestModel = "percentile-model",
                AttemptedModel = "percentile-model",
                TargetSiteId = FirstSiteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = true,
                TotalDurationMs = (index + 1) * 10,
                FirstTokenLatencyMs = index + 1,
                RequestedAt = requestTime.AddSeconds(index + 100)
            });
        }

        db.ProxyUsageLogs.AddRange(logs);
    }

    /// <summary>
    /// 释放测试宿主。
    /// </summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
