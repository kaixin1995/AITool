using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件本地 spool 选项。
/// </summary>
public sealed class CoreEventSpoolOptions
{
    /// <summary>
    /// spool 根目录。
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
}
