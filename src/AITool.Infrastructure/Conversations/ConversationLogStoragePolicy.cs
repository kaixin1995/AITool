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
