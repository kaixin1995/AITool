using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件本地 spool 选项。
/// 控制 spool 文件的存储位置和轮转/清理行为，防止磁盘空间无限增长。
/// </summary>
public sealed class CoreEventSpoolOptions
{
    /// <summary>
    /// spool 根目录。
    /// 每天的 spool 文件以 events-{yyyyMMdd}.jsonl 的格式存储在此目录下。
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// spool 文件最大保留天数。
    /// 超过此天数的旧文件会在清理时被删除，即使 Admin 尚未 ack。
    /// 这是防止磁盘空间耗尽的安全阀：当 Admin 长时间离线时，Core 不会无限积累 spool。
    /// 设为 0 表示不按天数清理（仅依赖 ack 驱动的 TrimAckedAsync）。
    /// 默认值 30 天。
    /// </summary>
    public int MaxAgeDays { get; set; } = 30;

    /// <summary>
    /// spool 文件最大保留数量。
    /// 即使所有文件都在保留天数内，如果文件总数超过此限制，最旧的文件也会被删除。
    /// 这是 MaxAgeDays 的补充保护：极端情况下单日可能产生多个文件。
    /// 设为 0 表示不限制文件数量。
    /// 默认值 60 个文件。
    /// </summary>
    public int MaxFileCount { get; set; } = 60;
}
