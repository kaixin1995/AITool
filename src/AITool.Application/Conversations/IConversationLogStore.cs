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
    /// 返回最近 <see cref="ConversationLogStoragePolicy.MaxQueryTurns"/> 条原始记录，超出部分需要分段查询。
    /// </summary>
    Task<IReadOnlyList<ConversationTurnLog>> QueryAsync(ConversationLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件流式聚合查询会话摘要（不保留整条记录，避免把全部正文一次性物化进内存）。
    /// 用于会话列表场景：每个会话只返回聚合字段 + 首条用户输入的压缩原文（由调用方按需解压）。
    /// </summary>
    Task<IReadOnlyList<ConversationSessionSummary>> QuerySessionSummariesAsync(ConversationLogQuery query, CancellationToken cancellationToken = default);

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

/// <summary>
/// 会话列表聚合摘要。流式扫描时按 <see cref="GroupKey"/> 累加得到，
/// 只保留列表展示所需字段，不物化整条对话记录，避免内存暴涨。
/// </summary>
public sealed class ConversationSessionSummary
{
    /// <summary>
    /// 会话分组键。
    /// </summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>
    /// 工具来源（取该会话最近一条记录）。
    /// </summary>
    public string SourceTool { get; set; } = string.Empty;

    /// <summary>
    /// 会话标识（取该会话最近一条记录）。
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 最近一次活动时间（会话内最大的 CreatedAt）。
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>
    /// 会话内的对话轮数。
    /// </summary>
    public int TurnCount { get; set; }

    /// <summary>
    /// 会话内全部轮次的 Token 总和（输入 + 缓存 + 输出）。
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// 自定义会话标题（若有），取该会话首个非空值。
    /// </summary>
    public string ConversationTitle { get; set; } = string.Empty;

    /// <summary>
    /// 首条非空用户输入的压缩原文（gzip: 前缀或明文，与存储一致）。
    /// 保留压缩态，由调用方按需解压取标题预览，避免聚合阶段解压全部正文。
    /// </summary>
    public string FirstUserInputTextCompressed { get; set; } = string.Empty;
}
