using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
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

// OpenAI 代理入口跨协议测试，验证 OpenAI 客户端也能转发到 Anthropic 站点。
public sealed class OpenAiCrossProtocolProxyTests
{
    [Fact]
    public async Task Post_chat_completions_bridges_to_anthropic_route_when_only_anthropic_site_exists()
    {
        var fakeForwardService = new OpenAiCrossProtocolFakeProxyForwardService();
        await using var factory = new OpenAiCrossProtocolWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "openai-cross-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"model\":\"claude-3-7-sonnet-real\"");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("object").GetString().Should().Be("chat.completion");
        document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("anthropic-bridged-ok");
        document.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(7);
        document.RootElement.GetProperty("usage").GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(8);
    }
}

internal sealed class OpenAiCrossProtocolWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-openai-cross-{Guid.NewGuid():N}.db");
    private readonly OpenAiCrossProtocolFakeProxyForwardService _fakeForwardService;

    public OpenAiCrossProtocolWebApplicationFactory(OpenAiCrossProtocolFakeProxyForwardService fakeForwardService)
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

        var siteId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var accessKeyRaw = "openai-cross-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Anthropic Only Site",
            BaseUrl = "https://anthropic-only.example.com",
            ApiKey = "anthropic-only-key",
            ProtocolType = "Anthropic",
            IsEnabled = true
        });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("34343434-3434-3434-3434-343434343434"),
            KeyName = "openai-cross",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***cross",
            IsEnabled = true
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("56565656-5656-5656-5656-565656565656"),
            ExternalModelName = "auto",
            UpstreamModelName = "claude-3-7-sonnet",
            SiteId = siteId,
            SiteModelName = "claude-3-7-sonnet-real",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 12,
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
}

internal sealed class OpenAiCrossProtocolFakeProxyForwardService : IProxyForwardService
{
    public List<ProxyForwardRequest> Requests { get; } = [];

    // 用固定 Anthropic 响应验证 OpenAI 入口收到的最终格式已经转换回 OpenAI。
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(new ProxyForwardRequest
        {
            TargetBaseUrl = request.TargetBaseUrl,
            TargetApiKey = request.TargetApiKey,
            ProtocolType = request.ProtocolType,
            TargetModelName = request.TargetModelName,
            RequestBody = request.RequestBody,
            PreparedRequestBody = request.PreparedRequestBody,
            EnableStreaming = request.EnableStreaming,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds,
            RetryCount = request.RetryCount
        });

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-bridged-ok\"}],\"usage\":{\"input_tokens\":7,\"cache_read_input_tokens\":1,\"output_tokens\":8}}",
            InputTokens = 7,
            CachedTokens = 1,
            OutputTokens = 8
        });
    }
}
