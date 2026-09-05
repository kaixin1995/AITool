using System.Text;
using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// SSE 心跳注入流单测：静默超阈值出现 `: ping` 注释帧；持续写数据不注入；关闭配置不注入。
/// </summary>
public sealed class SseHeartbeatStreamTests
{
    /// <summary>
    /// 静默超过阈值后应注入 `: ping` 注释帧（SSE 规范注释，标准客户端忽略）。
    /// </summary>
    [Fact]
    public async Task Ping_injected_after_silent_gap()
    {
        var inner = new MemoryStream();
        using var stream = new SseHeartbeatStream(inner, intervalSeconds: 1);

        await stream.WriteAsync("data: {\"a\":1}\n\n"u8.ToArray());
        await stream.FlushAsync();

        // 阈值 1s + 轮询 1s：等待出现 ping。
        await Task.Delay(TimeSpan.FromMilliseconds(2300));

        var text = Encoding.UTF8.GetString(inner.ToArray());
        text.Should().Contain(": ping\n\n", "静默期应注入心跳注释帧");
        text.Should().Contain("data: {\"a\":1}\n\n", "真实数据帧保持原样");
    }

    /// <summary>
    /// 持续有数据写入时不注入 ping（空闲基准随每次写入刷新）。
    /// </summary>
    [Fact]
    public async Task No_ping_when_data_flows_continuously()
    {
        var inner = new MemoryStream();
        using var stream = new SseHeartbeatStream(inner, intervalSeconds: 1);

        for (var i = 0; i < 6; i++)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: chunk-{i}\n\n"));
            await stream.FlushAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        // 总时长约 1.2s > 阈值 1s，但每段空闲仅 200ms，不应出现 ping。
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        var text = Encoding.UTF8.GetString(inner.ToArray());
        text.Should().NotContain(": ping", "持续写入时不应注入心跳");
        text.Should().Contain("data: chunk-5\n\n");
    }

    /// <summary>
    /// 选项语义：0 秒 = 关闭；负值被钳制为 0（关闭）。
    /// </summary>
    [Fact]
    public void Options_zero_means_disabled()
    {
        new SseHeartbeatOptions(0).Enabled.Should().BeFalse();
        new SseHeartbeatOptions(-5).Enabled.Should().BeFalse();
        new SseHeartbeatOptions(15).Enabled.Should().BeTrue();
        new SseHeartbeatOptions(15).Seconds.Should().Be(15);
    }
}