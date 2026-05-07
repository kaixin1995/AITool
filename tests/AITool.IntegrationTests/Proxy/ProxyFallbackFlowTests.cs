using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
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

namespace AITool.IntegrationTests.Proxy;

// 代理回退链路集成测试，验证按顺序 fallback 并记录每次尝试日志
public sealed class ProxyFallbackFlowTests
{
    [Fact]
    public async Task Get_entries_returns_master_entry_names_with_candidate_counts()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/route-rules/entries");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"entryName\":\"chat-prod\"");
        body.Should().Contain("\"candidateCount\":2");
    }

    [Fact]
    public async Task Post_entries_creates_empty_master_entry_visible_in_entry_list()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
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

    [Fact]
    public async Task Delete_entry_removes_all_rules_for_that_master_entry()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
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

    [Fact]
    public async Task Save_route_rules_accepts_multiple_upstream_model_groups()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
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

    [Fact]
    public async Task Get_routes_page_contains_search_box_and_hides_protocol_rendering_text()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Routes");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("搜索站点或模型");
        html.Should().NotContain("item.protocolType");
    }

    [Fact]
    public async Task Post_chat_completions_falls_back_to_next_route_and_persists_attempt_logs()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("success-from-second-route");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.OrderBy(x => x.AttemptIndex).ToListAsync();

        logs.Should().HaveCount(2);
        logs[0].AttemptedModel.Should().Be("gpt-5.5");
        logs[0].Status.Should().Be("fail");
        logs[0].FallbackTriggered.Should().BeTrue();
        logs[1].AttemptedModel.Should().Be("glm-5.1");
        logs[1].Status.Should().Be("success");
        logs[1].IsFinalResult.Should().BeTrue();
    }

    [Fact]
    public async Task Get_models_returns_unauthorized_after_access_key_is_disabled()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var initialResponse = await client.SendAsync(authorizedRequest);
        var initialBody = await initialResponse.Content.ReadAsStringAsync();

        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK, initialBody);
        initialBody.Should().Contain("\"id\":\"chat-prod\"");

        var toggleResponse = await client.PostAsync("/api/admin/access-keys/toggle/33333333-3333-3333-3333-333333333333", null);
        toggleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var disabledRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        disabledRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var disabledResponse = await client.SendAsync(disabledRequest);
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_models_refreshes_after_route_entry_is_deleted()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var beforeRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        beforeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        var beforeResponse = await client.SendAsync(beforeRequest);
        var beforeBody = await beforeResponse.Content.ReadAsStringAsync();

        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK, beforeBody);
        beforeBody.Should().Contain("\"id\":\"chat-prod\"");

        var deleteResponse = await client.PostAsync(
            "/api/admin/route-rules/entries/delete",
            new StringContent("{\"entryName\":\"chat-prod\"}", Encoding.UTF8, "application/json"));

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        afterRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        var afterResponse = await client.SendAsync(afterRequest);
        var afterBody = await afterResponse.Content.ReadAsStringAsync();

        afterResponse.StatusCode.Should().Be(HttpStatusCode.OK, afterBody);
        afterBody.Should().NotContain("\"id\":\"chat-prod\"");
    }

    [Fact]
    public async Task Post_chat_completions_uses_runtime_settings_for_forward_request()
    {
        var fakeForwardService = new FakeProxyForwardService();
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeForwardService.Requests.Should().HaveCount(2);
        fakeForwardService.Requests[0].RequestTimeoutSeconds.Should().Be(9);
        fakeForwardService.Requests[0].RetryCount.Should().Be(2);
        fakeForwardService.Requests[1].RequestTimeoutSeconds.Should().Be(9);
        fakeForwardService.Requests[1].RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task Save_route_rules_persists_latest_manual_order_used_by_followup_request()
    {
        var fakeForwardService = new FakeProxyForwardService();
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var saveResponse = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"glm-5.1\",\"siteId\":\"22222222-2222-2222-2222-222222222222\",\"siteModelName\":\"glm-5.1-a\"},{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"}]}",
                Encoding.UTF8,
                "application/json"));

        saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeForwardService.Requests.Should().HaveCount(2);
        fakeForwardService.Requests[0].TargetModelName.Should().Be("glm-5.1-a");
    }

    [Fact]
    public async Task Save_route_rules_allows_same_site_to_appear_multiple_times()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
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

internal sealed class ProxyFallbackWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-proxy-fallback-{Guid.NewGuid():N}.db");
    private readonly FakeProxyForwardService _fakeForwardService;

    public ProxyFallbackWebApplicationFactory()
        : this(new FakeProxyForwardService())
    {
    }

    public ProxyFallbackWebApplicationFactory(FakeProxyForwardService fakeForwardService)
    {
        _fakeForwardService = fakeForwardService;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(_fakeForwardService);
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

        var firstSite = new Site
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Primary OpenAI",
            BaseUrl = "https://invalid-primary.example.com",
            ApiKey = "upstream-key-1",
            ProtocolType = "OpenAI",
            IsEnabled = true
        };
        var secondSite = new Site
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Fallback GLM",
            BaseUrl = "https://invalid-fallback.example.com",
            ApiKey = "upstream-key-2",
            ProtocolType = "OpenAI",
            IsEnabled = true
        };
        var thirdSite = new Site
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Name = "Primary OpenAI Replica",
            BaseUrl = "https://invalid-replica.example.com",
            ApiKey = "upstream-key-3",
            ProtocolType = "OpenAI",
            IsEnabled = true
        };

        var accessKeyRaw = "test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));
        var proxyAccessKey = new ProxyAccessKey
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            KeyName = "integration",
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***key",
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
        db.ProxyAccessKeys.Add(proxyAccessKey);
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

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}

internal sealed class FakeProxyForwardService : IProxyForwardService
{
    private int _attemptCount;

    public List<ProxyForwardRequest> Requests { get; } = new();

    // 使用固定的两次结果模拟主路由失败、备路由成功的回退链路
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(new ProxyForwardRequest
        {
            TargetBaseUrl = request.TargetBaseUrl,
            TargetApiKey = request.TargetApiKey,
            ProtocolType = request.ProtocolType,
            TargetModelName = request.TargetModelName,
            RequestBody = request.RequestBody,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds,
            RetryCount = request.RetryCount
        });

        _attemptCount++;
        if (_attemptCount == 1)
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = false,
                StatusCode = 500,
                ErrorMessage = "first route failed"
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"success-from-second-route\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}",
            InputTokens = 1,
            OutputTokens = 2
        });
    }
}
