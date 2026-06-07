using AITool.Domain.Proxy;

namespace AITool.Application.Conversations;

/// <summary>
/// 对话记录底层存储接口，统一负责批量写入、查询、删除与标题更新。
/// </summary>
public interface IConversationLogStore
{
    /// <summary>
    /// 批量追加对话记录。
    /// </summary>
    Task AppendBatchAsync(IReadOnlyList<ConversationTurnLog> logs, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件查询对话记录。
    /// </summary>
    Task<IReadOnlyList<ConversationTurnLog>> QueryAsync(ConversationLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除某个会话下的全部记录。
    /// </summary>
    Task<int> DeleteSessionAsync(string groupKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新某个会话的自定义标题；传空值时恢复默认标题。
    /// </summary>
    Task<int> UpdateSessionTitleAsync(string groupKey, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理超过保留期的本地对话记录。
    /// </summary>
    Task PruneExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 对话记录查询条件。
/// </summary>
public sealed class ConversationLogQuery
{
    /// <summary>
    /// 查询起始时间（含）。
    /// </summary>
    public DateTimeOffset StartTime { get; set; }

    /// <summary>
    /// 查询结束时间（不含）。
    /// </summary>
    public DateTimeOffset EndTime { get; set; }

    /// <summary>
    /// 来源工具筛选。
    /// </summary>
    public string SourceTool { get; set; } = string.Empty;

    /// <summary>
    /// 路由入口筛选。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 会话关键字筛选。
    /// </summary>
    public string SessionKeyword { get; set; } = string.Empty;

    /// <summary>
    /// 会话分组键筛选。
    /// </summary>
    public string GroupKey { get; set; } = string.Empty;
}
