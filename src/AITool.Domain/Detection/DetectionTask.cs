namespace AITool.Domain.Detection;

/// <summary>
/// 表示一条周期性检测任务，用于描述系统按固定时间规则执行模型检测时所需的基础配置。
/// </summary>
public sealed class DetectionTask
{
    /// <summary>
    /// 任务唯一标识，用于关联任务配置、执行记录以及外部引用。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 任务名称，用于在页面或日志中区分不同的检测任务。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Cron 表达式，用于定义任务的触发周期和执行时间。
    /// </summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// 标记任务当前是否启用，禁用后即使到达调度时间也不应执行。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 关联的模型库项标识；为空时表示任务执行时需要覆盖当前可检测的全部模型。
    /// </summary>
    public Guid? ModelLibraryItemId { get; set; }
}
