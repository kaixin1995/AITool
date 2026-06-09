using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 验证独立 Admin 宿主中的 UsageLogs 页面最小迁移是否已经成立。
/// 当前阶段除了页面骨架外，还要确认只读 API 与页面使用的最小数据联动已经可用。
/// </summary>
public sealed class UsageLogsPageSmokeTests
{
    /// <summary>
    /// 独立 Admin 宿主应能访问 /Admin/UsageLogs，并返回基础页面骨架。
    /// </summary>
    [Fact]
    public async Task Usage_logs_page_is_available_in_independent_admin_host()
    {
        await using var factory = new AdminHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/UsageLogs");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, html);
        html.Should().Contain("调用日志");
        html.Should().Contain("筛选与汇总");
        html.Should().Contain("总请求");
        html.Should().Contain("查看链路");
    }

    /// <summary>
    /// 独立 Admin 宿主中的 UsageLogs 只读接口应能返回最小可用数据，证明页面不只是静态骨架。
    /// </summary>
    [Fact]
    public async Task Usage_logs_api_returns_summary_list_and_request_detail_in_independent_admin_host()
    {
        await using var factory = new AdminHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var listResponse = await client.GetAsync("/api/admin/usage-logs/list?rangeType=all");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        listResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, listBody);
        using var listDocument = JsonDocument.Parse(listBody);
        listDocument.RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);

        var summaryResponse = await client.GetAsync("/api/admin/usage-logs/summary?rangeType=all");
        var summaryBody = await summaryResponse.Content.ReadAsStringAsync();
        summaryResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, summaryBody);
        using var summaryDocument = JsonDocument.Parse(summaryBody);
        summaryDocument.RootElement.GetProperty("totalRequests").GetInt32().Should().BeGreaterThan(0);

        var requestId = listDocument.RootElement.GetProperty("items")[0].GetProperty("requestId").GetGuid();
        var detailResponse = await client.GetAsync($"/api/admin/usage-logs/request-detail/{requestId}");
        var detailBody = await detailResponse.Content.ReadAsStringAsync();
        detailResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, detailBody);
        using var detailDocument = JsonDocument.Parse(detailBody);
        detailDocument.RootElement.GetProperty("requestId").GetGuid().Should().Be(requestId);
        detailDocument.RootElement.GetProperty("attempts").GetArrayLength().Should().BeGreaterThan(0);
    }
}
