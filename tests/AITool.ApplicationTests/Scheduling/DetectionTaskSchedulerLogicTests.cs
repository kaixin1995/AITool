using AITool.Infrastructure.Scheduling;
using FluentAssertions;

namespace AITool.ApplicationTests.Scheduling;

/// <summary>
/// 检测任务秒级调度的纯逻辑测试：旧 Cron 迁移解析 + 抖动边界。
/// </summary>
public sealed class DetectionTaskSchedulerLogicTests
{
    [Theory]
    [InlineData("*/1 * * * *", 60)]
    [InlineData("*/5 * * * *", 300)]
    [InlineData("*/30 * * * *", 1800)]
    [InlineData("*/60 * * * *", 3600)]
    public void ParseLegacyCron_minute_step_formats_convert_to_seconds(string cron, int expectedSeconds)
    {
        var result = DetectionTaskSchedulerService.ParseLegacyCronToSeconds(cron);
        result.Should().Be(expectedSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0 3 * * *")]
    [InlineData("* * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/abc * * * *")]
    [InlineData("interval:60s")]
    public void ParseLegacyCron_unsupported_or_empty_formats_return_null(string? cron)
    {
        var result = DetectionTaskSchedulerService.ParseLegacyCronToSeconds(cron);
        result.Should().BeNull();
    }

    [Fact]
    public void ComputeJitteredDelay_never_below_minimum_interval()
    {
        // 10 秒间隔 + 抖动：多次抽样，结果永远不低于 10 秒硬下限。
        for (var i = 0; i < 200; i++)
        {
            var delay = DetectionTaskSchedulerService.ComputeJitteredDelay(TimeSpan.FromSeconds(10));
            delay.TotalSeconds.Should().BeGreaterThanOrEqualTo(10);
            delay.TotalSeconds.Should().BeLessThanOrEqualTo(13, "10s 的 ±20%（至少±3s）抖动上限为 13s");
        }
    }

    [Fact]
    public void ComputeJitteredDelay_applies_both_directions_for_longer_intervals()
    {
        // 60 秒间隔：抖动 ±12 秒，正负两个方向都应出现（证明抖动真的左右摆动而非单向）。
        var sawAbove = false;
        var sawBelow = false;
        for (var i = 0; i < 500; i++)
        {
            var delay = DetectionTaskSchedulerService.ComputeJitteredDelay(TimeSpan.FromSeconds(60)).TotalSeconds;
            delay.Should().BeGreaterThanOrEqualTo(10);
            if (delay > 60) sawAbove = true;
            if (delay < 60) sawBelow = true;
        }

        sawAbove.Should().BeTrue("应观察到向上抖动的样本");
        sawBelow.Should().BeTrue("应观察到向下抖动的样本");
    }
}
