using System.Text.Json;
using AITool.Application.Conversations;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 对话记录 → Core 事件发布器。
/// 当前阶段先把会话记录链路接入最小事件总线，后续再补 ack、replay 与 spool。
/// </summary>
public sealed class CoreConversationEventPublisher
{
    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly CoreAdminEventBus _eventBus;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// 初始化对话记录事件发布器。
    /// </summary>
    public CoreConversationEventPublisher(
        CoreEventSequenceProvider sequenceProvider,
        CoreAdminEventBus eventBus)
    {
        _sequenceProvider = sequenceProvider;
        _eventBus = eventBus;
    }

    /// <summary>
    /// 把一条对话记录投影成 Core 事件并发布到总线。
    /// </summary>
    public async Task PublishAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var envelope = new CoreAdminEventEnvelope
        {
            SequenceId = _sequenceProvider.Next(),
            EventType = "conversation-turn",
            OccurredAt = entry.CreatedAt,
            PayloadJson = JsonSerializer.Serialize(new CoreConversationTurnEvent
            {
                RequestId = entry.RequestId,
                CreatedAt = entry.CreatedAt,
                UserCreatedAt = entry.UserCreatedAt,
                SourceTool = entry.SourceTool,
                SessionId = entry.SessionId,
                ConversationGroupKey = entry.ConversationGroupKey,
                AccessKeyId = entry.AccessKeyId,
                RequestModel = entry.RequestModel,
                ProtocolType = entry.ProtocolType,
                RequestPath = entry.RequestPath,
                Source = entry.Source,
                UserInputText = entry.UserInputText,
                AssistantOutputMarkdown = entry.AssistantOutputMarkdown,
                InputTokens = entry.InputTokens,
                CachedTokens = entry.CachedTokens,
                OutputTokens = entry.OutputTokens,
                IsStreaming = entry.IsStreaming,
                Status = entry.Status,
                MetadataJson = entry.MetadataJson,
                ConversationTitle = entry.ConversationTitle
            }, SerializerOptions)
        };

        await _eventBus.PublishAsync(envelope, cancellationToken);
    }
}
