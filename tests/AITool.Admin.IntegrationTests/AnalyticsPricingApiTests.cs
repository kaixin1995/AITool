using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using AITool.Infrastructure.Persistence;

namespace AITool.Admin.IntegrationTests.Analytics;

/// <summary>
/// 模型价格端到端：价格表 API 读写 + 已定价模型的成本出现在统计看板与日志列表；
/// 价格保存后立即生效（改价 → 看板数字变化）。
/// </summary>
public sealed class AnalyticsPricingApiTests
{
    [Fact]
    public async Task Pricing_catalog_round_trips_and_costs_flow_into_dashboard_and_logs()
    {
        // 价格表文件位于测试宿主的共享 bin 目录：先清理，保证本测试从内置模板冷启动。
        var pricingFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model-pricing.json");
        try
        {
            File.Delete(pricingFilePath);
        }
        catch
        {
            // 文件不存在或被占用均可接受。
        }

        decimal expectedCost;
        await using var factory = new AnalyticsWebApplicationFactory();
        using var client = factory.CreateClient();

        // 1) 默认价格表可读（首次访问从模板初始化），内置条目可解析。
        var getResponse = await client.GetAsync("/api/admin/models/pricing");
        var getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);

        using var catalogDoc = JsonDocument.Parse(getBody);
        catalogDoc.RootElement.GetProperty("usdToCny").GetDecimal().Should().BeGreaterThan(0);
        catalogDoc.RootElement.GetProperty("models").GetArrayLength().Should().BeGreaterThan(50, "内置主流模型价格表");

        // 2) 为测试种子数据里的模型写入价格（final-model），保存后立即生效。
        var payload = """
        {
          "usdToCny": 6.74,
          "models": [
            { "id": "final-model", "displayName": "Final Model", "input": 2, "output": 6, "cacheRead": 0.2, "cacheWrite": 0 }
          ]
        }
        """;
        var putResponse = await client.PutAsync("/api/admin/models/pricing", new StringContent(payload, Encoding.UTF8, "application/json"));
        var putBody = await putResponse.Content.ReadAsStringAsync();
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK, putBody);

        // 3) 统计看板：总成本 = 种子 final 记录的 token 三段 × 价格（未定价的 failed-model 按 0 计）。
        //    先取种子日志的 token 数，按口径推算期望值。
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SqlSugarSetup.InitializeDatabase(db.Client);
            var finalRow = await db.ProxyUsageLogs
                .Where(x => x.AttemptedModel == "final-model" && x.IsFinalResult)
                .FirstAsync();
            finalRow.Should().NotBeNull();
            expectedCost = Math.Round(
                (finalRow.InputTokens * 2m + finalRow.CachedTokens * 0.2m + finalRow.OutputTokens * 6m) / 1_000_000m, 6);
            var expectedInputCost = Math.Round(finalRow.InputTokens * 2m / 1_000_000m, 6);
            var expectedCachedCost = Math.Round(finalRow.CachedTokens * 0.2m / 1_000_000m, 6);
            var expectedOutputCost = Math.Round(finalRow.OutputTokens * 6m / 1_000_000m, 6);

            var dashboardResponse = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all");
            var dashboardBody = await dashboardResponse.Content.ReadAsStringAsync();
            dashboardResponse.StatusCode.Should().Be(HttpStatusCode.OK, dashboardBody);

            using var dashboard = JsonDocument.Parse(dashboardBody);
            var summary = dashboard.RootElement.GetProperty("summary");
            var actualCost = summary.GetProperty("totalCostUsd").GetDecimal();
            actualCost.Should().Be(expectedCost, "查询时动态计价：已定价模型按三段价格计算，未定价模型按 0 计");

            // 模型分布带上成本维度。
            var finalPoint = dashboard.RootElement.GetProperty("modelDistribution").EnumerateArray()
                .Single(x => x.GetProperty("key").GetString() == "final-model");
            finalPoint.GetProperty("totalCostUsd").GetDecimal().Should().Be(expectedCost);

            // 站点分布与来源细分同样带成本维度（final 记录归入其站点/来源桶）。
            var finalSitePoint = dashboard.RootElement.GetProperty("siteDistribution").EnumerateArray()
                .Single(x => decimal.Round(x.GetProperty("totalCostUsd").GetDecimal(), 6) == expectedCost);
            finalSitePoint.GetProperty("key").GetString().Should().NotBeNullOrEmpty();
            dashboard.RootElement.GetProperty("sourceBreakdown").EnumerateArray()
                .Any(x => decimal.Round(x.GetProperty("totalCostUsd").GetDecimal(), 6) == expectedCost)
                .Should().BeTrue("来源细分应包含成本维度");

            // Token 趋势桶带三段成本：单桶合计 == 总成本，分段各自等于推算值。
            var trendPoints = dashboard.RootElement.GetProperty("tokenTrend").EnumerateArray().ToList();
            trendPoints.Sum(x => x.GetProperty("costUsd").GetDecimal()).Should().Be(expectedCost);
            trendPoints.Sum(x => x.GetProperty("inputCostUsd").GetDecimal()).Should().Be(expectedInputCost);
            trendPoints.Sum(x => x.GetProperty("cachedCostUsd").GetDecimal()).Should().Be(expectedCachedCost);
            trendPoints.Sum(x => x.GetProperty("outputCostUsd").GetDecimal()).Should().Be(expectedOutputCost);

            // 4) usage-logs 列表行成本：已定价行有值，未定价行为 null。
            var listResponse = await client.GetAsync("/api/admin/usage-logs/list?rangeType=all&pageSize=100");
            var listBody = await listResponse.Content.ReadAsStringAsync();
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK, listBody);

            using var list = JsonDocument.Parse(listBody);
            var items = list.RootElement.GetProperty("items").EnumerateArray().ToList();
            var finalItem = items.Single(x => x.GetProperty("attemptedModel").GetString() == "final-model");
            finalItem.GetProperty("costUsd").GetDecimal().Should().Be(expectedCost);
            var failedItem = items.Single(x => x.GetProperty("attemptedModel").GetString() == "failed-model");
            failedItem.GetProperty("costUsd").ValueKind.Should().Be(JsonValueKind.Null, "未定价模型成本为 null（前端显示为空）");

            // 5) summary 的总成本与看板一致。
            var summaryResponse = await client.GetAsync("/api/admin/usage-logs/summary?rangeType=all");
            var summaryBody = await summaryResponse.Content.ReadAsStringAsync();
            using var usageSummary = JsonDocument.Parse(summaryBody);
            usageSummary.RootElement.GetProperty("totalCostUsd").GetDecimal().Should().Be(expectedCost);
        }

        // 6) 改价立即生效：价格翻倍后看板成本翻倍（历史数据动态计价的直接验证）。
        //    统计接口带 20s 结果缓存，这里换一个筛选参数构造不同的缓存键。
        var payloadDoubled = """
        {
          "usdToCny": 6.74,
          "models": [
            { "id": "final-model", "displayName": "Final Model", "input": 4, "output": 12, "cacheRead": 0.4, "cacheWrite": 0 }
          ]
        }
        """;
        await client.PutAsync("/api/admin/models/pricing", new StringContent(payloadDoubled, Encoding.UTF8, "application/json"));

        var dashboard2Response = await client.GetAsync("/api/admin/analytics/dashboard?rangeType=all&modelName=final-model");
        var dashboard2Body = await dashboard2Response.Content.ReadAsStringAsync();
        dashboard2Response.StatusCode.Should().Be(HttpStatusCode.OK, dashboard2Body);
        using var doc2 = JsonDocument.Parse(dashboard2Body);
        doc2.RootElement.GetProperty("summary").GetProperty("totalCostUsd").GetDecimal()
            .Should().Be(expectedCost * 2, "保存价格后计价缓存立即刷新，历史统计同步更新");

        // 清理共享 bin 目录中的价格文件，避免影响其他用例的默认模板假设。
        try
        {
            File.Delete(pricingFilePath);
        }
        catch
        {
            // 忽略清理失败。
        }
    }
}
