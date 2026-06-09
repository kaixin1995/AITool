using System.Net;
using System.Net.Http.Json;
using AITool.Application.CoreRuntime;
using FluentAssertions;

namespace AITool.IntegrationTests.Core;

/// <summary>
/// 验证 Core 与 Admin 的最小握手闭环。
/// </summary>
public sealed class CoreConfigHandshakeTests
{
    /// <summary>
    /// 当 Core 尚未加载配置时，握手应要求 full sync；
    /// 当配置已同步且版本、哈希一致时，应返回 noop。
    /// </summary>
    [Fact]
    public async Task Handshake_returns_expected_sync_decision_before_and_after_full_sync()
    {
        await using var factory = new CoreConfigSyncWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new CoreAdminHandshakeRequest
        {
            AdminInstanceId = "admin-node-01",
            AdminStartedAt = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
            CurrentConfigVersion = 1,
            CurrentConfigHash = "sha256:pending",
            LastAckedSequenceId = 0
        };

        var beforeResponse = await client.PostAsJsonAsync("/api/core/config/handshake", request);
        var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK, beforeBody);
        beforeBody.Should().Contain("full-sync-required");
        beforeBody.Should().Contain("\"ready\":false");

        var snapshot = await factory.BuildSnapshotAsync(1);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        request.CurrentConfigHash = snapshot.ConfigHash;
        var afterResponse = await client.PostAsJsonAsync("/api/core/config/handshake", request);
        var after = await afterResponse.Content.ReadFromJsonAsync<CoreAdminHandshakeResponse>();

        afterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        after.Should().NotBeNull();
        after!.Ready.Should().BeTrue();
        after.ConfigSyncDecision.Should().Be("noop");
        after.AppliedConfigVersion.Should().Be(1);
        after.AppliedConfigHash.Should().Be(snapshot.ConfigHash);
    }
}
