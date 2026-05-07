using System.Net;
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

// Anthropic 代理入口集成测试，验证鉴权、缓存失效和运行时设置都会按真实入口生效。
public sealed class AnthropicProxyControllerTests
{
    [Fact]
    public async Task Post_messages_uses_x_api_key_and_runtime_settings_for_forward_request()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");
        fakeForwardService.Requests[0].TargetModelName.Should().Be("claude-3-7-sonnet-real");
        fakeForwardService.Requests[0].RequestTimeoutSeconds.Should().Be(11);
        fakeForwardService.Requests[0].RetryCount.Should().Be(4);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("anthropic-proxy-ok");
    }

    [Fact]
    public async Task Post_messages_returns_unauthorized_after_access_key_is_disabled()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var initialResponse = await SendMessagesAsync(client);
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var toggleResponse = await client.PostAsync("/api/admin/access-keys/toggle/99999999-9999-9999-9999-999999999999", null);
        toggleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var disabledResponse = await SendMessagesAsync(client);
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_messages_returns_not_found_after_route_entry_is_deleted()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var initialResponse = await SendMessagesAsync(client);
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteResponse = await client.PostAsync(
            "/api/admin/route-rules/entries/delete",
            new StringContent("{\"entryName\":\"claude-proxy\"}", Encoding.UTF8, "application/json"));
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterDeleteResponse = await SendMessagesAsync(client);
        var body = await afterDeleteResponse.Content.ReadAsStringAsync();

        afterDeleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("No available route for model: claude-proxy");
    }

    [Fact]
    public async Task Post_messages_bridges_to_openai_route_when_only_openai_site_exists()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        var response = await SendMessagesAsync(client);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("OpenAI");
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"model\":\"claude-3-7-sonnet-real\"");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("type").GetString().Should().Be("message");
        document.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("openai-bridged-ok");
        document.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(6);
        document.RootElement.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(9);
    }

    private static Task<HttpResponseMessage> SendMessagesAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        return client.SendAsync(request);
    }
}

internal sealed class AnthropicProxyWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-anthropic-proxy-{Guid.NewGuid():N}.db");
    private readonly AnthropicFakeProxyForwardService _fakeForwardService;
    private readonly string _siteProtocol;

    public AnthropicProxyWebApplicationFactory(AnthropicFakeProxyForwardService fakeForwardService, string siteProtocol = "Anthropic")
    {
        _fakeForwardService = fakeForwardService;
        _siteProtocol = siteProtocol;
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

        var siteId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var accessKeyRaw = "anthropic-test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Anthropic Proxy Site",
            BaseUrl = "https://anthropic-proxy.example.com",
            ApiKey = "site-anthropic-key",
            ProtocolType = _siteProtocol,
            IsEnabled = true
        });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            KeyName = "anthropic",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***ropic",
            IsEnabled = true
        });

        db.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = "claude-proxy"
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            ExternalModelName = "claude-proxy",
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
            ProxyRequestTimeoutSeconds = 11,
            ProxyRetryCount = 4,
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

internal sealed class AnthropicFakeProxyForwardService : IProxyForwardService
{
    public List<ProxyForwardRequest> Requests { get; } = [];

    // 使用固定成功响应，验证 Anthropic 入口会把真实运行时参数传递到转发层。
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

        if (string.Equals(request.ProtocolType, "OpenAI", StringComparison.Ordinal))
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"openai-bridged-ok\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":9}}",
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-proxy-ok\"}],\"usage\":{\"input_tokens\":6,\"output_tokens\":9}}",
            InputTokens = 6,
            OutputTokens = 9
        });
    }
}
