using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Models;
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
using System.Net.Http.Headers;

namespace AITool.IntegrationTests.Chat;

// 聊天测试接口集成测试，覆盖跨协议非流式发送的准确性。
public sealed class ChatApiTests
{
    [Fact]
    public async Task Post_send_uses_route_protocol_for_anthropic_targets()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        await using var factory = new ChatWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/chat/send",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"message\":\"hello\",\"enableReasoning\":false,\"enableStreaming\":false}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().HaveCount(1);
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");
        fakeForwardService.Requests[0].RequestBody.Should().Contain("\"messages\"");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("content").GetString().Should().Be("anthropic-ok");
        document.RootElement.GetProperty("inputTokens").GetInt32().Should().Be(3);
        document.RootElement.GetProperty("outputTokens").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Post_send_stream_uses_anthropic_sse_protocol_and_returns_stream_events()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        var fakeHttpFactory = new ChatFakeHttpClientFactory(new ChatStreamingHttpMessageHandler());
        await using var factory = new ChatWebApplicationFactory(fakeForwardService, fakeHttpFactory);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/chat/send-stream",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"message\":\"hello-stream\",\"enableReasoning\":true,\"enableStreaming\":true}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        fakeHttpFactory.Handler.Requests.Should().HaveCount(1);
        fakeHttpFactory.Handler.Requests[0].RequestUri!.AbsoluteUri.Should().EndWith("/v1/messages");
        fakeHttpFactory.Handler.Requests[0].Headers.Contains("x-api-key").Should().BeTrue();
        fakeHttpFactory.Handler.Requests[0].Headers.Authorization.Should().BeNull();
        body.Should().Contain("event: reasoning");
        body.Should().Contain("ponder");
        body.Should().Contain("event: token");
        body.Should().Contain("stream-ok");
        body.Should().Contain("event: meta");
        body.Should().Contain("\"success\":true");
        body.Should().Contain("\"inputTokens\":4");
        body.Should().Contain("\"outputTokens\":6");
        body.Should().Contain("event: done");
    }
}

internal sealed class ChatWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid ModelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-chat-{Guid.NewGuid():N}.db");
    private readonly ChatFakeProxyForwardService _fakeForwardService;
    private readonly IHttpClientFactory? _httpClientFactory;

    public ChatWebApplicationFactory(ChatFakeProxyForwardService fakeForwardService)
        : this(fakeForwardService, null)
    {
    }

    public ChatWebApplicationFactory(ChatFakeProxyForwardService fakeForwardService, IHttpClientFactory? httpClientFactory)
    {
        _fakeForwardService = fakeForwardService;
        _httpClientFactory = httpClientFactory;
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
            if (_httpClientFactory is not null)
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton(_httpClientFactory);
            }
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

        var siteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Anthropic Site",
            BaseUrl = "https://anthropic.example.com",
            ApiKey = "anthropic-key",
            ProtocolType = "Anthropic",
            SupportsOpenAi = false,
            SupportsAnthropic = true,
            IsEnabled = true
        });

        db.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = ModelId,
            ModelName = "chat-anthropic",
            DisplayName = "Chat Anthropic",
            IsEnabled = true
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            ExternalModelName = "chat-anthropic",
            UpstreamModelName = "claude-3-7-sonnet",
            SiteId = siteId,
            SiteModelName = "claude-3-7-sonnet",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 8,
            ProxyRetryCount = 1,
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

internal sealed class ChatFakeProxyForwardService : IProxyForwardService
{
    public List<ProxyForwardRequest> Requests { get; } = [];

    // 根据请求协议返回对应的兼容响应，验证聊天页非流式调用不会误用 OpenAI 协议。
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

        if (request.ProtocolType == "Anthropic")
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-ok\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":5}}",
                InputTokens = 3,
                OutputTokens = 5
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"openai-ok\"}}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":4}}",
            InputTokens = 2,
            OutputTokens = 4
        });
    }

    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        var result = await ForwardAsync(request, cancellationToken);
        foreach (var line in result.ResponseBody.Replace("\r\n", "\n").Split('\n'))
        {
            await onSseDataAsync(line, cancellationToken);
        }

        result.IsStreaming = true;
        return result;
    }
}

internal sealed class ChatFakeHttpClientFactory : IHttpClientFactory
{
    public ChatStreamingHttpMessageHandler Handler { get; }

    public ChatFakeHttpClientFactory(ChatStreamingHttpMessageHandler handler)
    {
        Handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(Handler, disposeHandler: false);
    }
}

internal sealed class ChatStreamingHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    // 模拟 Anthropic SSE，验证流式聊天链路会按真实协议解析思考与正文。
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(CloneRequest(request));

        var payload = string.Join(
            "\n",
            "event: message_start",
            "data: {\"message\":{\"usage\":{\"input_tokens\":4,\"output_tokens\":0}}}",
            string.Empty,
            "event: content_block_delta",
            "data: {\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"ponder\"}}",
            string.Empty,
            "event: content_block_delta",
            "data: {\"delta\":{\"type\":\"text_delta\",\"text\":\"stream-ok\"}}",
            string.Empty,
            "event: message_delta",
            "data: {\"usage\":{\"output_tokens\":6}}",
            string.Empty,
            "data: [DONE]",
            string.Empty);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

        return Task.FromResult(response);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
