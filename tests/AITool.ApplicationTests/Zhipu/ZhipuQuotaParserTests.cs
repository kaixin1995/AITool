using AITool.Infrastructure.Zhipu;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Zhipu;

/// <summary>
/// 智谱额度响应解析测试（GET /api/monitor/usage/quota/limit）。
/// 用例翻译自 cc-switch src-tauri/src/services/coding_plan.rs 的 zhipu 系列测试，
/// 覆盖 unit 显式分桶、兜底启发式与边界形态。
/// </summary>
public sealed class ZhipuQuotaParserTests
{
    private const long ResetSoon = 1_000_003_600_000L;
    private const long ResetLater = 1_000_018_000_000L;

    private static IReadOnlyList<SiteQuotaWindowLike> ParseWindows(string dataJson)
    {
        var body = $$"""{ "success": true, "data": {{dataJson}} }""";
        var result = ZhipuQuotaParser.Parse(body);
        result.Should().NotBeNull();
        result!.Error.Should().BeNull();
        return result.Windows.Select(w => new SiteQuotaWindowLike(
            w.Id, w.Label, w.UsedPercent, w.ResetAtUtc)).ToList();
    }

    private sealed record SiteQuotaWindowLike(string Id, string Label, double UsedPercent, DateTimeOffset? ResetAtUtc);

    [Fact]
    public void Parse_extracts_level_from_data()
    {
        var result = ZhipuQuotaParser.Parse(
            """{ "success": true, "data": { "level": "GLM Coding Plan (Lite)", "limits": [] } }""");

        result.Should().NotBeNull();
        result!.Level.Should().Be("GLM Coding Plan (Lite)");
        result.Windows.Should().BeEmpty();
    }

    [Fact]
    public void New_plan_unit_classification_beats_reset_order()
    {
        // cc-switch issue #3036 实例：每周周期末尾，周桶（42%、1 小时后重置）比 5 小时桶
        // （1%、5 小时后重置）更早重置。unit 字段优先，不能被重置时间排序标反。
        // 故意把周桶放数组前面，验证不依赖输入顺序。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "unit": 6, "number": 7, "percentage": 42.0, "nextResetTime": {{ResetSoon}} },
                { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 1.0,  "nextResetTime": {{ResetLater}} },
                { "type": "TIME_LIMIT",   "percentage": 7.0 }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].Label.Should().Be("5 小时窗口");
        windows[0].UsedPercent.Should().Be(1.0);
        windows[0].ResetAtUtc.Should().NotBeNull();
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
        windows[1].Label.Should().Be("每周额度");
        windows[1].UsedPercent.Should().Be(42.0);
    }

    [Fact]
    public void Weekly_unit_six_number_one_variant()
    {
        // z.ai 也观测过 (unit:6, number:1) 表示每周窗口（按「1 周」计），
        // 分类只看 unit，number 取值不影响。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 30.0, "nextResetTime": {{ResetSoon}} },
                { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 10.0, "nextResetTime": {{ResetLater}} }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].UsedPercent.Should().Be(10.0);
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
        windows[1].UsedPercent.Should().Be(30.0);
    }

    [Fact]
    public void Old_plan_single_tier_falls_back_to_five_hour()
    {
        // 老套餐（2026-02-12 前订阅）只回 1 条 TOKENS_LIMIT，无周限。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "percentage": 2.0, "nextResetTime": 1774967594803 },
                { "type": "TIME_LIMIT",   "percentage": 0.0 }
              ]
            }
            """);

        windows.Should().HaveCount(1);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].UsedPercent.Should().Be(2.0);
    }

    [Fact]
    public void No_token_limits_returns_empty()
    {
        var windows = ParseWindows("""{ "limits": [ { "type": "TIME_LIMIT", "percentage": 5.0 } ] }""");
        windows.Should().BeEmpty();
    }

    [Fact]
    public void Missing_reset_time_is_five_hour_when_weekly_has_reset()
    {
        // 真实反馈：5 小时桶 0% 时可能没有 nextResetTime；每周桶带 reset。
        // 兜底启发式不能按 reset 升序把每周桶误判为 5 小时桶。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "percentage": 25.0, "nextResetTime": {{ResetLater}} },
                { "type": "TOKENS_LIMIT", "percentage": 0.0 }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].UsedPercent.Should().Be(0.0);
        windows[0].ResetAtUtc.Should().BeNull();
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
        windows[1].UsedPercent.Should().Be(25.0);
        windows[1].ResetAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Type_is_case_insensitive()
    {
        // 防御性：上游若把 TOKENS_LIMIT 改成小写（仅大小写变化）仍能识别。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "tokens_limit", "percentage": 12.0, "nextResetTime": {{ResetSoon}} },
                { "type": "Tokens_Limit", "percentage": 34.0, "nextResetTime": {{ResetLater}} }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].UsedPercent.Should().Be(12.0);
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
        windows[1].UsedPercent.Should().Be(34.0);
    }

    [Fact]
    public void Credit_limit_type_is_also_recognized()
    {
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "CREDIT_LIMIT", "unit": 3, "percentage": 12.0, "nextResetTime": {{ResetSoon}} },
                { "type": "CREDIT_LIMIT", "unit": 6, "percentage": 34.0, "nextResetTime": {{ResetLater}} }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
    }

    [Fact]
    public void Invalid_percentage_falls_back_to_zero()
    {
        // percentage 为字符串或 null 时不崩溃，按 0 处理（仍展示窗口）。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "percentage": "invalid", "nextResetTime": {{ResetSoon}} },
                { "type": "TOKENS_LIMIT", "percentage": null,      "nextResetTime": {{ResetLater}} }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].UsedPercent.Should().Be(0);
        windows[1].UsedPercent.Should().Be(0);
    }

    [Fact]
    public void Numeric_string_percentage_is_parsed()
    {
        var windows = ParseWindows("""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "percentage": "42", "nextResetTime": 1000003600000 }
              ]
            }
            """);

        windows.Should().HaveCount(1);
        windows[0].UsedPercent.Should().Be(42);
    }

    [Fact]
    public void Partial_unit_fields_fill_remaining_slot()
    {
        // 只有周桶带 unit 时，缺 unit 的另一条应填入剩下的 5 小时槽位，
        // 即便它的 reset 更晚——显式分类结果不受时间排序干扰。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "unit": 6, "number": 7, "percentage": 42.0, "nextResetTime": {{ResetSoon}} },
                { "type": "TOKENS_LIMIT", "percentage": 1.0, "nextResetTime": {{ResetLater}} }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].UsedPercent.Should().Be(1.0);
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
        windows[1].UsedPercent.Should().Be(42.0);
    }

    [Fact]
    public void Unknown_unit_values_fall_back_to_reset_order()
    {
        // 未识别的 unit 枚举值不猜语义，整体回落重置时间启发式
        // （reset 更早的归 5 小时桶）。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "unit": 9, "percentage": 44.0, "nextResetTime": {{ResetSoon}} },
                { "type": "TOKENS_LIMIT", "unit": 9, "percentage": 53.0, "nextResetTime": {{ResetLater}} }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].UsedPercent.Should().Be(44.0);
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
        windows[1].UsedPercent.Should().Be(53.0);
    }

    [Fact]
    public void Duplicate_unit_classification_fills_other_slot()
    {
        // 防御性：两条都标成 5 小时窗（上游异常）时，第一条占 5 小时桶，
        // 第二条降级走兜底填入每周，保证不丢数据也不抛错。
        var windows = ParseWindows($$"""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 10.0, "nextResetTime": {{ResetSoon}} },
                { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 20.0, "nextResetTime": {{ResetLater}} }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].Id.Should().Be(ZhipuQuotaParser.FiveHourWindowId);
        windows[0].UsedPercent.Should().Be(10.0);
        windows[1].Id.Should().Be(ZhipuQuotaParser.WeeklyWindowId);
        windows[1].UsedPercent.Should().Be(20.0);
    }

    [Fact]
    public void Business_error_maps_to_error_result()
    {
        var result = ZhipuQuotaParser.Parse("""{ "success": false, "msg": "令牌无效" }""");

        result.Should().NotBeNull();
        result!.Error.Should().Be("令牌无效");
        result.Windows.Should().BeEmpty();
    }

    [Fact]
    public void Business_error_without_message_uses_fallback_text()
    {
        var result = ZhipuQuotaParser.Parse("""{ "success": false }""");
        result.Should().NotBeNull();
        result!.Error.Should().Be("未知错误");
    }

    [Fact]
    public void Invalid_json_returns_null()
    {
        ZhipuQuotaParser.Parse("not json").Should().BeNull();
        ZhipuQuotaParser.Parse("").Should().BeNull();
        ZhipuQuotaParser.Parse("""{ "success": true }""").Should().BeNull();
    }

    [Fact]
    public void MatchesBaseUrl_hits_zhipu_hosts_only()
    {
        ZhipuQuotaParser.MatchesBaseUrl("https://open.bigmodel.cn/api/coding/paas/v4").Should().BeTrue();
        ZhipuQuotaParser.MatchesBaseUrl("https://BigModel.CN/api/coding/paas/v4").Should().BeTrue();
        ZhipuQuotaParser.MatchesBaseUrl("https://api.z.ai/api/v1").Should().BeTrue();
        // host 级匹配：不认路径里出现域名或伪造后缀。
        ZhipuQuotaParser.MatchesBaseUrl("https://notbigmodel.cn/v1").Should().BeFalse();
        ZhipuQuotaParser.MatchesBaseUrl("https://api.example.com/bigmodel.cn/v1").Should().BeFalse();
        ZhipuQuotaParser.MatchesBaseUrl("https://api.openai.com/v1").Should().BeFalse();
        ZhipuQuotaParser.MatchesBaseUrl("").Should().BeFalse();
    }

    [Fact]
    public void ResolveQuotaBase_routes_cn_and_global_hosts()
    {
        ZhipuQuotaParser.ResolveQuotaBase("https://open.bigmodel.cn/api/coding/paas/v4")
            .Should().Be("https://open.bigmodel.cn");
        ZhipuQuotaParser.ResolveQuotaBase("https://api.z.ai/api/v1")
            .Should().Be("https://api.z.ai");
    }

    [Fact]
    public void Non_positive_reset_time_is_treated_as_absent()
    {
        var windows = ParseWindows("""
            {
              "limits": [
                { "type": "TOKENS_LIMIT", "percentage": 10.0, "nextResetTime": 0 },
                { "type": "TOKENS_LIMIT", "percentage": 20.0, "nextResetTime": -1 }
              ]
            }
            """);

        windows.Should().HaveCount(2);
        windows[0].ResetAtUtc.Should().BeNull();
        windows[1].ResetAtUtc.Should().BeNull();
    }
}
