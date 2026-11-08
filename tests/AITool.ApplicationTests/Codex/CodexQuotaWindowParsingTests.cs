using AITool.Infrastructure.Codex;
using FluentAssertions;

namespace AITool.ApplicationTests.Codex;

public sealed class CodexQuotaWindowParsingTests
{
    [Fact]
    public void Parses_both_known_windows_even_when_reversed()
    {
        const string json = """{"rate_limit":{"primary_window":{"limit_window_seconds":604800,"used_percent":70},"secondary_window":{"limit_window_seconds":18000,"used_percent":20}}}""";
        var (_, windows) = CodexUsageParser.Parse(json);
        windows.Select(x => x.Id).Should().Equal("weekly", "five-hour");
        windows.Select(x => x.UsedPercent).Should().Equal(70, 20);
    }

    [Fact]
    public void Recognizes_four_week_window_and_keeps_unknown_duration_distinct()
    {
        const string json = """{"rate_limit":{"primary_window":{"limit_window_seconds":2419200},"secondary_window":{"limit_window_seconds":3600}}}""";
        var (_, windows) = CodexUsageParser.Parse(json);
        windows.Select(x => x.Id).Should().Equal("monthly", "window-2");
        windows[1].Label.Should().Be("其他额度窗口");
    }

    [Fact]
    public void Relative_reset_is_anchored_to_observation_time()
    {
        const string json = """{"rate_limit":{"primary_window":{"limit_window_seconds":18000,"reset_after_seconds":3600}}}""";
        var observed = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
        var (_, windows) = CodexUsageParser.Parse(json, observed);
        windows.Single().ResetAtUtc.Should().Be(observed.AddHours(1));
    }
}
