using System.Net;
using System.Net.Http.Json;
using AITool.Application.CoreRuntime;
using FluentAssertions;

namespace AITool.IntegrationTests.Core;

/// <summary>
/// 验证 Core replay 补传最小闭环。
/// </summary>
public sealed class CoreEventReplayTests
{
    /// <summary>
    /// 当 Core 已积压事件时，应能按 afterSequenceId 读取剩余事件。
    /// 这一步先把最小 replay 能力打通，后续再接入真正的 Admin 消费端。
    /// </summary>
    [Fact]
    public async Task Replay_returns_backlog_events_after_given_sequence()
    {
        await using var factory = new CoreConfigSyncWebApplicationFactory();
        using var client = factory.CreateClient();

        await factory.PublishUsageLogEventAsync();
        await factory.PublishUsageLogEventAsync();
        await Task.Delay(150);

        var replayResponse = await client.GetAsync("/api/core/events/replay?afterSequenceId=0");
        var replay = await replayResponse.Content.ReadFromJsonAsync<List<CoreAdminEventEnvelope>>();

        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Should().NotBeNull();
        replay!.Should().HaveCount(2);
        replay.Select(x => x.SequenceId).Should().Equal(1, 2);
        replay.Should().OnlyContain(x => x.EventType == "usage-log");

        var replayAfterOneResponse = await client.GetAsync("/api/core/events/replay?afterSequenceId=1");
        var replayAfterOne = await replayAfterOneResponse.Content.ReadFromJsonAsync<List<CoreAdminEventEnvelope>>();
        replayAfterOneResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        replayAfterOne.Should().NotBeNull();
        replayAfterOne!.Should().HaveCount(1);
        replayAfterOne[0].SequenceId.Should().Be(2);
    }
}
