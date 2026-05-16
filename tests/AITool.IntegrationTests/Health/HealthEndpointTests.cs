using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AITool.IntegrationTests.Health;

/// <summary>
/// 健康检查端点集成测试，验证宿主能正常启动
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// 保存用于访问健康检查端点的客户端。
    /// </summary>
    private readonly HttpClient _client;

    /// <summary>
    /// 创建健康检查测试要使用的客户端。
    /// </summary>
    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        // 创建测试宿主客户端
        _client = factory.CreateClient();
    }

    /// <summary>
    /// 验证健康检查端点会返回成功状态。
    /// </summary>
    [Fact]
    public async Task Get_health_returns_ok()
    {
        // 验证系统已成功启动并暴露健康检查端点
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
