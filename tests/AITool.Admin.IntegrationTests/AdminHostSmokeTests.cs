using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 验证独立 Admin 宿主骨架至少可以正常启动并暴露最小 HTTP 管线。
/// 当前阶段不要求迁完真实 /Admin/* 页面，先确保宿主本身可独立编译和拉起。
/// </summary>
public sealed class AdminHostSmokeTests
{
    /// <summary>
    /// 独立 Admin 宿主应能成功启动，并返回一个非 500 的基础响应。
    /// 这里先用不存在的路径做最小 smoke check，避免测试依赖真实页面迁移进度。
    /// </summary>
    [Fact]
    public async Task Admin_host_starts_successfully()
    {
        await using var factory = new AdminHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/not-found-smoke-check");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

internal sealed class AdminHostWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
}
