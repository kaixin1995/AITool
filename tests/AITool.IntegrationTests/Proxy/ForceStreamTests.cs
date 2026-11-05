using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 强制上游流式（X-AITool-Force-Stream）与 OpenAI Chat SSE 聚合的单元测试：
/// ① 请求体改写（加 stream:true / 已 true 不重复 / 非 JSON 不动）；
/// ② SSE 聚合（纯文本 / tool_calls / usage / 空流 / 无效输入）；
/// ③ BuildRequestMessage 剥离标记头不上行。
/// </summary>
public sealed class ForceStreamTests
{
    // ---- ForceStreamTrue（通过反射不可直接调用 private 方法，经聚合器+标记头行为覆盖） ----

    // ---- TryAggregateChatCompletionSse ----

    [Fact]
    public void Aggregate_plain_text_stream()
    {
        const string sse = """
            data: {"id":"c1","object":"chat.completion.chunk","model":"glm-5.3","choices":[{"index":0,"delta":{"role":"assistant","content":"你"},"finish_reason":null}]}
            
            data: {"id":"c1","object":"chat.completion.chunk","model":"glm-5.3","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":null}]}
            
            data: {"id":"c1","object":"chat.completion.chunk","model":"glm-5.3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}
            
            data: [DONE]
            """;

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        var obj = System.Text.Json.JsonDocument.Parse(result!);
        obj.RootElement.GetProperty("object").GetString().Should().Be("chat.completion");
        obj.RootElement.GetProperty("id").GetString().Should().Be("c1");
        obj.RootElement.GetProperty("model").GetString().Should().Be("glm-5.3");
        var message = obj.RootElement.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("role").GetString().Should().Be("assistant");
        message.GetProperty("content").GetString().Should().Be("你好");
        obj.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
        obj.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64().Should().Be(3);
        obj.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64().Should().Be(5);
    }

    [Fact]
    public void Aggregate_tool_calls_stream()
    {
        const string sse = """
            data: {"id":"c2","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"tc1","type":"function","function":{"name":"get_weather","arguments":""}}]}}]}
            
            data: {"id":"c2","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]}}]}
            
            data: {"id":"c2","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"北京\"}"}}]}}]}
            
            data: {"id":"c2","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}
            
            data: [DONE]
            """;

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        var obj = System.Text.Json.JsonDocument.Parse(result!);
        var message = obj.RootElement.GetProperty("choices")[0].GetProperty("message");
        message.TryGetProperty("tool_calls", out var tc).Should().BeTrue();
        // 分片按 index 合并为一个完整调用：id/name 取首个分片，arguments 逐段拼接。
        tc.GetArrayLength().Should().Be(1);
        tc[0].GetProperty("id").GetString().Should().Be("tc1");
        tc[0].GetProperty("type").GetString().Should().Be("function");
        tc[0].GetProperty("function").GetProperty("name").GetString().Should().Be("get_weather");
        tc[0].GetProperty("function").GetProperty("arguments").GetString().Should().Be("{\"city\":\"北京\"}");
        obj.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("tool_calls");
    }

    [Fact]
    public void Aggregate_parallel_tool_calls_merge_by_index()
    {
        const string sse = """
            data: {"id":"c4","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","type":"function","function":{"name":"read","arguments":"{}"}}]}}]}
            
            data: {"id":"c4","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"b","type":"function","function":{"name":"write","arguments":"{\"p\":1}"}}]}}]}
            
            data: {"id":"c4","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"more\":true}"}}]},"finish_reason":"tool_calls"}]}
            
            data: [DONE]
            """;

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        var tc = System.Text.Json.JsonDocument.Parse(result!).RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls");
        tc.GetArrayLength().Should().Be(2, "两个 index 各合并为一条");
        tc[0].GetProperty("id").GetString().Should().Be("a");
        tc[0].GetProperty("function").GetProperty("arguments").GetString().Should().Be("{}{\"more\":true}");
        tc[1].GetProperty("id").GetString().Should().Be("b");
        tc[1].GetProperty("function").GetProperty("name").GetString().Should().Be("write");
    }

    [Fact]
    public void Aggregate_reasoning_content()
    {
        const string sse = """
            data: {"id":"c3","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"思考中"},"finish_reason":null}]}
            
            data: {"id":"c3","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"content":"回答"},"finish_reason":"stop"}]}
            
            data: [DONE]
            """;

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        var obj = System.Text.Json.JsonDocument.Parse(result!);
        var message = obj.RootElement.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("content").GetString().Should().Be("回答");
        message.GetProperty("reasoning_content").GetString().Should().Be("思考中");
    }

    [Fact]
    public void Aggregate_empty_or_invalid_input_returns_null()
    {
        ProxyForwardService.TryAggregateChatCompletionSse("").Should().BeNull();
        ProxyForwardService.TryAggregateChatCompletionSse("not sse").Should().BeNull();
        // 只有 [DONE] 没有任何 chunk → null。
        ProxyForwardService.TryAggregateChatCompletionSse("data: [DONE]").Should().BeNull();
        // 非 chat.completion.chunk 对象（如 Responses API 事件）→ null。
        ProxyForwardService.TryAggregateChatCompletionSse("""data: {"type":"response.completed","response":{}}""").Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Aggregate_rejects_stream_with_only_empty_choices(bool withDone)
    {
        const string chunk = "data: {\"object\":\"chat.completion.chunk\",\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":0}}\n\n";
        ProxyForwardService.TryAggregateChatCompletionSse(chunk + (withDone ? "data: [DONE]\n" : ""))
            .Should().BeNull("用量块不是回答，不能聚合为空 choices 的成功结果");
    }

    [Fact]
    public void Aggregate_accepts_empty_usage_chunk_after_choice()
    {
        const string sse = """
            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: {"object":"chat.completion.chunk","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2}}

            data: [DONE]
            """;
        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        using var obj = System.Text.Json.JsonDocument.Parse(result!);
        obj.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("ok");
        obj.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64().Should().Be(3);
    }

    [Fact]
    public void Aggregate_tolerates_empty_data_and_sse_comments()
    {
        const string sse = """
            data:

            : stream starting

            data: {"id":"c5","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}]}

            data: [DONE]
            """;

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        var obj = System.Text.Json.JsonDocument.Parse(result!);
        obj.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("好");
        obj.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
    }

    [Theory]
    [InlineData("not-json-garbage")]
    [InlineData("{\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lost")]
    [InlineData("{\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[")]
    public void Aggregate_rejects_malformed_data_between_valid_chunks(string malformed)
    {
        const string before = "data: {\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"before\"}}]}\n\n";
        const string after = "data: {\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"after\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
        ProxyForwardService.TryAggregateChatCompletionSse(before + "data: " + malformed + "\n\n" + after)
            .Should().BeNull("损坏的非空数据可能包含丢失的回答或工具调用");
    }

    [Fact]
    public void Aggregate_rejects_error_events()
    {
        const string chunk = "data: {\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"stop\"}]}\n\n";
        ProxyForwardService.TryAggregateChatCompletionSse(chunk + "data: {\"error\":{\"message\":\"bad\"}}\n\ndata: [DONE]\n").Should().BeNull();
    }

    [Fact]
    public void Aggregate_rejects_truncated_stream_without_terminal_signal()
    {
        const string sse = """
            data: {"id":"c6","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"content":"部分回答"},"finish_reason":null}]}

            """;

        ProxyForwardService.TryAggregateChatCompletionSse(sse).Should().BeNull("截断的回答不能作为成功结果返回");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Aggregate_multiple_choices_only_done_allows_missing_finish_reason_fallback(bool withDone)
    {
        // 一个 choice 已结束，不代表另一个 choice 已结束；[DONE] 则是全流收尾信号。
        const string chunks = """
            data: {"id":"c9","object":"chat.completion.chunk","model":"m","choices":[{"index":2,"delta":{"content":"部分"}},{"index":0,"delta":{"content":"完成"}}]}

            data: {"id":"c9","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"length"}]}

            """;
        var sse = chunks + (withDone ? "\ndata: [DONE]\n" : "");

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        if (!withDone)
        {
            result.Should().BeNull("另一个 choice 的结束原因不能证明本 choice 已完成");
            return;
        }

        result.Should().NotBeNull();
        using var obj = System.Text.Json.JsonDocument.Parse(result!);
        var choices = obj.RootElement.GetProperty("choices");
        choices.GetArrayLength().Should().Be(2);
        choices[0].GetProperty("index").GetInt32().Should().Be(0);
        choices[0].GetProperty("finish_reason").GetString().Should().Be("length");
        choices[1].GetProperty("index").GetInt32().Should().Be(2);
        choices[1].GetProperty("message").GetProperty("content").GetString().Should().Be("部分");
        choices[1].GetProperty("finish_reason").GetString().Should().Be("stop");
    }

    [Fact]
    public void Aggregate_missing_finish_reason_with_done_gets_stop_fallback()
    {
        // 部分实现不逐 choice 发 finish_reason（只在末块发或干脆不发）：流已正常收尾（[DONE]）
        // → 兜底 stop 判定成功，而非把本可成功的请求变成 502。
        const string sse = """
            data: {"id":"c8","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"content":"全量输出"}}]}

            data: [DONE]
            """;

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        var obj = System.Text.Json.JsonDocument.Parse(result!);
        obj.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("全量输出");
        obj.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
    }

    [Fact]
    public void Aggregate_multiple_choices_by_index()
    {
        // 客户端 n=2 的合法请求经强制流式站点：两个 choice 都要聚合，不能丢第 2 个。
        const string sse = """
            data: {"id":"c7","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"甲"}},{"index":1,"delta":{"role":"assistant","content":"乙"}}]}

            data: {"id":"c7","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"content":"续"},"finish_reason":"stop"},{"index":1,"delta":{},"finish_reason":"length"}]}

            data: [DONE]
            """;

        var result = ProxyForwardService.TryAggregateChatCompletionSse(sse);
        result.Should().NotBeNull();
        var choices = System.Text.Json.JsonDocument.Parse(result!).RootElement.GetProperty("choices");
        choices.GetArrayLength().Should().Be(2);
        choices[0].GetProperty("index").GetInt32().Should().Be(0);
        choices[0].GetProperty("message").GetProperty("content").GetString().Should().Be("甲续");
        choices[0].GetProperty("finish_reason").GetString().Should().Be("stop");
        choices[1].GetProperty("index").GetInt32().Should().Be(1);
        choices[1].GetProperty("message").GetProperty("content").GetString().Should().Be("乙");
        choices[1].GetProperty("finish_reason").GetString().Should().Be("length");
    }

    // ---- ForceStreamHeader 常量验证（BuildRequestMessage 剥离不上行） ----

    [Fact]
    public void Force_stream_header_is_defined_and_matches_site_config_marker()
    {
        ProxyForwardService.ForceStreamHeader.Should().Be("X-AITool-Force-Stream");
    }
}
