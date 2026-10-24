using AITool.Application.Proxy;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// SSE 首包蓄流器测试：热身块扣住、内容块放行、上限强制放行、冲刷与丢弃语义。
/// </summary>
public sealed class SseHeadBufferTests
{
    private const string RoleDeltaChunk = "data: {\"delta\":{\"role\":\"assistant\"}}\n\n";
    private const string ContentChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n";

    [Fact]
    public void Warmup_chunks_are_held_until_content_arrives()
    {
        var buffer = new SseHeadBuffer();

        buffer.TryHold(RoleDeltaChunk).Should().BeTrue("role-only 增量是热身块");
        buffer.Released.Should().BeFalse();
        buffer.TryHold("event: ping\ndata: {\"type\":\"ping\"}\n\n").Should().BeTrue();

        buffer.TryHold(ContentChunk).Should().BeFalse("内容块触发放行");
        buffer.Released.Should().BeTrue();
        var held = buffer.Drain();
        held.Should().HaveCount(2, "之前扣住的热身块随放行一并交出");
        buffer.TryHold(ContentChunk).Should().BeFalse("放行后全部直写");
        buffer.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Max_held_blocks_forces_release()
    {
        var buffer = new SseHeadBuffer();
        for (var i = 0; i < SseHeadBuffer.MaxHeldBlocks; i++)
        {
            buffer.TryHold(RoleDeltaChunk).Should().BeTrue();
        }

        // 攒满上限后，即使是热身块也必须放行，保证首字延迟增量有界。
        buffer.TryHold(RoleDeltaChunk).Should().BeFalse();
        buffer.Released.Should().BeTrue();
    }

    [Fact]
    public void Max_held_chars_forces_release()
    {
        var buffer = new SseHeadBuffer();
        var bigWarmup = "data: {\"delta\":{\"role\":\"assistant\",\"x\":\"" + new string('a', 6000) + "\"}}\n\n";
        buffer.TryHold(bigWarmup).Should().BeTrue();
        buffer.TryHold(bigWarmup).Should().BeFalse("累计字符超上限即放行");
    }

    [Fact]
    public void Done_and_error_chunks_count_as_content()
    {
        var buffer = new SseHeadBuffer();
        buffer.TryHold("data: [DONE]\n\n").Should().BeFalse();

        var errorBuffer = new SseHeadBuffer();
        errorBuffer.TryHold("data: {\"error\":{\"message\":\"boom\"}}\n\n").Should().BeFalse();
    }
}
