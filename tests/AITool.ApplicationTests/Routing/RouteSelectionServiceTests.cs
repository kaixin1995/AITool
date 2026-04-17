using AITool.Application.Routing;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
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
            new ProxyRouteRule { ExternalModelName = "gpt-5", SiteId = siteId, SiteModelName = "gpt-5.4", Priority = 10, IsEnabled = true },
            new ProxyRouteRule { ExternalModelName = "gpt-5", SiteId = siteId, SiteModelName = "gpt-5-turbo", Priority = 2, IsEnabled = true },
            new ProxyRouteRule { ExternalModelName = "gpt-5", SiteId = siteId, SiteModelName = "gpt-5-pro", Priority = 5, IsEnabled = true }
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
            new ProxyRouteRule { ExternalModelName = "claude-4", SiteId = Guid.NewGuid(), SiteModelName = "claude-4-opus", Priority = 1, IsEnabled = false }
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
            new ProxyRouteRule { ExternalModelName = "llama-4", SiteId = siteId, SiteModelName = "llama-4-high", Priority = 1, IsEnabled = false },
            new ProxyRouteRule { ExternalModelName = "llama-4", SiteId = siteId, SiteModelName = "llama-4-low", Priority = 5, IsEnabled = true }
        );
        await _dbContext.SaveChangesAsync();

        var result = await _service.SelectRouteAsync("llama-4");

        result.Found.Should().BeTrue();
        result.Route!.SiteModelName.Should().Be("llama-4-low");
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
