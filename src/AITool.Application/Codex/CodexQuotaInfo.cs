namespace AITool.Application.Codex;

/// <summary>
/// Codex 额度查询结果。数据来自 chatgpt.com/backend-api/wham/usage。
/// 上游只暴露每个窗口的 used_percent（百分比），没有 used/limit/remaining 绝对值。
/// </summary>
public sealed class CodexQuotaInfo
{
    public bool Success { get; set; }

    /// <summary>失败原因（Success=false 时）。</summary>
    public string? Error { get; set; }

    /// <summary>订阅计划类型（从 usage 响应规范化，如 Pro/Plus/Team/Free）。</summary>
    public string? PlanType { get; set; }

    /// <summary>
    /// 额度窗口列表（5 小时限额 / 周限额 / 代码审查 / 额外）。每个窗口含 usedPercent 与重置文本。
    /// 这是前端画进度条的主要数据源。
    /// </summary>
    public List<CodexQuotaWindow> Windows { get; set; } = [];

    /// <summary>5 小时窗口已用百分比（便捷字段，从 Windows 提取，可能为 null）。</summary>
    public double? FiveHourUsedPercent { get; set; }

    /// <summary>周窗口已用百分比（便捷字段，从 Windows 提取，可能为 null）。</summary>
    public double? WeeklyUsedPercent { get; set; }

    /// <summary>原始响应 JSON（存入 LastQuotaRawJson 供面板展示/再解析）。</summary>
    public string? RawJson { get; set; }

    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 单个额度窗口（供前端进度条渲染）。
/// </summary>
public sealed class CodexQuotaWindow
{
    /// <summary>窗口标识：five-hour / weekly / code-review-five-hour / ...</summary>
    public string Id { get; set; } = "";

    /// <summary>显示标签，如「5 小时限额」「周限额」。</summary>
    public string Label { get; set; } = "";

    /// <summary>已使用百分比（0-100，可能为 null 表示无数据）。</summary>
    public double? UsedPercent { get; set; }

    /// <summary>重置时间显示文本，如「2天3小时后重置」。</summary>
    public string ResetLabel { get; set; } = "";
}
