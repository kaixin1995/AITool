using SqlSugar;

namespace AITool.Domain.Detection;

/// <summary>
/// 表示一条检测任务配置，用于按计划定期对指定模型发起健康探测，并汇总其可用性结果。
/// </summary>
[SugarTable("DetectionTasks")]
public sealed class DetectionTask
{
    /// <summary>
    /// 检测任务唯一标识，用于在调度、执行记录和配置管理中引用该任务。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 检测任务名称，用于在界面上展示和区分不同的检测任务。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Cron 表达式，用于描述该检测任务的调度周期与触发时间规则。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = false)]
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// 标记该检测任务当前是否启用，禁用后不会被调度器触发执行。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 关联的模型库项标识，用于指明该检测任务针对哪个模型进行探测；为空表示不限定具体模型。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public Guid? ModelLibraryItemId { get; set; }

    /// <summary>
    /// 检测任务创建时间，用于记录该任务何时被加入系统。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
