using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 ConfigApplied 事件发布器将参数正确投影到事件信封。
/// </summary>
public sealed class CoreConfigAppliedEventPublisherTests
{
    /// <summary>
    /// 发布全量同步事件后，信封类型应为 config-applied，所有字段正确序列化。
    /// </summary>
    [Fact]
    public async Task PublishAsync_full_sync_projects_fields_into_envelope()
    {
        var sequenceProvider = TestCoreEventSequenceProvider.Create();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreConfigAppliedEventPublisher(sequenceProvider, eventBus);

        await publisher.PublishAsync(
            syncMode: "full",
            configVersion: 42,
            configHash: "hash-abc",
            previousConfigVersion: 41,
            previousConfigHash: "hash-xyz",
            changedCategories: null);

        var envelope = await eventBus.Reader.ReadAsync();
        envelope.SequenceId.Should().Be(1);
        envelope.EventType.Should().Be("config-applied");

        var payload = JsonSerializer.Deserialize<CoreConfigAppliedEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.ConfigVersion.Should().Be(42);
        payload.ConfigHash.Should().Be("hash-abc");
        payload.SyncMode.Should().Be("full");
        payload.PreviousConfigVersion.Should().Be(41);
        payload.PreviousConfigHash.Should().Be("hash-xyz");
        payload.ChangedCategories.Should().BeEmpty();
        payload.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 增量同步时 changedCategories 列表应完整保留。
    /// </summary>
    [Fact]
    public async Task PublishAsync_patch_sync_preserves_changed_categories()
    {
        var sequenceProvider = TestCoreEventSequenceProvider.Create();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreConfigAppliedEventPublisher(sequenceProvider, eventBus);

        var categories = new List<string> { "Sites", "Models", "Keys" };

        await publisher.PublishAsync(
            syncMode: "patch",
            configVersion: 100,
            configHash: "h100",
            previousConfigVersion: 99,
            previousConfigHash: "h99",
            changedCategories: categories);

        var envelope = await eventBus.Reader.ReadAsync();
        envelope.EventType.Should().Be("config-applied");

        var payload = JsonSerializer.Deserialize<CoreConfigAppliedEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.SyncMode.Should().Be("patch");
        payload.ChangedCategories.Should().BeEquivalentTo("Sites", "Models", "Keys");
    }

    /// <summary>
    /// 连续发布多条事件时，序号应递增。
    /// </summary>
    [Fact]
    public async Task PublishAsync_sequential_calls_increment_sequence()
    {
        var sequenceProvider = TestCoreEventSequenceProvider.Create();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreConfigAppliedEventPublisher(sequenceProvider, eventBus);

        await publisher.PublishAsync("full", 1, "h1", 0, "");
        await publisher.PublishAsync("patch", 2, "h2", 1, "h1", ["Sites"]);
        await publisher.PublishAsync("full", 3, "h3", 2, "h2");

        var envelope1 = await eventBus.Reader.ReadAsync();
        var envelope2 = await eventBus.Reader.ReadAsync();
        var envelope3 = await eventBus.Reader.ReadAsync();

        envelope1.SequenceId.Should().Be(1);
        envelope2.SequenceId.Should().Be(2);
        envelope3.SequenceId.Should().Be(3);
    }
}
