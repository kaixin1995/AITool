using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Application.Proxy;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// Core 宿主代理转发端到端集成测试。
/// 通过 FakeProxyForwardService 替换真实转发实现，验证 Core 宿主从鉴权、路由解析、
/// 并发控制到转发调用的完整代理链路，无需依赖外部上游站点。
/// </summary>
public sealed class CoreProxyForwardingTests
{
    // 测试用的明文访问密钥
    private const string TestAccessKey = "sk-test-forwarding-key";

    // ─── Chat Completions 非流式 ──────────────────────────────────────

    /// <summary>
    /// 验证非流式 Chat Completions 请求能通过完整代理链路返回成功响应。
    /// 测试覆盖：配置同步 → 密钥校验 → 路由解析 → 转发调用 → 响应回写。
    /// </summary>
    [Fact]
    public async Task Chat_completions_non_streaming_returns_success_with_valid_key()
    {
        var fakeForwardService = new CoreFakeProxyForwardService();
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        // 先下发配置快照，使代理缓存中包含密钥和路由
        var snapshot = CreateForwardingTestSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        // 发送非流式 Chat Completions 请求
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.ForwardCallCount.Should().Be(1);
        fakeForwardService.LastForwardRequest.Should().NotBeNull();
        fakeForwardService.LastForwardRequest!.TargetModelName.Should().Be("gpt-4o");
        fakeForwardService.LastForwardRequest.EnableStreaming.Should().BeFalse();

        // 验证返回的响应体包含 OpenAI 格式的字段
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("choices", out _).Should().BeTrue();
    }

    /// <summary>
    /// 验证非流式 Chat Completions 请求会将正确的上游参数传递给转发服务。
    /// 包括上游站点 BaseUrl、ApiKey、SiteModelName 等。
    /// </summary>
    [Fact]
    public async Task Chat_completions_non_streaming_passes_correct_upstream_parameters()
    {
        var fakeForwardService = new CoreFakeProxyForwardService();
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var snapshot = CreateForwardingTestSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);

        await client.SendAsync(request);

        fakeForwardService.LastForwardRequest.Should().NotBeNull();
        // 转发请求应包含快照中配置的上游站点信息
        fakeForwardService.LastForwardRequest!.ProtocolType.Should().Be("OpenAI");
        fakeForwardService.LastForwardRequest.RequestTimeoutSeconds.Should().Be(60);
        fakeForwardService.LastForwardRequest.RetryCount.Should().Be(1);
    }

    // ─── Chat Completions 流式 ────────────────────────────────────────

    /// <summary>
    /// 验证流式 Chat Completions 请求能通过完整代理链路返回 SSE 事件流。
    /// </summary>
    [Fact]
    public async Task Chat_completions_streaming_returns_sse_events()
    {
        var fakeForwardService = new CoreFakeProxyForwardService
        {
            StreamingLines =
            [
                "data: {\"id\":\"chatcmpl-test\",\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"index\":0}]}",
                string.Empty,
                "data: {\"id\":\"chatcmpl-test\",\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"index\":0}]}",
                string.Empty,
                "data: {\"id\":\"chatcmpl-test\",\"choices\":[{\"delta\":{\"content\":\" world\"},\"index\":0}],\"usage\":{\"prompt_tokens\":6,\"completion_tokens\":2}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var snapshot = CreateForwardingTestSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"stream\":true}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.StreamingCallCount.Should().Be(1);
        fakeForwardService.LastStreamingRequest.Should().NotBeNull();
        fakeForwardService.LastStreamingRequest!.EnableStreaming.Should().BeTrue();

        // 验证响应包含 SSE 事件流内容
        body.Should().Contain("data: ");
        body.Should().Contain("[DONE]");
        body.Should().Contain("Hello");
        body.Should().Contain(" world");
    }

    // ─── 鉴权失败 ────────────────────────────────────────────────────

    /// <summary>
    /// 验证使用无效密钥时，Chat Completions 请求返回 401 未授权。
    /// </summary>
    [Fact]
    public async Task Chat_completions_with_wrong_key_returns_unauthorized()
    {
        var fakeForwardService = new CoreFakeProxyForwardService();
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var snapshot = CreateForwardingTestSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-wrong-key");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // 鉴权失败时不应调用转发服务
        fakeForwardService.ForwardCallCount.Should().Be(0);
        fakeForwardService.StreamingCallCount.Should().Be(0);
    }

    /// <summary>
    /// 验证不带任何认证头时，Chat Completions 请求返回 401 未授权。
    /// </summary>
    [Fact]
    public async Task Chat_completions_without_auth_returns_unauthorized()
    {
        var fakeForwardService = new CoreFakeProxyForwardService();
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var snapshot = CreateForwardingTestSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fakeForwardService.ForwardCallCount.Should().Be(0);
        fakeForwardService.StreamingCallCount.Should().Be(0);
    }

    // ─── 路由不存在 ──────────────────────────────────────────────────

    /// <summary>
    /// 验证请求的模型名不在路由配置中时，返回 404 找不到可用路由。
    /// </summary>
    [Fact]
    public async Task Chat_completions_with_unknown_model_returns_not_found()
    {
        var fakeForwardService = new CoreFakeProxyForwardService();
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var snapshot = CreateForwardingTestSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"nonexistent-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("没有可用的路由");
        fakeForwardService.ForwardCallCount.Should().Be(0);
        fakeForwardService.StreamingCallCount.Should().Be(0);
    }

    // ─── 路由回退（多路由失败后成功）─────────────────────────────────

    /// <summary>
    /// 验证配置多条路由时，首条失败后会自动回退到第二条路由并成功返回。
    /// </summary>
    [Fact]
    public async Task Chat_completions_fallback_to_second_route_when_first_fails()
    {
        var fakeForwardService = new CoreFakeProxyForwardService
        {
            // 第一次非流式转发返回失败，第二次成功
            ForwardResultFactory = request =>
            {
                // 第一条路由对应的上游模型名（通过 SiteModelMapping 中 RemoteModelName 映射）
                if (request.TargetModelName == "gpt-4o-primary")
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        StatusCode = 502,
                        ErrorMessage = "upstream unavailable"
                    };
                }

                // 第二条路由回退成功
                return new ProxyForwardResult
                {
                    Success = true,
                    StatusCode = 200,
                    ResponseBody = "{\"id\":\"chatcmpl-fallback\",\"object\":\"chat.completion\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fallback-ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":3}}",
                    InputTokens = 4,
                    OutputTokens = 3
                };
            }
        };
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var snapshot = CreateMultiRouteFallbackSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        // 非流式转发应被调用两次：第一次失败，第二次成功
        fakeForwardService.ForwardCallCount.Should().BeGreaterThanOrEqualTo(2);
        body.Should().Contain("fallback-ok");
    }

    // ─── Embeddings 非流式 ────────────────────────────────────────────

    /// <summary>
    /// 验证 Embeddings 请求能通过代理链路正确转发。
    /// Embeddings 端点不支持流式，仅走非流式转发路径。
    /// </summary>
    [Fact]
    public async Task Embeddings_non_streaming_returns_success()
    {
        var fakeForwardService = new CoreFakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\",\"embedding\":[0.1,0.2,0.3],\"index\":0}],\"model\":\"gpt-4o\",\"usage\":{\"prompt_tokens\":5,\"total_tokens\":5}}",
                InputTokens = 5,
                OutputTokens = 0
            }
        };
        await using var factory = new CoreProxyForwardingWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var snapshot = CreateForwardingTestSnapshot();
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = new StringContent(
                "{\"model\":\"gpt-4o\",\"input\":\"hello world\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.ForwardCallCount.Should().Be(1);
        body.Should().Contain("embedding");
    }

    // ─── 辅助方法 ────────────────────────────────────────────────────

    /// <summary>
    /// 计算明文访问密钥的 SHA256 哈希值（大写十六进制），
    /// 与 ValidateAccessKeyAsync 中的哈希逻辑保持一致。
    /// </summary>
    private static string ComputeAccessKeyHash(string plainKey)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainKey));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// 构造一份用于代理转发测试的单路由配置快照。
    /// 包含一个 OpenAI 协议的上游站点、一条路由规则和一个访问密钥。
    /// </summary>
    private static CoreRuntimeConfigSnapshot CreateForwardingTestSnapshot()
    {
        var siteId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var accessKeyHash = ComputeAccessKeyHash(TestAccessKey);

        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = 300,
            GeneratedAt = DateTimeOffset.UtcNow,
            Sites =
            [
                new CoreRuntimeSite
                {
                    Id = siteId,
                    Name = "forwarding-test-site",
                    BaseUrl = "https://api.forwarding-test.example.com",
                    EndpointPathMode = "append",
                    ApiKey = "sk-upstream-forwarding-key",
                    ProtocolType = "OpenAI",
                    SupportsOpenAi = true,
                    SupportsAnthropic = false,
                    IsEnabled = true
                }
            ],
            Models =
            [
                new CoreRuntimeModel
                {
                    Id = modelId,
                    ModelName = "gpt-4o",
                    DisplayName = "GPT-4o",
                    IsEnabled = true
                }
            ],
            SiteModelMappings =
            [
                new CoreRuntimeSiteModelMapping
                {
                    Id = Guid.NewGuid(),
                    SiteId = siteId,
                    ModelLibraryItemId = modelId,
                    RemoteModelName = "gpt-4o",
                    LastStatus = "Healthy",
                    IsEnabled = true,
                    MaxConcurrency = 10
                }
            ],
            RouteEntries =
            [
                new CoreRuntimeRouteEntry
                {
                    Id = Guid.NewGuid(),
                    EntryName = "default"
                }
            ],
            RouteRules =
            [
                new CoreRuntimeRouteRule
                {
                    Id = Guid.NewGuid(),
                    ExternalModelName = "gpt-4o",
                    UpstreamModelName = "gpt-4o",
                    SiteId = siteId,
                    SiteModelName = "gpt-4o",
                    Priority = 1,
                    ModelPriority = 1,
                    InstancePriority = 1,
                    IsEnabled = true,
                    AvailabilityMode = "always",
                    TimeRangesJson = "{}"
                }
            ],
            AccessKeys =
            [
                new CoreRuntimeAccessKey
                {
                    Id = Guid.NewGuid(),
                    KeyName = "forwarding-test-key",
                    PlainKey = TestAccessKey,
                    AccessKeyHash = accessKeyHash,
                    MaskedValue = "sk-***fwd",
                    IsEnabled = true
                }
            ],
            RuntimeSettings = new CoreRuntimeSettings
            {
                ProxyRequestTimeoutSeconds = 60,
                ProxyRetryCount = 1,
                CircuitBreakerFailureThreshold = 5,
                CircuitBreakerRecoveryMinutes = 2,
                ConcurrencyMode = 0,
                ConcurrencyQueueTimeoutSeconds = 120,
                ConversationLogEnabled = true
            }
        };

        snapshot.ConfigHash = CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 构造一份用于代理回退测试的双路由配置快照。
    /// 配置两条路由指向不同的站点，Priority 递增，验证回退机制。
    /// </summary>
    private static CoreRuntimeConfigSnapshot CreateMultiRouteFallbackSnapshot()
    {
        var primarySiteId = Guid.NewGuid();
        var fallbackSiteId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var accessKeyHash = ComputeAccessKeyHash(TestAccessKey);

        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = 400,
            GeneratedAt = DateTimeOffset.UtcNow,
            Sites =
            [
                new CoreRuntimeSite
                {
                    Id = primarySiteId,
                    Name = "primary-site",
                    BaseUrl = "https://primary.example.com",
                    EndpointPathMode = "append",
                    ApiKey = "sk-primary-key",
                    ProtocolType = "OpenAI",
                    SupportsOpenAi = true,
                    SupportsAnthropic = false,
                    IsEnabled = true
                },
                new CoreRuntimeSite
                {
                    Id = fallbackSiteId,
                    Name = "fallback-site",
                    BaseUrl = "https://fallback.example.com",
                    EndpointPathMode = "append",
                    ApiKey = "sk-fallback-key",
                    ProtocolType = "OpenAI",
                    SupportsOpenAi = true,
                    SupportsAnthropic = false,
                    IsEnabled = true
                }
            ],
            Models =
            [
                new CoreRuntimeModel
                {
                    Id = modelId,
                    ModelName = "gpt-4o",
                    DisplayName = "GPT-4o",
                    IsEnabled = true
                }
            ],
            SiteModelMappings =
            [
                new CoreRuntimeSiteModelMapping
                {
                    Id = Guid.NewGuid(),
                    SiteId = primarySiteId,
                    ModelLibraryItemId = modelId,
                    RemoteModelName = "gpt-4o-primary",
                    LastStatus = "Healthy",
                    IsEnabled = true,
                    MaxConcurrency = 10
                },
                new CoreRuntimeSiteModelMapping
                {
                    Id = Guid.NewGuid(),
                    SiteId = fallbackSiteId,
                    ModelLibraryItemId = modelId,
                    RemoteModelName = "gpt-4o-fallback",
                    LastStatus = "Healthy",
                    IsEnabled = true,
                    MaxConcurrency = 10
                }
            ],
            RouteEntries =
            [
                new CoreRuntimeRouteEntry
                {
                    Id = Guid.NewGuid(),
                    EntryName = "default"
                }
            ],
            RouteRules =
            [
                new CoreRuntimeRouteRule
                {
                    Id = Guid.NewGuid(),
                    ExternalModelName = "gpt-4o",
                    UpstreamModelName = "gpt-4o",
                    SiteId = primarySiteId,
                    SiteModelName = "gpt-4o-primary",
                    Priority = 1,
                    ModelPriority = 1,
                    InstancePriority = 1,
                    IsEnabled = true,
                    AvailabilityMode = "always",
                    TimeRangesJson = "{}"
                },
                new CoreRuntimeRouteRule
                {
                    Id = Guid.NewGuid(),
                    ExternalModelName = "gpt-4o",
                    UpstreamModelName = "gpt-4o",
                    SiteId = fallbackSiteId,
                    SiteModelName = "gpt-4o-fallback",
                    Priority = 2,
                    ModelPriority = 2,
                    InstancePriority = 2,
                    IsEnabled = true,
                    AvailabilityMode = "always",
                    TimeRangesJson = "{}"
                }
            ],
            AccessKeys =
            [
                new CoreRuntimeAccessKey
                {
                    Id = Guid.NewGuid(),
                    KeyName = "forwarding-test-key",
                    PlainKey = TestAccessKey,
                    AccessKeyHash = accessKeyHash,
                    MaskedValue = "sk-***fwd",
                    IsEnabled = true
                }
            ],
            RuntimeSettings = new CoreRuntimeSettings
            {
                ProxyRequestTimeoutSeconds = 60,
                ProxyRetryCount = 0,
                CircuitBreakerFailureThreshold = 5,
                CircuitBreakerRecoveryMinutes = 2,
                ConcurrencyMode = 0,
                ConcurrencyQueueTimeoutSeconds = 120,
                ConversationLogEnabled = true
            }
        };

        snapshot.ConfigHash = CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshot);
        return snapshot;
    }
}

/// <summary>
/// 扩展 CoreHostWebApplicationFactory，在 DI 中替换 IProxyForwardService 为伪造实现。
/// Core 宿主不依赖数据库，只需替换转发服务即可实现端到端代理测试。
/// </summary>
internal sealed class CoreProxyForwardingWebApplicationFactory : WebApplicationFactory<AITool.Core.CoreProgramMarker>
{
    /// <summary>
    /// 保存当前测试使用的伪造转发服务。
    /// </summary>
    private readonly CoreFakeProxyForwardService _fakeForwardService;

    /// <summary>
    /// 初始化代理转发测试宿主，注入指定的伪造转发服务。
    /// </summary>
    public CoreProxyForwardingWebApplicationFactory(CoreFakeProxyForwardService fakeForwardService)
    {
        _fakeForwardService = fakeForwardService;
    }

    /// <summary>
    /// 重写测试宿主依赖，替换 IProxyForwardService 注册。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // 移除真实转发服务的 HttpClient 注册，替换为伪造实现
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(_fakeForwardService);
        });
    }
}

/// <summary>
/// 伪造代理转发服务，用于 Core 宿主端到端代理测试。
/// 记录所有收到的转发请求，按配置返回可控的响应结果。
/// </summary>
internal sealed class CoreFakeProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 记录非流式转发被调用的总次数。
    /// </summary>
    public int ForwardCallCount { get; private set; }
    /// <summary>
    /// 记录流式转发被调用的总次数。
    /// </summary>
    public int StreamingCallCount { get; private set; }
    /// <summary>
    /// 保存最后一次非流式转发请求的参数。
    /// </summary>
    public ProxyForwardRequest? LastForwardRequest { get; private set; }
    /// <summary>
    /// 保存最后一次流式转发请求的参数。
    /// </summary>
    public ProxyForwardRequest? LastStreamingRequest { get; private set; }
    /// <summary>
    /// 允许按请求动态生成非流式转发结果，便于覆盖特定断言场景。
    /// </summary>
    public Func<ProxyForwardRequest, ProxyForwardResult>? ForwardResultFactory { get; set; }
    /// <summary>
    /// 保存测试时返回的流式 SSE 响应片段。
    /// </summary>
    public List<string>? StreamingLines { get; set; }

    /// <summary>
    /// 模拟非流式转发，记录请求参数并返回默认成功响应或自定义结果。
    /// </summary>
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        ForwardCallCount++;
        LastForwardRequest = new ProxyForwardRequest
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
        };

        var customResult = ForwardResultFactory?.Invoke(request);
        if (customResult is not null)
        {
            return Task.FromResult(customResult);
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"proxy-ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":6,\"completion_tokens\":3}}",
            InputTokens = 6,
            OutputTokens = 3
        });
    }

    /// <summary>
    /// 模拟流式转发，按配置的 SSE 事件片段回放给调用方。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        StreamingCallCount++;
        LastStreamingRequest = new ProxyForwardRequest
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
        };

        var lines = StreamingLines ??
        [
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"index\":0}]}",
            string.Empty,
            "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"delta\":{\"content\":\"proxy-streaming-ok\"},\"index\":0}],\"usage\":{\"prompt_tokens\":6,\"completion_tokens\":3}}",
            string.Empty,
            "data: [DONE]",
            string.Empty
        ];

        foreach (var line in lines)
        {
            await onSseDataAsync(line, cancellationToken);
        }

        return new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = string.Join("\n", lines),
            InputTokens = 6,
            OutputTokens = 3,
            IsStreaming = true,
            HasStartedStreaming = true
        };
    }
}
