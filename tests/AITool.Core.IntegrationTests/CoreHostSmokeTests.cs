using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 验证独立 Core 宿主至少可以正常启动并暴露最小 HTTP 管线。
/// Core 宿主不依赖数据库，所以测试工厂只需配置 Testing 环境。
/// </summary>
public sealed class CoreHostSmokeTests
{
    /// <summary>
    /// 独立 Core 宿主应能成功启动，并返回一个非 500 的基础响应。
    /// 使用不存在的路径做最小 smoke check，不依赖真实代理功能。
    /// </summary>
    [Fact]
    public async Task Core_host_starts_successfully()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/not-found-smoke-check");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Core 宿主的 /health 端点应返回 200 OK 和 ok 状态。
    /// </summary>
    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ok");
    }

    /// <summary>
    /// Core 宿主应暴露 /api/core/health 端点（CoreRuntimeStatusController）。
    /// </summary>
    [Fact]
    public async Task Core_health_endpoint_is_available()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/health");
        // 无论返回什么状态码，只要不是 404 就说明端点已注册。
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Core 宿主测试工厂，使用 Testing 环境避免加载生产配置。
/// Core 宿主不依赖数据库，所以不需要替换 DbContext。
/// </summary>
internal sealed class CoreHostWebApplicationFactory : WebApplicationFactory<AITool.Core.CoreProgramMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
