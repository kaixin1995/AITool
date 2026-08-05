namespace AITool.Application.UsageLogs;

/// <summary>
/// 百分位计算结果，包含有效样本数和三个常用百分位。
/// </summary>
public readonly record struct PercentileResult(double P50, double P95, double P99, int SampleCount);

/// <summary>
/// 使用 nearest-rank 规则计算非负有限数值的百分位。
/// </summary>
public static class PercentileCalculator
{
    /// <summary>
    /// 过滤负数和非有限值后，计算 P50、P95 和 P99。
    /// </summary>
    public static PercentileResult Calculate(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var validValues = values
            .Where(value => value >= 0 && double.IsFinite(value))
            .Order()
            .ToArray();

        if (validValues.Length == 0)
        {
            return new PercentileResult(0d, 0d, 0d, 0);
        }

        return new PercentileResult(
            GetNearestRank(validValues, 0.50),
            GetNearestRank(validValues, 0.95),
            GetNearestRank(validValues, 0.99),
            validValues.Length);
    }

    /// <summary>
    /// 按 nearest-rank 规则取第一个不小于百分位排名的样本。
    /// </summary>
    private static double GetNearestRank(IReadOnlyList<double> sortedValues, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sortedValues.Count);
        return sortedValues[rank - 1];
    }
}
