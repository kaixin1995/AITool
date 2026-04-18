namespace AITool.Domain.Models;

// 模型健康监控配置，记录用户选择监控的模型
public sealed class ModelHealthMonitor
{
    // 主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 关联的模型库项 ID
    public Guid ModelLibraryItemId { get; set; }

    // 创建时间
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
