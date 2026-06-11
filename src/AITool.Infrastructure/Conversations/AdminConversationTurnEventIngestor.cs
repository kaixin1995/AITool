using AITool.Application.Conversations;
using AITool.Application.CoreRuntime;
using AITool.Domain.Proxy;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// Admin 侧 conversation-turn 事件消费器。
/// 从 Core 宿主的事件流中提取对话记录事件，反序列化后通过 <see cref="IConversationLogStore"/> 写入 Admin 本地存储。
/// <para>
/// 对话记录不走数据库（ProxyUsageLogs），而是走文件系统的 JSONL 存储（<see cref="IConversationLogStore"/>），
/// 因此此 Ingestor 依赖 <see cref="IConversationLogStore"/> 而非 <see cref="AppDbContext"/>。
/// </para>
/// </summary>
public sealed class AdminConversationTurnEventIngestor
{
    private readonly IConversationLogStore _logStore;
    private readonly ILogger<AdminConversationTurnEventIngestor> _logger;

    /// <summary>
    /// 初始化对话记录事件消费器。
    /// </summary>
    public AdminConversationTurnEventIngestor(
        IConversationLogStore logStore,
        ILogger<AdminConversationTurnEventIngestor> logger)
    {
        _logStore = logStore;
        _logger = logger;
    }

    /// <summary>
    /// 消费一批 Core 事件，提取 conversation-turn 类型事件并写入对话记录存储。
    /// 返回本批次中 conversation-turn 事件的最大序号；如果没有此类事件则返回 0。
    /// </summary>
    public async Task<long> IngestConversationTurnEventsAsync(
        IReadOnlyList<CoreAdminEventEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
        {
            return 0;
        }

        // 筛选 conversation-turn 类型事件并尝试反序列化
        var parsedEvents = envelopes
            .Where(x => string.Equals(x.EventType, "conversation-turn", StringComparison.Ordinal))
            .Select(x => (Envelope: x, Payload: DeserializeConversationTurn(x.PayloadJson)))
            .Where(x => x.Payload is not null)
            .ToList();

        if (parsedEvents.Count == 0)
        {
            return 0;
        }

        // 按 RequestId 去重：同一个 RequestId 的重复事件只保留最早的一条
        var deduplicated = parsedEvents
            .GroupBy(x => x.Payload!.RequestId)
            .Select(g => g.OrderBy(item => item.Envelope.SequenceId).First())
            .ToList();

        // 将事件转换为 ConversationTurnLog 并批量写入
        var logs = deduplicated.Select(x => new ConversationTurnLog
        {
            RequestId = x.Payload!.RequestId,
            CreatedAt = x.Payload.CreatedAt,
            UserCreatedAt = x.Payload.UserCreatedAt,
            SourceTool = x.Payload.SourceTool,
            SessionId = x.Payload.SessionId,
            ConversationGroupKey = x.Payload.ConversationGroupKey,
            AccessKeyId = x.Payload.AccessKeyId,
            RequestModel = x.Payload.RequestModel,
            ProtocolType = x.Payload.ProtocolType,
            RequestPath = x.Payload.RequestPath,
            Source = x.Payload.Source,
            UserInputText = x.Payload.UserInputText,
            AssistantOutputMarkdown = x.Payload.AssistantOutputMarkdown,
            InputTokens = x.Payload.InputTokens,
            CachedTokens = x.Payload.CachedTokens,
            OutputTokens = x.Payload.OutputTokens,
            IsStreaming = x.Payload.IsStreaming,
            Status = x.Payload.Status,
            MetadataJson = x.Payload.MetadataJson,
            ConversationTitle = x.Payload.ConversationTitle
        }).ToList();

        if (logs.Count > 0)
        {
            await _logStore.AppendBatchAsync(logs, cancellationToken);
            _logger.LogDebug("已消费 {Count} 条对话记录事件", logs.Count);
        }

        return parsedEvents.Max(x => x.Envelope.SequenceId);
    }

    /// <summary>
    /// 反序列化对话记录事件负载；解析失败时返回 null，由上层跳过。
    /// </summary>
    private static CoreConversationTurnEvent? DeserializeConversationTurn(string payloadJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CoreConversationTurnEvent>(
                payloadJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }
}
