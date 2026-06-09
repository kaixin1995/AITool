namespace AITool.Application.CoreRuntime;

/// <summary>
/// Admin 对 Core 事件的确认信息。
/// 当前阶段先把最小 ack 模型固化下来，后续 replay 与删除 spool 文件都基于这个边界推进。
/// </summary>
public sealed class CoreAdminAckRequest
{
    /// <summary>
    /// Admin 实例标识。
    /// </summary>
    public string AdminInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 当前已成功落库并确认的最大连续事件序号。
    /// </summary>
    public long AckedSequenceId { get; set; }

    /// <summary>
    /// 确认时间。
    /// </summary>
    public DateTimeOffset AckedAt { get; set; } = DateTimeOffset.UtcNow;
}
