using System.Net;
using System.Net.Http.Json;
using AITool.Application.CoreRuntime;
using FluentAssertions;

namespace AITool.IntegrationTests.Core;

/// <summary>
/// 验证 Core 事件确认与状态查询的最小闭环。
/// </summary>
public sealed class CoreEventAckTests
{
    /// <summary>
    /// 当 Core 已旁路发布事件后，runtime/status 与握手应暴露最新序号；
    /// 提交 ack 后，spool backlog 应被清空。
    /// </summary>
    [Fact]
    public async Task Ack_clears_spool_backlog_and_runtime_status_exposes_latest_sequence()
    {
        await using var factory = new CoreConfigSyncWebApplicationFactory();
        using var client = factory.CreateClient();

        await factory.PublishUsageLogEventAsync();
        await Task.Delay(150);

        var runtimeStatusBefore = await client.GetAsync("/api/core/runtime/status");
        var runtimeStatusBeforeBody = await runtimeStatusBefore.Content.ReadAsStringAsync();
        runtimeStatusBefore.StatusCode.Should().Be(HttpStatusCode.OK, runtimeStatusBeforeBody);
        runtimeStatusBeforeBody.Should().Contain("\"latestSequenceId\":1");
        runtimeStatusBeforeBody.Should().Contain("\"hasSpoolBacklog\":true");

        var handshakeResponse = await client.PostAsJsonAsync("/api/core/config/handshake", new CoreAdminHandshakeRequest
        {
            AdminInstanceId = "admin-node-01",
            AdminStartedAt = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
            CurrentConfigVersion = 0,
            CurrentConfigHash = string.Empty,
            LastAckedSequenceId = 0
        });
        var handshake = await handshakeResponse.Content.ReadFromJsonAsync<CoreAdminHandshakeResponse>();
        handshake.Should().NotBeNull();
        handshake!.LatestSequenceId.Should().Be(1);
        handshake.HasSpoolBacklog.Should().BeTrue();

        var ackResponse = await client.PostAsJsonAsync("/api/core/events/ack", new CoreAdminAckRequest
        {
            AdminInstanceId = "admin-node-01",
            AckedSequenceId = 1,
            AckedAt = new DateTimeOffset(2026, 6, 10, 9, 1, 0, TimeSpan.Zero)
        });
        var ackBody = await ackResponse.Content.ReadAsStringAsync();
        ackResponse.StatusCode.Should().Be(HttpStatusCode.OK, ackBody);
        ackBody.Should().Contain("\"ackedSequenceId\":1");

        var runtimeStatusAfter = await client.GetAsync("/api/core/runtime/status");
        var runtimeStatusAfterBody = await runtimeStatusAfter.Content.ReadAsStringAsync();
        runtimeStatusAfter.StatusCode.Should().Be(HttpStatusCode.OK, runtimeStatusAfterBody);
        runtimeStatusAfterBody.Should().Contain("\"hasSpoolBacklog\":false");
    }
}
