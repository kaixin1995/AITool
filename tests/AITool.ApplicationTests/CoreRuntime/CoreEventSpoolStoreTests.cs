using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Core 事件 spool 的最小行为。
/// </summary>
public sealed class CoreEventSpoolStoreTests
{
    /// <summary>
    /// 事件写入 spool 后，应能按序读取，并在 ack 后清掉已确认数据。
    /// </summary>
    [Fact]
    public async Task Append_and_trim_ack_should_keep_only_unacked_events()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"aitool-core-spool-test-{Guid.NewGuid():N}");
        try
        {
            var store = new CoreEventSpoolStore(
                new CoreEventSpoolOptions { RootPath = rootPath },
                NullLogger<CoreEventSpoolStore>.Instance);

            await store.AppendAsync(new CoreAdminEventEnvelope
            {
                SequenceId = 1,
                EventType = "usage-log",
                OccurredAt = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero),
                PayloadJson = "{}"
            });
            await store.AppendAsync(new CoreAdminEventEnvelope
            {
                SequenceId = 2,
                EventType = "conversation-turn",
                OccurredAt = new DateTimeOffset(2026, 6, 10, 12, 0, 1, TimeSpan.Zero),
                PayloadJson = "{}"
            });

            store.HasBacklog().Should().BeTrue();
            (await store.GetLatestSequenceIdAsync()).Should().Be(2);
            (await store.ListAfterAsync(0)).Select(x => x.SequenceId).Should().Equal(1, 2);

            await store.TrimAckedAsync(1);

            var remaining = await store.ListAfterAsync(0);
            remaining.Select(x => x.SequenceId).Should().Equal(2);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, true);
            }
        }
    }
}
