namespace AITool.Domain.Detection;

/// <summary>
/// 表示检测任务的一次实际执行记录，用于追踪任务运行过程、结果状态以及执行时间范围。
/// </summary>
public sealed class DetectionTaskExecution
{
    /// <summary>
    /// 执行记录唯一标识，用于区分同一任务的不同运行实例。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 所属检测任务标识，用于将执行结果回溯到具体的任务配置。
    /// </summary>
    public Guid DetectionTaskId { get; set; }

    /// <summary>
    /// 当前执行状态，例如 running、completed、failed，用于表示本次任务所处阶段或最终结果。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 本次执行的开始时间，通常在任务被调度启动时写入。
    /// </summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 本次执行的结束时间；任务仍在运行时保持为空，便于区分未完成记录。
    /// </summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// 本次执行的结果摘要，用于保存简要说明、错误概览或统计信息。
    /// </summary>
    public string? Summary { get; set; }
}
