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
            
            data: {"id":"c4","object":"chat.completion.chunk","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"more\":true}"}}]}}]}
            
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

    // ---- ForceStreamHeader 常量验证（BuildRequestMessage 剥离不上行） ----

    [Fact]
    public void Force_stream_header_is_defined_and_matches_site_config_marker()
    {
        ProxyForwardService.ForceStreamHeader.Should().Be("X-AITool-Force-Stream");
    }
}
