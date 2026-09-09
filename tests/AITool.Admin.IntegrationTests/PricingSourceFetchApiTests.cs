using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AITool.Admin.IntegrationTests.Analytics;
using FluentAssertions;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 公开价格源查询端点契约测试：空 ID 校验 + 正常请求返回结构（容忍公网不可达——
/// 服务设计为单源失败容忍、双源失败降级 success=false，均为合法 200 契约）。
/// </summary>
public sealed class PricingSourceFetchApiTests
{
    [Fact]
    public async Task Source_fetch_with_empty_ids_returns_error_contract()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/models/pricing/source-fetch", new { ids = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("没有要查询的模型 ID");
    }

    [Fact]
    public async Task Source_fetch_with_ids_returns_contract_shape()
    {
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/admin/models/pricing/source-fetch",
            new { ids = new[] { "gpt-4o", "claude-sonnet-4" } });

        // 无论公网可达与否，端点都应返回 200 + 结构化契约（success/sources 或 error）。
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Object, "ApiResponse 包装下应有 data 对象");
        var data = root.GetProperty("data");
        data.TryGetProperty("success", out _).Should().BeTrue("契约必须携带 success 字段");
    }
}