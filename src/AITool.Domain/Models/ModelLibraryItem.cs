namespace AITool.Domain.Models;

// 模型库实体，统一管理 AI 模型定义
public sealed class ModelLibraryItem
{
    // 模型主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 统一模型名
    public string ModelName { get; set; } = string.Empty;

    // 页面显示名
    public string DisplayName { get; set; } = string.Empty;

    // 模型类型，例如 chat 或 embedding
    public string ModelType { get; set; } = string.Empty;

    // 是否启用该模型
    public bool IsEnabled { get; set; } = true;

    // 创建时间
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
