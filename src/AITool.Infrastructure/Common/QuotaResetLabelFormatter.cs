namespace AITool.Infrastructure.Common;

public static class QuotaResetLabelFormatter
{
    public static string Format(TimeSpan remaining)
    {
        if (remaining.TotalSeconds <= 0)
        {
            return "已重置";
        }

        var totalMinutes = Math.Max(1, (long)Math.Floor(remaining.TotalMinutes));
        var months = totalMinutes / (30L * 24 * 60);
        totalMinutes %= 30L * 24 * 60;
        var weeks = totalMinutes / (7L * 24 * 60);
        totalMinutes %= 7L * 24 * 60;
        var days = totalMinutes / (24L * 60);
        totalMinutes %= 24L * 60;
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        var parts = new List<string>();
        if (months > 0) parts.Add($"{months}个月");
        if (weeks > 0) parts.Add($"{weeks}周");
        if (days > 0) parts.Add($"{days}天");
        if (hours > 0) parts.Add($"{hours}小时");
        if (minutes > 0) parts.Add($"{minutes}分");

        return parts.Count > 0 ? string.Join("", parts) + "后重置" : "<1分钟后重置";
    }
}
