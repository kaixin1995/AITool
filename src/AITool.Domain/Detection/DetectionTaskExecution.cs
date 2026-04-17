namespace AITool.Domain.Detection;

// 检测任务执行记录，记录每次定时检测的执行状态
public sealed class DetectionTaskExecution
{
    // 执行记录主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 所属检测任务标识
    public Guid DetectionTaskId { get; set; }

    // 执行状态，例如 running、completed、failed
    public string Status { get; set; } = string.Empty;

    // 执行开始时间
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    // 执行结束时间
    public DateTimeOffset? FinishedAt { get; set; }

    // 执行结果摘要
    public string? Summary { get; set; }
}
