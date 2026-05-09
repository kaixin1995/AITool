using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Domain.Operations;
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

// 使用日志记录失败时，代理请求仍应继续返回，避免内部异常打断正常中转。
public sealed class ProxyResilienceTests
{
    [Fact]
    public async Task OpenAi_proxy_keeps_returning_success_when_usage_log_write_throws()
    {
        var fakeForwardService = new ProxyResilienceFakeForwardService();
        await using var factory = new ProxyResilienceWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "resilience-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("resilience-ok");
    }

    [Fact]
    public async Task Anthropic_proxy_keeps_returning_success_when_usage_log_write_throws()
    {
        var fakeForwardService = new ProxyResilienceFakeForwardService();
        await using var factory = new ProxyResilienceWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "resilience-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("resilience-ok");
    }
}

internal sealed class ProxyResilienceWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-proxy-resilience-{Guid.NewGuid():N}.db");
    private readonly ProxyResilienceFakeForwardService _fakeForwardService;

    public ProxyResilienceWebApplicationFactory(ProxyResilienceFakeForwardService fakeForwardService)
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
            services.RemoveAll<AITool.Application.UsageLogs.IUsageLogService>();
            services.AddSingleton<AITool.Application.UsageLogs.IUsageLogService, ThrowingUsageLogService>();
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

        var openAiSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var anthropicSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var accessKeyRaw = "resilience-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.AddRange(
            new Site
            {
                Id = openAiSiteId,
                Name = "OpenAI Site",
                BaseUrl = "https://openai.example.com",
                ApiKey = "openai-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            },
            new Site
            {
                Id = anthropicSiteId,
                Name = "Anthropic Site",
                BaseUrl = "https://anthropic.example.com",
                ApiKey = "anthropic-key",
                ProtocolType = "Anthropic",
                SupportsOpenAi = false,
                SupportsAnthropic = true,
                IsEnabled = true
            });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            KeyName = "resilience",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***lience",
            IsEnabled = true
        });

        db.ProxyRouteEntries.AddRange(
            new ProxyRouteEntry { EntryName = "auto" },
            new ProxyRouteEntry { EntryName = "claude-proxy" });

        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                ExternalModelName = "auto",
                UpstreamModelName = "gpt-4.1",
                SiteId = openAiSiteId,
                SiteModelName = "gpt-4.1-real",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                ExternalModelName = "claude-proxy",
                UpstreamModelName = "claude-3-7-sonnet",
                SiteId = anthropicSiteId,
                SiteModelName = "claude-3-7-sonnet-real",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            });

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 11,
            ProxyRetryCount = 0,
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
}

internal sealed class ProxyResilienceFakeForwardService : IProxyForwardService
{
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.ProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"id\":\"msg_resilience\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-3-7-sonnet-real\",\"content\":[{\"type\":\"text\",\"text\":\"resilience-ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}",
                InputTokens = 3,
                OutputTokens = 2
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"id\":\"chatcmpl_resilience\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4.1-real\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"resilience-ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}",
            InputTokens = 3,
            OutputTokens = 2
        });
    }

    public Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        return ForwardAsync(request, cancellationToken);
    }
}

internal sealed class ThrowingUsageLogService : IUsageLogService
{
    public Task LogAsync(UsageLogEntry entry, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("usage log write failed");
    }
}
