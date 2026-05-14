namespace AITool.Application.Models;

// 模型库创建命令
public sealed class CreateModelLibraryItemCommand
{
    // 统一模型名
    public string ModelName { get; set; } = string.Empty;

    // 页面显示名
    public string DisplayName { get; set; } = string.Empty;

    // 是否启用
    public bool IsEnabled { get; set; } = true;
}
