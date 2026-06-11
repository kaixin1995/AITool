using System.Net;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AITool.IntegrationTests.Core;

/// <summary>
/// 验证 Admin 最小 Core 客户端能完成握手、全量同步、ack 与 replay 调用。
/// </summary>
public sealed class CoreAdminClientTests
{
    /// <summary>
    /// 最小客户端应能顺序完成：
    /// 握手 → full sync → replay → ack。
    /// </summary>
    [Fact]
    public async Task Client_can_drive_handshake_full_sync_replay_and_ack_flow()
    {
        await using var factory = new CoreConfigSyncWebApplicationFactory();
        using var httpClient = factory.CreateClient();
        var client = new CoreAdminClient(httpClient);

        var handshakeBefore = await client.HandshakeAsync(new CoreAdminHandshakeRequest
        {
            AdminInstanceId = "admin-node-01",
            AdminStartedAt = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
            CurrentConfigVersion = 1,
            CurrentConfigHash = "sha256:pending",
            LastAckedSequenceId = 0
        });
        handshakeBefore.ConfigSyncDecision.Should().Be("full-sync-required");
        handshakeBefore.Ready.Should().BeFalse();

        var snapshot = await factory.BuildSnapshotAsync(1);
        var syncResult = await client.FullSyncAsync(snapshot);
        syncResult.Applied.Should().BeTrue();
        syncResult.Ignored.Should().BeFalse();

        await factory.PublishUsageLogEventAsync();
        await Task.Delay(150);

        var replay = await client.ReplayAsync(0);
        replay.Should().HaveCount(2);
        replay[0].EventType.Should().Be("config-applied");
        replay[1].EventType.Should().Be("usage-log");

        var ackResult = await client.AckAsync(new CoreAdminAckRequest
        {
            AdminInstanceId = "admin-node-01",
            AckedSequenceId = replay[1].SequenceId,
            AckedAt = new DateTimeOffset(2026, 6, 10, 9, 1, 0, TimeSpan.Zero)
        });
        ackResult.AckedSequenceId.Should().Be(replay[1].SequenceId);

        var handshakeAfter = await client.HandshakeAsync(new CoreAdminHandshakeRequest
        {
            AdminInstanceId = "admin-node-01",
            AdminStartedAt = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
            CurrentConfigVersion = 1,
            CurrentConfigHash = snapshot.ConfigHash,
            LastAckedSequenceId = ackResult.AckedSequenceId
        });
        handshakeAfter.ConfigSyncDecision.Should().Be("noop");
        handshakeAfter.HasSpoolBacklog.Should().BeFalse();
        handshakeAfter.Ready.Should().BeTrue();
    }
}
