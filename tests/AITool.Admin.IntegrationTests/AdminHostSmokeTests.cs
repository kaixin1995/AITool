using System.Net;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 验证独立 Admin 宿主骨架至少可以正常启动并暴露最小 HTTP 管线。
/// 当前阶段不要求迁完真实 /Admin/* 页面，先确保宿主本身可独立编译和拉起。
/// </summary>
public sealed class AdminHostSmokeTests
{
    /// <summary>
    /// 独立 Admin 宿主应能成功启动，并返回一个非 500 的基础响应。
    /// 这里先用不存在的路径做最小 smoke check，避免测试依赖真实页面迁移进度。
    /// </summary>
    [Fact]
    public async Task Admin_host_starts_successfully()
    {
        await using var factory = new AdminHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/not-found-smoke-check");
        // SPA fallback：测试环境无 wwwroot 构建产物时返回 404，有产物时返回 200（index.html）。
        // 关键是不返回 500，证明宿主管线正常工作。
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }
}

internal sealed class AdminHostWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-admin-host-{Guid.NewGuid():N}.db");

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
        await IntegrationTestDbHelper.InitializeDatabaseAsync(Services);
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var siteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await db.InsertAsync(new Site
        {
            Id = siteId,
            Name = "Admin Host Site",
            BaseUrl = "https://admin-host.example.com",
            ApiKey = "site-key",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        });

        await db.InsertAsync(new ProxyRouteRule
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            SiteId = siteId,
            ExternalModelName = "chat-prod",
            UpstreamModelName = "gpt-5.4",
            SiteModelName = "gpt-5.4-site",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true,
            AvailabilityMode = "AllDay",
            TimeRangesJson = string.Empty
        });

        await db.InsertRangeAsync(new[]
        {
            new ProxyUsageLog
            {
                RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                ProtocolType = "OpenAI",
                ForwardingMode = "direct",
                RequestModel = "chat-prod",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteId,
                Status = "fail",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 1,
                IsFinalResult = false,
                FallbackTriggered = true,
                ErrorMessage = "首次尝试超时",
                InputTokens = 10,
                CachedTokens = 2,
                OutputTokens = 0,
                TotalTokens = 12,
                IsStreaming = true,
                IsStreamInterrupted = true,
                FirstTokenLatencyMs = 0,
                StreamDurationMs = 0,
                TotalDurationMs = 1200,
                ReasoningEffort = "medium",
                RequestedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)
            },
            new ProxyUsageLog
            {
                RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                ProtocolType = "OpenAI",
                ForwardingMode = "direct",
                RequestModel = "chat-prod",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = siteId,
                Status = "success",
                Source = "proxy",
                RetryCount = 1,
                AttemptIndex = 2,
                IsFinalResult = true,
                FallbackTriggered = true,
                ErrorMessage = string.Empty,
                InputTokens = 10,
                CachedTokens = 2,
                OutputTokens = 6,
                TotalTokens = 18,
                IsStreaming = true,
                IsStreamInterrupted = false,
                FirstTokenLatencyMs = 30,
                StreamDurationMs = 150,
                TotalDurationMs = 300,
                ReasoningEffort = "medium",
                RequestedAt = new DateTimeOffset(2026, 6, 10, 10, 1, 0, TimeSpan.Zero)
            },
            new ProxyUsageLog
            {
                RequestId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                AccessKeyId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                ProtocolType = "Anthropic",
                ForwardingMode = "bridge",
                RequestModel = "claude-opus",
                AttemptedModel = "claude-opus-4-8",
                TargetSiteId = siteId,
                Status = "success",
                Source = "claude-code",
                RetryCount = 0,
                AttemptIndex = 1,
                IsFinalResult = true,
                FallbackTriggered = false,
                ErrorMessage = string.Empty,
                InputTokens = 128,
                CachedTokens = 64,
                OutputTokens = 96,
                TotalTokens = 288,
                IsStreaming = false,
                IsStreamInterrupted = false,
                FirstTokenLatencyMs = 20,
                StreamDurationMs = 0,
                TotalDurationMs = 180,
                ReasoningEffort = "high",
                RequestedAt = new DateTimeOffset(2026, 6, 11, 9, 30, 0, TimeSpan.Zero)
            }
        });
    }
}
