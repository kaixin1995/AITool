using AITool.Application.UsageLogs;
using FluentAssertions;

namespace AITool.ApplicationTests.UsageLogs;

/// <summary>
/// 验证使用日志百分位计算的过滤规则、nearest-rank 规则和空数据行为。
/// </summary>
public sealed class PercentileCalculatorTests
{
    /// <summary>
    /// 应过滤负数和非有限值，并按 nearest-rank 计算各百分位。
    /// </summary>
    [Fact]
    public void Calculate_filters_invalid_values_and_uses_nearest_rank()
    {
        var values = new[] { 5d, double.NaN, 1d, double.PositiveInfinity, -1d, 4d, 3d, 2d, double.NegativeInfinity };

        var result = PercentileCalculator.Calculate(values);

        result.P50.Should().Be(3d);
        result.P95.Should().Be(5d);
        result.P99.Should().Be(5d);
        result.SampleCount.Should().Be(5);
    }

    /// <summary>
    /// 空集合时，所有百分位和样本数都应为零。
    /// </summary>
    [Fact]
    public void Calculate_returns_zero_for_empty_values()
    {
        var result = PercentileCalculator.Calculate(Array.Empty<double>());

        result.P50.Should().Be(0d);
        result.P95.Should().Be(0d);
        result.P99.Should().Be(0d);
        result.SampleCount.Should().Be(0);
    }

    /// <summary>
    /// 集合中的值全部无效时，所有百分位和样本数都应为零。
    /// </summary>
    [Fact]
    public void Calculate_returns_zero_when_all_values_are_invalid()
    {
        var result = PercentileCalculator.Calculate(new[]
        {
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            -1d
        });

        result.P50.Should().Be(0d);
        result.P95.Should().Be(0d);
        result.P99.Should().Be(0d);
        result.SampleCount.Should().Be(0);
    }

    /// <summary>
    /// 单个有效样本应同时作为三个百分位的结果。
    /// </summary>
    [Fact]
    public void Calculate_returns_single_value_for_all_percentiles()
    {
        var result = PercentileCalculator.Calculate(new[] { 42d });

        result.P50.Should().Be(42d);
        result.P95.Should().Be(42d);
        result.P99.Should().Be(42d);
        result.SampleCount.Should().Be(1);
    }
}
