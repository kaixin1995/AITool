namespace AITool.Domain.Models;

/// <summary>
/// 表示一条模型健康监控配置，用于记录哪些模型被纳入定期检测或状态跟踪范围。
/// </summary>
public sealed class ModelHealthMonitor
{
    /// <summary>
    /// 配置唯一标识，用于区分不同的监控记录。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 关联的模型库项标识，用于指向被监控的具体模型定义。
    /// </summary>
    public Guid ModelLibraryItemId { get; set; }

    /// <summary>
    /// 配置创建时间，用于记录该模型从何时开始被加入监控范围。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
