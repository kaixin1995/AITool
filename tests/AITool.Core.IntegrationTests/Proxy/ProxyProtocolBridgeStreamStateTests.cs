using AITool.Protocol;
using FluentAssertions;

namespace AITool.Core.IntegrationTests.Proxy;

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

    // —— Anthropic 出口 usage 口径一致性：input_tokens 不含缓存，三桶相加 = 总输入（官方语义）。 ——

    private static (string Start, string Delta) ExtractAnthropicUsageEvents(string sse)
    {
        var start = System.Text.RegularExpressions.Regex.Match(sse, "\"message_start\"[\\s\\S]*?\"usage\":\\{([\\s\\S]*?)\\}").Groups[1].Value;
        var delta = System.Text.RegularExpressions.Regex.Match(sse, "\"message_delta\",[\\s\\S]*?\"usage\":\\{([\\s\\S]*?)\\}").Groups[1].Value;
        return (start, delta);
    }

    [Fact]
    public void Realtime_stream_delta_reports_input_tokens_excluding_cache()
    {
        var state = new ProxyProtocolBridge.AnthropicOpenAiStreamState();

        // 上游最后一帧带 usage：prompt_tokens=100（含缓存 80）→ 新输入 20。
        ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":80}}}""",
            state);
        var sse = ProxyProtocolBridge.CompleteAnthropicStream(state);
        var (_, delta) = ExtractAnthropicUsageEvents(sse);

        delta.Should().Contain("\"input_tokens\":20", "delta 的 input_tokens 不含缓存（官方三桶加法语义）");
        delta.Should().Contain("\"cache_read_input_tokens\":80", "缓存在独立桶中完整上报");
    }

    [Fact]
    public void Replay_stream_start_and_delta_agree_on_excluding_cache()
    {
        var sse = ProxyProtocolBridge.BuildAnthropicStreamFromOpenAiResponse(
            """{"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":80}}}""",
            "test-model", 0, 0, 0);
        var (start, delta) = ExtractAnthropicUsageEvents(sse);

        start.Should().Contain("\"input_tokens\":20");
        delta.Should().Contain("\"input_tokens\":20", "message_delta 与 message_start 同口径，且不含缓存");
        delta.Should().Contain("\"cache_read_input_tokens\":80");
    }

    [Fact]
    public void Replay_stream_fallback_params_are_not_double_subtracted()
    {
        // 响应体不带 usage，回退到入参（aba2773 后入参已是"新输入"20）——不能再减缓存 80。
        var sse = ProxyProtocolBridge.BuildAnthropicStreamFromOpenAiResponse(
            """{"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""",
            "test-model", 20, 80, 5);
        var (start, delta) = ExtractAnthropicUsageEvents(sse);

        start.Should().Contain("\"input_tokens\":20", "回退分支入参已是新输入，双重扣减会把它错算成 0");
        delta.Should().Contain("\"input_tokens\":20");
        delta.Should().Contain("\"cache_read_input_tokens\":80");
    }

    [Fact]
    public void Buffered_stream_via_adapt_agrees_on_excluding_cache()
    {
        var upstreamSse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}",
            "",
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":80}}}",
            "",
            "data: [DONE]",
            "");

        var sse = ProxyProtocolBridge.AdaptResponseBodyForClient(
            "Anthropic", "OpenAI", upstreamSse, isStreaming: true, "test-model",
            inputTokens: 0, cachedTokens: 0, outputTokens: 0);
        var (start, delta) = ExtractAnthropicUsageEvents(sse);

        start.Should().Contain("\"input_tokens\":20");
        delta.Should().Contain("\"input_tokens\":20");
        delta.Should().Contain("\"cache_read_input_tokens\":80");
    }

    [Fact]
    public void Buffered_stream_fallback_params_are_not_double_subtracted()
    {
        var upstreamSse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}",
            "",
            "data: [DONE]",
            "");

        var sse = ProxyProtocolBridge.AdaptResponseBodyForClient(
            "Anthropic", "OpenAI", upstreamSse, isStreaming: true, "test-model",
            inputTokens: 20, cachedTokens: 80, outputTokens: 5);
        var (start, delta) = ExtractAnthropicUsageEvents(sse);

        start.Should().Contain("\"input_tokens\":20", "回退分支不得双重扣减缓存");
        delta.Should().Contain("\"input_tokens\":20");
    }
}
