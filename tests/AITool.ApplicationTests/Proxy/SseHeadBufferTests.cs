using AITool.Application.Proxy;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// SSE 首包蓄流器测试：用真实协议形态的热身/内容事件验证——热身块（含自带 content/text/error
/// 字段名的 message_start、role 增量、response.created）必须扣住，真正的增量/结束/错误事件才放行。
/// </summary>
public sealed class SseHeadBufferTests
{
    // —— 真实形态的事件块 ——
    private const string AnthropicMessageStart =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"content\":[],\"role\":\"assistant\"}}\n\n";
    private const string AnthropicPing = "event: ping\ndata: {\"type\":\"ping\"}\n\n";
    private const string AnthropicBlockDelta =
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"你\"}}\n\n";
    private const string OpenAiRoleOnlyDelta =
        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}\n\n";
    private const string OpenAiContentDelta =
        "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n";
    private const string ResponsesCreated =
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"text\":{\"format\":{}},\"error\":null}}\n\n";
    private const string ResponsesTextDelta =
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"你\"}\n\n";
    private const string ErrorObject =
        "data: {\"error\":{\"message\":\"boom\",\"type\":\"server_error\"}}\n\n";

    [Fact]
    public void Real_warmup_events_are_held()
    {
        var buffer = new SseHeadBuffer();
        // Anthropic message_start 自带 "content":[] —— 必须扣住（本特性的初衷）。
        buffer.TryHold(AnthropicMessageStart).Should().BeTrue("message_start 是热身事件");
        buffer.TryHold(AnthropicPing).Should().BeTrue("ping 是热身事件");
        // OpenAI role-only 增量（content 空串）也是热身。
        buffer.TryHold(OpenAiRoleOnlyDelta).Should().BeTrue("空 content 增量是热身事件");
        // Responses created 自带 "text":{} 与 "error":null —— 同样热身。
        buffer.TryHold(ResponsesCreated).Should().BeTrue("response.created 是热身事件");
        buffer.Released.Should().BeFalse("四个热身块都被扣住");
        buffer.Drain().Should().HaveCount(4);
    }

    [Fact]
    public void Real_content_events_release()
    {
        var buffer = new SseHeadBuffer();
        buffer.TryHold(AnthropicMessageStart).Should().BeTrue();
        // Anthropic 首个内容增量放行，连带此前扣住的热身块。
        buffer.TryHold(AnthropicBlockDelta).Should().BeFalse("content_block_delta 是内容事件");
        buffer.Released.Should().BeTrue();
        buffer.Drain().Should().HaveCount(1, "此前扣住的热身块随放行一并交出");
    }

    [Fact]
    public void Openai_nonempty_content_releases_but_empty_does_not()
    {
        var buffer = new SseHeadBuffer();
        buffer.TryHold(OpenAiRoleOnlyDelta).Should().BeTrue("content 空串不放行");
        buffer.TryHold(OpenAiContentDelta).Should().BeFalse("非空 content 增量放行");
    }

    [Fact]
    public void Responses_text_delta_releases()
    {
        var buffer = new SseHeadBuffer();
        buffer.TryHold(ResponsesCreated).Should().BeTrue();
        buffer.TryHold(ResponsesTextDelta).Should().BeFalse("output_text.delta 是内容事件");
    }

    [Fact]
    public void Max_held_blocks_forces_release()
    {
        var buffer = new SseHeadBuffer();
        for (var i = 0; i < SseHeadBuffer.MaxHeldBlocks; i++)
        {
            buffer.TryHold(AnthropicPing).Should().BeTrue();
        }

        // 攒满上限后，即使是热身块也必须放行，保证首字延迟增量有界。
        buffer.TryHold(AnthropicPing).Should().BeFalse();
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
    public void Done_and_error_events_release_immediately()
    {
        new SseHeadBuffer().TryHold("data: [DONE]\n\n").Should().BeFalse();

        var errorBuffer = new SseHeadBuffer();
        errorBuffer.TryHold(ErrorObject).Should().BeFalse("错误对象必须立即交给上层判定");
    }
}
