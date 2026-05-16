namespace AITool.Domain.Models;

/// <summary>
/// 表示模型库中的一条统一模型定义，用于在不同站点、路由和检测场景中复用同一套模型标识。
/// </summary>
public sealed class ModelLibraryItem
{
    /// <summary>
    /// 模型唯一标识，用于在系统内部稳定引用该模型定义。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 统一模型名称，作为系统内部识别模型的标准名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 页面展示名称，用于界面显示或对外说明时提供更友好的可读文本。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 标记该模型当前是否可用，关闭后通常不再参与选择、路由或检测。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 模型定义创建时间，用于保留基础数据的建立时间信息。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
