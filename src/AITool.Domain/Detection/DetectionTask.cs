namespace AITool.Domain.Detection;

// 定时检测任务实体，定义周期性模型检测的调度规则
public sealed class DetectionTask
{
    // 任务主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 任务名称
    public string Name { get; set; } = string.Empty;

    // Cron 表达式，控制执行周期
    public string CronExpression { get; set; } = string.Empty;

    // 是否启用该任务
    public bool IsEnabled { get; set; } = true;

    // 关联的模型 ID，null 表示检测所有模型
    public Guid? ModelLibraryItemId { get; set; }
}
