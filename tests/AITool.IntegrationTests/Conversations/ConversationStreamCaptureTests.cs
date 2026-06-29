using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests.Conversations;

/// <summary>
/// ConversationStreamCapture 的单元测试。
/// 锁定核心契约：流式转发时实时累积 AI 正文，不受 64KB 诊断副本限制，
/// 完整捕获超过 64KB 的 AI 回复（这是对话记录"显示不全"bug 的修复关键）。
/// </summary>
public sealed class ConversationStreamCaptureTests
{
    [Fact]
    public void AppendOpenAiChatDelta_accumulates_full_content_beyond_64kb()
    {
        var capture = new ConversationStreamCapture();
        // 构造一段总长 100KB 的正文（每个 delta 1KB，共 100 个），远超旧版 64KB 上限。
        var chunkPerDelta = new string('A', 1024);
        for (var i = 0; i < 100; i++)
        {
            var payload = $"{{\"choices\":[{{\"delta\":{{\"content\":\"{chunkPerDelta}\"}}}}]}}";
            capture.AppendOpenAiChatDelta(payload);
        }

        var content = capture.Build();
        // 100KB 全部捕获，没有被 64KB 截断。
        content.Length.Should().Be(100 * 1024);
    }

    [Fact]
    public void AppendOpenAiChatDelta_ignores_usage_and_non_content_events()
    {
        var capture = new ConversationStreamCapture();
        // usage 事件、role chunk 不应进入正文。
        capture.AppendOpenAiChatDelta("""{"choices":[{"delta":{"role":"assistant"}}]}""");
        capture.AppendOpenAiChatDelta("""{"choices":[],"usage":{"prompt_tokens":10}}""");
        capture.AppendOpenAiChatDelta("""{"choices":[{"delta":{"content":"真实正文"}}]}""");

        capture.Build().Should().Be("真实正文");
    }

    [Fact]
    public void AppendOpenAiResponsesDelta_handles_output_text_delta()
    {
        var capture = new ConversationStreamCapture();
        capture.AppendOpenAiResponsesDelta("""{"type":"response.output_text.delta","delta":"第一段"}""");
        capture.AppendOpenAiResponsesDelta("""{"type":"response.output_text.delta","delta":"第二段"}""");
        capture.AppendOpenAiResponsesDelta("""{"type":"response.completed","response":{"output":[]}}""");

        capture.Build().Should().Be("第一段第二段");
    }

    [Fact]
    public void AppendAnthropicDelta_accumulates_text_delta_only()
    {
        var capture = new ConversationStreamCapture();
        // message_start 携带 usage，不应进入正文。
        capture.AppendAnthropicDelta("message_start", """{"message":{"usage":{"input_tokens":5}}}""");
        capture.AppendAnthropicDelta("content_block_delta", """{"delta":{"type":"text_delta","text":"你好"}}""");
        capture.AppendAnthropicDelta("content_block_delta", """{"delta":{"type":"text_delta","text":"世界"}}""");
        // signature_delta 不应进入正文。
        capture.AppendAnthropicDelta("content_block_delta", """{"delta":{"type":"signature_delta","signature":"xxx"}}""");

        capture.Build().Should().Be("你好世界");
    }

    [Fact]
    public void Build_caps_at_max_and_appends_truncation_marker()
    {
        var capture = new ConversationStreamCapture();
        // 喂入远超 1MB 上限的内容，验证截断兜底生效。
        var bigChunk = new string('B', 600 * 1024);
        capture.AppendOpenAiChatDelta($"{{\"choices\":[{{\"delta\":{{\"content\":\"{bigChunk}\"}}}}]}}");
        capture.AppendOpenAiChatDelta($"{{\"choices\":[{{\"delta\":{{\"content\":\"{bigChunk}\"}}}}]}}");

        var content = capture.Build();
        content.Should().Contain("已截断");
        content.Length.Should().BeLessThanOrEqualTo(ConversationStreamCapture.MaxContentChars + 100);
    }
}
