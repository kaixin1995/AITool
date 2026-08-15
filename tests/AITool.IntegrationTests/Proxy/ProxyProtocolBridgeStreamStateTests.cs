using AITool.Protocol;
using FluentAssertions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 流式转换状态机的回归测试：
/// 1. 工具调用关闭 thinking/text 块后再出现的增量必须用新索引重开块（不能对已 stop 的索引发 delta）；
/// 2. Anthropic→Responses 方向 thinking_delta 的正文在 delta.thinking 字段（不在 delta.text）。
/// </summary>
public sealed class ProxyProtocolBridgeStreamStateTests
{
    [Fact]
    public void OpenAi_to_Anthropic_reopens_thinking_block_with_new_index_after_tool_calls()
    {
        var state = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
        var output = new System.Text.StringBuilder();

        // 1) reasoning 增量 → 创建 thinking 块（index 0）
        output.Append(ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{"reasoning_content":"think-1"}}]}""", state));
        // 2) 工具调用增量 → 关闭 thinking 块
        output.Append(ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"write","arguments":"{}"}}]}}]}""", state));
        // 3) 再次出现 reasoning 增量（GLM/DeepSeek 分段思考）
        output.Append(ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{"reasoning_content":"think-2"}}]}""", state));

        var text = output.ToString();

        // 第二段 reasoning 必须重开新的 thinking 块（新 content_block_start + 新索引），而不是复用已 stop 的索引。
        var startEvents = System.Text.RegularExpressions.Regex.Matches(text, "\"type\":\"content_block_start\"[^}]*\"index\":(\\d+),\"content_block\":\\{\"type\":\"thinking\"");
        startEvents.Count.Should().Be(2, "第二段 reasoning 应重开 thinking 块");
        startEvents[1].Groups[1].Value.Should().NotBe(startEvents[0].Groups[1].Value, "重开的块必须使用新索引");

        // 对旧索引不能再发 thinking_delta：think-2 的 delta 应指向新索引。
        var secondIndex = int.Parse(startEvents[1].Groups[1].Value);
        var think2Delta = System.Text.RegularExpressions.Regex.Matches(text, $"\"index\":{secondIndex},\"delta\":\\{{\"type\":\"thinking_delta\",\"thinking\":\"think-2\"}}");
        think2Delta.Count.Should().Be(1);
    }

    [Fact]
    public void OpenAi_to_Anthropic_reopens_text_block_with_new_index_after_tool_calls()
    {
        var state = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
        var output = new System.Text.StringBuilder();

        // 1) 文本增量 → 创建 text 块（index 0）
        output.Append(ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{"content":"text-1"}}]}""", state));
        // 2) 工具调用增量 → 关闭 text 块
        output.Append(ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read","arguments":"{}"}}]}}]}""", state));
        // 3) 工具调用后的尾段文本
        output.Append(ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{"content":"text-2"}}]}""", state));

        var text = output.ToString();

        var startEvents = System.Text.RegularExpressions.Regex.Matches(text, "\"type\":\"content_block_start\"[^}]*\"index\":(\\d+),\"content_block\":\\{\"type\":\"text\"");
        startEvents.Count.Should().Be(2, "工具调用后的尾段文本应重开 text 块");
        startEvents[1].Groups[1].Value.Should().NotBe(startEvents[0].Groups[1].Value, "重开的块必须使用新索引");
    }

    [Fact]
    public void Anthropic_to_Responses_reads_thinking_field_for_thinking_delta()
    {
        var state = new ChatToResponsesStreamState();

        var output = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(
            "content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"chain-of-thought"}}""",
            state);

        output.Should().Contain("response.reasoning_summary_text.delta", "thinking 增量应转发为 reasoning summary 事件");
        output.Should().Contain("chain-of-thought", "思维链正文来自 delta.thinking 字段（修复前误读 delta.text 导致整段丢失）");
    }
}
