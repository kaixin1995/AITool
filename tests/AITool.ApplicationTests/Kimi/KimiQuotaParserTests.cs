using AITool.Infrastructure.Kimi;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Kimi;

/// <summary>
/// Kimi 额度响应解析测试（GET /coding/v1/usages，数值为字符串）。
/// </summary>
public sealed class KimiQuotaParserTests
{
    private const string SampleJson = """
        {
          "usage": {
            "limit": "2048",
            "used": "214",
            "remaining": "1834",
            "resetTime": "2026-01-09T15:23:13.716839300Z"
          },
          "limits": [
            {
              "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
              "detail": {
                "limit": "200",
                "used": "139",
                "remaining": "61",
                "resetTime": "2026-01-06T13:33:02.717479433Z"
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parse_extracts_weekly_and_rolling_windows()
    {
        var windows = KimiQuotaParser.Parse(SampleJson);

        windows.Should().NotBeNull();
        windows.Should().HaveCount(2);

        windows![0].Id.Should().Be("weekly");
        windows[0].Label.Should().Be("周额度");
        windows[0].UsedPercent.Should().BeApproximately(214d / 2048d * 100d, 0.001d);
        windows[0].ResetAtUtc.Should().NotBeNull();

        windows[1].Id.Should().Be("window-1");
        windows[1].Label.Should().Be("5 小时窗口");
        windows[1].UsedPercent.Should().BeApproximately(139d / 200d * 100d, 0.001d);
    }

    [Fact]
    public void Parse_labels_hour_and_day_windows()
    {
        const string json = """
            {
              "usage": { "limit": "100", "used": "1" },
              "limits": [
                { "window": { "duration": 1, "timeUnit": "TIME_UNIT_HOUR" },
                  "detail": { "limit": "10", "used": "2" } },
                { "window": { "duration": 1, "timeUnit": "TIME_UNIT_DAY" },
                  "detail": { "limit": "10", "used": "0" } }
              ]
            }
            """;

        var windows = KimiQuotaParser.Parse(json);

        windows.Should().NotBeNull();
        windows.Should().HaveCount(3);
        windows![1].Label.Should().Be("1 小时窗口");
        windows[2].Label.Should().Be("24 小时窗口");
    }

    [Fact]
    public void Parse_returns_null_for_invalid_or_empty_payloads()
    {
        KimiQuotaParser.Parse("").Should().BeNull();
        KimiQuotaParser.Parse("not json").Should().BeNull();
        KimiQuotaParser.Parse("{}").Should().BeNull();
        KimiQuotaParser.Parse("""{"usage":{"limit":"0","used":"0"}}""").Should().BeNull();
    }

    [Fact]
    public void Parse_clamps_usage_over_limit()
    {
        const string json = """{"usage":{"limit":"100","used":"180"}}""";

        var windows = KimiQuotaParser.Parse(json);

        windows.Should().NotBeNull();
        windows![0].UsedPercent.Should().Be(100d);
    }
}
