namespace AITool.Application.Dashboard;

// 看板概览查询参数
public sealed class GetDashboardOverviewQuery;

// 看板概览统计结果
public sealed class DashboardOverviewResult
{
    // 启用的站点数量
    public int EnabledSiteCount { get; init; }

    // 模型总数
    public int ModelCount { get; init; }

    // 最近24小时检测次数
    public int RecentDetectionCount { get; init; }

    // 最近24小时检测成功率
    public double RecentSuccessRate { get; init; }

    // 启用的定时检测任务数
    public int EnabledTaskCount { get; init; }
}
