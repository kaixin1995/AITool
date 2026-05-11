using System.Net;
using System.Net.Http;
using AITool.Application.Proxy;
using AITool.Application.Routing;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Routing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Routing;

// 路由选择服务测试，验证优先级排序和启用过滤
public sealed class RouteSelectionServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly RouteSelectionService _service;

    public RouteSelectionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _service = new RouteSelectionService(_dbContext);
    }

    [Fact]
    public async Task SelectRouteAsync_returns_highest_priority_enabled_route()
    {
        // 多个候选路由时，返回优先级最高（数值最小）的启用路由
        var siteId = Guid.NewGuid();
        _dbContext.ProxyRouteRules.AddRange(
            new ProxyRouteRule { ExternalModelName = "gpt-5", UpstreamModelName = "gpt-5", SiteId = siteId, SiteModelName = "gpt-5.4", Priority = 10, ModelPriority = 10, InstancePriority = 0, IsEnabled = true },
            new ProxyRouteRule { ExternalModelName = "gpt-5", UpstreamModelName = "gpt-5", SiteId = siteId, SiteModelName = "gpt-5-turbo", Priority = 2, ModelPriority = 2, InstancePriority = 0, IsEnabled = true },
            new ProxyRouteRule { ExternalModelName = "gpt-5", UpstreamModelName = "gpt-5", SiteId = siteId, SiteModelName = "gpt-5-pro", Priority = 5, ModelPriority = 5, InstancePriority = 0, IsEnabled = true }
        );
        await _dbContext.SaveChangesAsync();

        var result = await _service.SelectRouteAsync("gpt-5");

        result.Found.Should().BeTrue();
        result.Route!.SiteModelName.Should().Be("gpt-5-turbo");
        result.Route.Priority.Should().Be(2);
    }

    [Fact]
    public async Task SelectRouteAsync_excludes_disabled_routes()
    {
        // 仅禁用路由存在时，不应返回任何结果
        _dbContext.ProxyRouteRules.Add(
            new ProxyRouteRule { ExternalModelName = "claude-4", UpstreamModelName = "claude-4", SiteId = Guid.NewGuid(), SiteModelName = "claude-4-opus", Priority = 1, ModelPriority = 1, InstancePriority = 0, IsEnabled = false }
        );
        await _dbContext.SaveChangesAsync();

        var result = await _service.SelectRouteAsync("claude-4");

        result.Found.Should().BeFalse();
        result.Route.Should().BeNull();
    }

    [Fact]
    public async Task SelectRouteAsync_prefers_enabled_over_disabled()
    {
        // 同时存在启用和禁用路由时，忽略禁用路由
        var siteId = Guid.NewGuid();
        _dbContext.ProxyRouteRules.AddRange(
            new ProxyRouteRule { ExternalModelName = "llama-4", UpstreamModelName = "llama-4", SiteId = siteId, SiteModelName = "llama-4-high", Priority = 1, ModelPriority = 1, InstancePriority = 0, IsEnabled = false },
            new ProxyRouteRule { ExternalModelName = "llama-4", UpstreamModelName = "llama-4", SiteId = siteId, SiteModelName = "llama-4-low", Priority = 5, ModelPriority = 5, InstancePriority = 0, IsEnabled = true }
        );
        await _dbContext.SaveChangesAsync();

        var result = await _service.SelectRouteAsync("llama-4");

        result.Found.Should().BeTrue();
        result.Route!.SiteModelName.Should().Be("llama-4-low");
    }

    [Fact]
    public async Task SelectAllRoutesAsync_returns_grouped_then_global_priority_order()
    {
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var siteC = Guid.NewGuid();
        var siteD = Guid.NewGuid();

        _dbContext.ProxyRouteRules.AddRange(
            new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "gpt-5.5", SiteId = siteA, SiteModelName = "gpt-5.5-a", Priority = 0, ModelPriority = 0, InstancePriority = 0, IsEnabled = true },
            new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "gpt-5.5", SiteId = siteB, SiteModelName = "gpt-5.5-b", Priority = 1, ModelPriority = 0, InstancePriority = 1, IsEnabled = true },
            new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "glm-5.1", SiteId = siteC, SiteModelName = "glm-5.1-a", Priority = 2, ModelPriority = 1, InstancePriority = 0, IsEnabled = true },
            new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "glm-5.1", SiteId = siteD, SiteModelName = "glm-5.1-b", Priority = 3, ModelPriority = 1, InstancePriority = 1, IsEnabled = true }
        );
        await _dbContext.SaveChangesAsync();

        var routes = await _service.SelectAllRoutesAsync("chat-prod");

        routes.Select(r => r.Route!.SiteModelName).Should().ContainInOrder(
            "gpt-5.5-a",
            "gpt-5.5-b",
            "glm-5.1-a",
            "glm-5.1-b");
    }

    [Fact]
    public async Task SelectRouteAsync_returns_not_found_for_unknown_model()
    {
        // 不存在的模型名称应返回空结果
        var result = await _service.SelectRouteAsync("nonexistent");

        result.Found.Should().BeFalse();
        result.Route.Should().BeNull();
    }

    public void Dispose() => _dbContext.Dispose();
}

public sealed class ProxyForwardServiceTests
{
    // 单路由失败后应按配置重试，并在后续成功时返回成功结果
    [Fact]
    public async Task ForwardAsync_retries_before_returning_success()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"first\"}")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "OpenAI",
            TargetModelName = "gpt-5.5-a",
            RequestBody = "{\"model\":\"chat-prod\"}",
            RetryCount = 1,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        handler.CallCount.Should().Be(2);
        result.InputTokens.Should().Be(1);
        result.OutputTokens.Should().Be(2);
    }

    // HTTP 成功但响应体为空时，应视为失败并继续后续重试
    [Fact]
    public async Task ForwardAsync_treats_empty_response_body_as_failure_and_retries_next_attempt()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "OpenAI",
            TargetModelName = "gpt-5.5-a",
            RequestBody = "{\"model\":\"chat-prod\"}",
            RetryCount = 1,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        handler.CallCount.Should().Be(2);
        result.ResponseBody.Should().Contain("\"content\":\"ok\"");
        result.InputTokens.Should().Be(3);
        result.OutputTokens.Should().Be(4);
    }
    // Anthropic 原协议流在收到 message_stop 时应视为正常结束，不能误判为中断。
    [Fact]
    public async Task ForwardAsync_treats_anthropic_stream_with_message_stop_as_completed()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event: message_start\n" +
                                            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":12}}}\n\n" +
                                            "event: content_block_delta\n" +
                                            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\n" +
                                            "event: message_delta\n" +
                                            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":3},\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
                                            "event: message_stop\n" +
                                            "data: {\"type\":\"message_stop\"}\n\n")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "Anthropic",
            TargetModelName = "claude-sonnet",
            RequestBody = "{\"model\":\"chat-prod\",\"stream\":true}",
            EnableStreaming = true,
            RetryCount = 0,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        result.IsStreaming.Should().BeTrue();
        result.IsStreamInterrupted.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
        result.InputTokens.Should().Be(12);
        result.OutputTokens.Should().Be(3);
    }

    // OpenAI 原协议流缺少 DONE 时仍应保留中断标记，避免误吞真实异常。
    [Fact]
    public async Task ForwardAsync_keeps_openai_stream_without_done_as_interrupted()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "OpenAI",
            TargetModelName = "gpt-5.5-a",
            RequestBody = "{\"model\":\"chat-prod\",\"stream\":true}",
            EnableStreaming = true,
            RetryCount = 0,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        result.IsStreaming.Should().BeTrue();
        result.IsStreamInterrupted.Should().BeTrue();
        result.ErrorMessage.Should().Be("stream interrupted before normal completion");
    }
}

public sealed class SequenceHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;

    public SequenceHandler(params HttpResponseMessage[] responses)
    {
        _responses = new Queue<HttpResponseMessage>(responses);
    }

    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No more responses configured.");
        }

        return Task.FromResult(_responses.Dequeue());
    }
}
