namespace AITool.Application.Codex;

/// <summary>
/// 单个账号的巡检结果（一次巡检产出一组）。
/// </summary>
public sealed class InspectionAccountResult
{
    /// <summary>账号 Id。</summary>
    public Guid AccountId { get; set; }

    /// <summary>账号显示名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>动作：keep（保留）/ disable（禁用）/ enable（启用）。</summary>
    public string Action { get; set; } = "keep";

    /// <summary>判定原因。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>是否用了缓存（未真实刷新）。</summary>
    public bool FromCache { get; set; }

    /// <summary>周窗口已用百分比（若刷新到）。</summary>
    public double? WeeklyUsedPercent { get; set; }

    /// <summary>5 小时窗口已用百分比（若刷新到）。</summary>
    public double? FiveHourUsedPercent { get; set; }

    /// <summary>本次巡检时间。</summary>
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 一次完整巡检轮次的汇总结果。
/// </summary>
public sealed class InspectionRunResult
{
    public bool IsRunning { get; set; }

    /// <summary>本轮是否强制真实刷新（手动「真实巡检」触发）。</summary>
    public bool ForcedRefresh { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public List<InspectionAccountResult> Accounts { get; set; } = [];

    public int KeepCount => Accounts.Count(a => a.Action == "keep");
    public int DisableCount => Accounts.Count(a => a.Action == "disable");
    public int EnableCount => Accounts.Count(a => a.Action == "enable");
    public int CacheCount => Accounts.Count(a => a.FromCache);
    public int RealRefreshCount => Accounts.Count(a => !a.FromCache);

    /// <summary>巡检是否自动执行（否则为手动触发）。用于状态展示。</summary>
    public bool AutoTriggered { get; set; }
}

/// <summary>
/// 巡检操作日志条目（内存环形缓冲，供前端「运行输出」展示）。
/// </summary>
public sealed class InspectionLogEntry
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>分类：inspection / quota / account / system。</summary>
    public string Category { get; set; } = "inspection";

    /// <summary>日志内容。</summary>
    public string Message { get; set; } = string.Empty;
}
