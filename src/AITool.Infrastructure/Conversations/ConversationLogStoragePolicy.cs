namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 对话记录本地存储策略。
/// </summary>
public static class ConversationLogStoragePolicy
{
    /// <summary>
    /// 本地对话记录默认保留 60 天。
    /// </summary>
    public const int RetentionDays = 60;

    /// <summary>
    /// 单次查询允许覆盖的最大天数。
    /// </summary>
    public const int MaxQueryDays = 31;

    /// <summary>
    /// 单次查询原始记录（turn）的最大返回条数。
    /// 从最新分片文件倒序流式读取，达到上限立即停止读取后续文件，
    /// 避免把保留窗口内全部记录（含数十 KB 的对话正文）一次性物化进内存。
    /// </summary>
    public const int MaxQueryTurns = 1000;

    /// <summary>
    /// 单个会话详情查询的最大返回轮次。
    /// 单个会话累积轮次过多时只返回最近这些轮，更早的需缩小时间范围分段查看。
    /// </summary>
    public const int MaxTurnsPerSession = 500;
}

/// <summary>
/// 对话记录本地文件配置。
/// </summary>
public sealed class ConversationLogFileOptions
{
    /// <summary>
    /// 本地文件根目录。
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
}
