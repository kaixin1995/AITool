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
    public async Task Get_models_returns_anthropic_model_list()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("data")[0].GetProperty("id").GetString().Should().Be("claude-proxy");
        document.RootElement.GetProperty("data")[0].GetProperty("type").GetString().Should().Be("model");
        document.RootElement.GetProperty("has_more").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Post_count_tokens_returns_estimated_input_tokens()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/count_tokens")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"system\":\"You are helpful\",\"messages\":[{\"role\":\"user\",\"content\":\"hello world\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("input_tokens").GetInt32().Should().BeGreaterThan(0);
    }

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
    public async Task Post_messages_accepts_bearer_key_and_forwards_anthropic_headers()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "token-counting-2024-11-01");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ForwardHeaders["anthropic-version"].Should().Be("2023-06-01");
        fakeForwardService.Requests[0].ForwardHeaders["anthropic-beta"].Should().Be("token-counting-2024-11-01");
    }

    [Fact]
    public async Task Post_messages_preserves_whitespace_only_stream_chunks_when_bridging_openai_stream()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"```bash\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"\\n\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"curl test\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":0},\"completion_tokens\":2}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\\u0060\\u0060\\u0060bash");
        body.Should().Contain("\"text\":\"\\n\"");
        body.Should().Contain("curl test");
        body.Should().NotContain("bashcurl");
    }

    [Fact]
    public async Task Post_messages_preserves_space_only_stream_chunks_when_bridging_openai_stream()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\" \"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":0},\"completion_tokens\":2}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"text\":\" \"");
        body.Should().NotContain("\"text\":\"AB\"");
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
    public async Task Post_messages_falls_back_to_next_route_when_openai_stream_fails_before_first_chunk()
    {
        var fakeForwardService = new AnthropicFallbackStreamProxyForwardService();
        await using var factory = new AnthropicProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.StreamAttemptCount.Should().Be(1);
        fakeForwardService.NonStreamAttemptCount.Should().Be(1);
        body.Should().Contain("anthropic-fallback-ok");
    }

    [Fact]
    public async Task Post_messages_stops_fallback_when_openai_stream_fails_after_first_chunk()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
                string.Empty
            ],
            StreamingResultFactory = _ => new ProxyForwardResult
            {
                Success = false,
                StatusCode = 502,
                ErrorMessage = "stream interrupted after first chunk",
                IsStreaming = true,
                HasStartedStreaming = true,
                IsStreamInterrupted = true,
                TotalDurationMs = 12
            }
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("event: message_start");
        body.Should().Contain("event: content_block_delta");
        body.Should().Contain("event: message_stop");
        body.Should().NotContain("StatusCode cannot be set because the response has already started");
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

internal sealed class AnthropicProxyFallbackWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-anthropic-proxy-fallback-{Guid.NewGuid():N}.db");
    private readonly AnthropicFallbackStreamProxyForwardService _fakeForwardService;

    public AnthropicProxyFallbackWebApplicationFactory(AnthropicFallbackStreamProxyForwardService fakeForwardService)
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

        var openAiSiteId = Guid.Parse("71717171-7171-7171-7171-717171717171");
        var anthropicSiteId = Guid.Parse("72727272-7272-7272-7272-727272727272");
        var accessKeyRaw = "anthropic-test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.AddRange(
            new Site
            {
                Id = openAiSiteId,
                Name = "OpenAI First",
                BaseUrl = "https://openai-first.example.com",
                ApiKey = "site-openai-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            },
            new Site
            {
                Id = anthropicSiteId,
                Name = "Anthropic Second",
                BaseUrl = "https://anthropic-second.example.com",
                ApiKey = "site-anthropic-key",
                ProtocolType = "Anthropic",
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

        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                Id = Guid.Parse("73737373-7373-7373-7373-737373737373"),
                ExternalModelName = "claude-proxy",
                UpstreamModelName = "claude-openai-primary",
                SiteId = openAiSiteId,
                SiteModelName = "claude-openai-primary-real",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("74747474-7474-7474-7474-747474747474"),
                ExternalModelName = "claude-proxy",
                UpstreamModelName = "claude-anthropic-secondary",
                SiteId = anthropicSiteId,
                SiteModelName = "claude-anthropic-secondary-real",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 1,
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

internal sealed class AnthropicFakeProxyForwardService : IProxyForwardService
{
    public List<ProxyForwardRequest> Requests { get; } = [];
    public List<string>? OpenAiStreamingLines { get; set; }
    public Func<ProxyForwardRequest, ProxyForwardResult>? StreamingResultFactory { get; set; }

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
            RetryCount = request.RetryCount,
            TargetPath = request.TargetPath,
            ForwardHeaders = new Dictionary<string, string>(request.ForwardHeaders, StringComparer.OrdinalIgnoreCase)
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

    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
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
            RetryCount = request.RetryCount,
            TargetPath = request.TargetPath,
            ForwardHeaders = new Dictionary<string, string>(request.ForwardHeaders, StringComparer.OrdinalIgnoreCase)
        });

        if (string.Equals(request.ProtocolType, "OpenAI", StringComparison.Ordinal))
        {
            var lines = OpenAiStreamingLines ??
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":9}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ];

            foreach (var line in lines)
            {
                await onSseDataAsync(line, cancellationToken);
            }

            var streamingResult = StreamingResultFactory?.Invoke(request);
            if (streamingResult is not null)
            {
                streamingResult.ResponseBody = string.IsNullOrWhiteSpace(streamingResult.ResponseBody)
                    ? string.Join("\n", lines)
                    : streamingResult.ResponseBody;
                streamingResult.InputTokens = streamingResult.InputTokens == 0 ? 6 : streamingResult.InputTokens;
                streamingResult.CachedTokens = streamingResult.CachedTokens;
                streamingResult.OutputTokens = streamingResult.OutputTokens == 0 ? 9 : streamingResult.OutputTokens;
                streamingResult.IsStreaming = true;
                return streamingResult;
            }

            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = string.Join("\n", lines),
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9,
                IsStreaming = true,
                HasStartedStreaming = true
            };
        }

        var result = await ForwardAsync(request, cancellationToken);
        foreach (var line in result.ResponseBody.Replace("\r\n", "\n").Split('\n'))
        {
            await onSseDataAsync(line, cancellationToken);
        }

        result.IsStreaming = true;
        return result;
    }
}

internal sealed class AnthropicFallbackStreamProxyForwardService : IProxyForwardService
{
    public int StreamAttemptCount { get; private set; }
    public int NonStreamAttemptCount { get; private set; }

    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        NonStreamAttemptCount++;
        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-fallback-ok\"}],\"usage\":{\"input_tokens\":4,\"output_tokens\":6}}",
            InputTokens = 4,
            OutputTokens = 6,
            IsStreaming = false
        });
    }

    public Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        StreamAttemptCount++;
        return Task.FromResult(new ProxyForwardResult
        {
            Success = false,
            StatusCode = 502,
            ErrorMessage = "upstream failed before first chunk",
            IsStreaming = true,
            HasStartedStreaming = false,
            TotalDurationMs = 5
        });
    }
}
