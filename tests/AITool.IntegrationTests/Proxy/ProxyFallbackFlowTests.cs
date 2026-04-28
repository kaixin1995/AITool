using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;
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
}

internal sealed class ProxyFallbackWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-proxy-fallback-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(new FakeProxyForwardService());
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

        db.Sites.AddRange(firstSite, secondSite);
        db.ProxyAccessKeys.Add(proxyAccessKey);
        db.ProxyRouteRules.AddRange(routeRules);
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

    // 使用固定的两次结果模拟主路由失败、备路由成功的回退链路
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
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
