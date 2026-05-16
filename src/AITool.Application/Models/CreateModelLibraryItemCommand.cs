namespace AITool.Application.Models;

/// <summary>
/// 模型库创建命令，用于承载新增模型条目时提交的字段。
/// </summary>
public sealed class CreateModelLibraryItemCommand
{
    /// <summary>
    /// 统一模型名，作为系统内部识别和路由匹配的基础名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 页面显示名，用于后台界面展示更友好的名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 控制该模型条目创建后是否立即可用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
