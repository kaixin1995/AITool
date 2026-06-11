using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 UsageLog 事件信封构造与最小事件总线行为。
/// </summary>
public sealed class CoreUsageLogEventPublisherTests
{
    /// <summary>
    /// 发布 UsageLog 后，应生成 usage-log 事件，并把关键字段序列化到事件负载中。
    /// </summary>
    [Fact]
    public async Task PublishAsync_projects_usage_log_into_event_envelope()
    {
        var sequenceProvider = TestCoreEventSequenceProvider.Create();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreUsageLogEventPublisher(sequenceProvider, eventBus);
        var entry = new UsageLogEntry
        {
            RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ProtocolType = "OpenAI",
            ForwardingMode = "direct",
            RequestModel = "chat-prod",
            AttemptedModel = "gpt-5.4",
            TargetSiteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Status = "success",
            Source = "claude-code",
            RetryCount = 1,
            AttemptIndex = 1,
            IsFinalResult = true,
            FallbackTriggered = false,
            ErrorMessage = string.Empty,
            InputTokens = 120,
            CachedTokens = 30,
            OutputTokens = 80,
            IsStreaming = true,
            IsStreamInterrupted = false,
            FirstTokenLatencyMs = 200,
            StreamDurationMs = 600,
            TotalDurationMs = 900,
            ReasoningEffort = "high",
            RequestedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)
        };

        await publisher.PublishAsync(entry);

        var envelope = await eventBus.Reader.ReadAsync();
        envelope.SequenceId.Should().Be(1);
        envelope.EventType.Should().Be("usage-log");
        envelope.OccurredAt.Should().Be(entry.RequestedAt);

        var payload = JsonSerializer.Deserialize<CoreUsageLogEvent>(envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.RequestId.Should().Be(entry.RequestId);
        payload.AccessKeyId.Should().Be(entry.AccessKeyId);
        payload.ProtocolType.Should().Be(entry.ProtocolType);
        payload.ForwardingMode.Should().Be(entry.ForwardingMode);
        payload.RequestModel.Should().Be(entry.RequestModel);
        payload.AttemptedModel.Should().Be(entry.AttemptedModel);
        payload.TargetSiteId.Should().Be(entry.TargetSiteId);
        payload.Status.Should().Be(entry.Status);
        payload.Source.Should().Be(entry.Source);
        payload.InputTokens.Should().Be(entry.InputTokens);
        payload.CachedTokens.Should().Be(entry.CachedTokens);
        payload.OutputTokens.Should().Be(entry.OutputTokens);
        payload.TotalDurationMs.Should().Be(entry.TotalDurationMs);
        payload.RequestedAt.Should().Be(entry.RequestedAt);
    }
}
