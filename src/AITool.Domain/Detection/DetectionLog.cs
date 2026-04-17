namespace AITool.Domain.Detection;

// 模型可用性检测日志实体，记录每次探测的结果与耗时
public sealed class DetectionLog
{
    // 检测日志主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 被检测的站点标识
    public Guid SiteId { get; set; }

    // 被检测的模型库条目标识
    public Guid ModelLibraryItemId { get; set; }

    // 检测结果状态，成功或失败
    public string Status { get; set; } = string.Empty;

    // 本次检测耗时（毫秒）
    public int DurationMs { get; set; }

    // 失败时的错误信息，成功时为空
    public string? ErrorMessage { get; set; }

    // 检测执行时间
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}
