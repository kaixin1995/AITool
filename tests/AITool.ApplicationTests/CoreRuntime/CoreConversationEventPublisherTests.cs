using System.Text.Json;
using AITool.Application.Conversations;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证对话记录事件发布器是否能把结构化会话数据投影成 Core 事件。
/// </summary>
public sealed class CoreConversationEventPublisherTests
{
    /// <summary>
    /// 发布对话记录后，应生成 conversation-turn 事件，并保留会话相关关键字段。
    /// </summary>
    [Fact]
    public async Task PublishAsync_projects_conversation_turn_into_event_envelope()
    {
        var sequenceProvider = new CoreEventSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreConversationEventPublisher(sequenceProvider, eventBus);
        var entry = new ConversationTurnEntry
        {
            RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            CreatedAt = new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero),
            UserCreatedAt = new DateTimeOffset(2026, 6, 10, 10, 59, 58, TimeSpan.Zero),
            SourceTool = "claude-code",
            SessionId = "session-123",
            ConversationGroupKey = "claude-code:session-123",
            AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            RequestModel = "claude-sonnet-4-6",
            ProtocolType = "OpenAI",
            RequestPath = "/v1/responses",
            Source = "claude-code",
            UserInputText = "请帮我检查当前改动",
            AssistantOutputMarkdown = "先看 diff，再给你结论",
            InputTokens = 120,
            CachedTokens = 20,
            OutputTokens = 80,
            IsStreaming = true,
            Status = "success",
            MetadataJson = "{}",
            ConversationTitle = "示例会话"
        };

        await publisher.PublishAsync(entry);

        var envelope = await eventBus.Reader.ReadAsync();
        envelope.SequenceId.Should().Be(1);
        envelope.EventType.Should().Be("conversation-turn");
        envelope.OccurredAt.Should().Be(entry.CreatedAt);

        var payload = JsonSerializer.Deserialize<CoreConversationTurnEvent>(envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.RequestId.Should().Be(entry.RequestId);
        payload.SourceTool.Should().Be(entry.SourceTool);
        payload.SessionId.Should().Be(entry.SessionId);
        payload.ConversationGroupKey.Should().Be(entry.ConversationGroupKey);
        payload.UserInputText.Should().Be(entry.UserInputText);
        payload.AssistantOutputMarkdown.Should().Be(entry.AssistantOutputMarkdown);
        payload.ConversationTitle.Should().Be(entry.ConversationTitle);
    }
}
