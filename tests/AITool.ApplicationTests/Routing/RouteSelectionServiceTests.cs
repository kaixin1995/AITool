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
                Content = new StringContent("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient);

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
