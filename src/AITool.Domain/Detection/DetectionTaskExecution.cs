using SqlSugar;

namespace AITool.Domain.Detection;

/// <summary>
/// 表示一次检测任务的执行记录，用于保留某个检测任务在某次调度周期内的执行过程与结果。
/// </summary>
[SugarTable("DetectionTaskExecutions")]
[SugarIndex("IX_DetectionTaskExecutions_StartedAt", nameof(StartedAt), OrderByType.Asc)]
public sealed class DetectionTaskExecution
{
    /// <summary>
    /// 执行记录唯一标识，用于区分每一次独立的检测任务执行。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 关联的检测任务标识，用于指明该执行记录属于哪个检测任务。
    /// </summary>
    public Guid DetectionTaskId { get; set; }

    /// <summary>
    /// 本次执行的状态，用于描述成功、失败或其他阶段性结果。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 本次执行的汇总信息，用于记录检测到的模型可用性摘要或异常说明。
    /// </summary>
    [SugarColumn(Length = 2000, IsNullable = false)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 本次执行开始时间，用于记录检测任务何时开始运行。
    /// </summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 本次执行结束时间，用于记录检测任务何时完成；运行中或异常中断时可能为空。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? FinishedAt { get; set; }
}
