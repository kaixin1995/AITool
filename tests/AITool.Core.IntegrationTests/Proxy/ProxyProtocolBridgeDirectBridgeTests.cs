using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Protocol;
using FluentAssertions;

namespace AITool.Core.IntegrationTests.Proxy;

/// <summary>
/// Anthropic ↔ Responses 直接转换（不经 Chat 中转）与配套修复的回归测试。
/// </summary>
public sealed class ProxyProtocolBridgeDirectBridgeTests
{
    // ============ 请求方向：Anthropic → Responses 直转 ============

    [Fact]
    public void PrepareRequestBody_anthropic_to_responses_maps_instructions_tools_and_tool_choice()
    {
        var anthropicBody = """
        {
          "model": "route-entry",
          "system": "you are helpful",
          "max_tokens": 1024,
          "temperature": 0.3,
          "messages": [
            { "role": "user", "content": [ { "type": "text", "text": "hi" } ] },
            { "role": "assistant", "content": [
              { "type": "text", "text": "calling" },
              { "type": "tool_use", "id": "toolu_1", "name": "write", "input": { "path": "a.txt" } }
            ] },
            { "role": "user", "content": [
              { "type": "tool_result", "tool_use_id": "toolu_1", "content": "ok" },
              { "type": "text", "text": "continue" }
            ] }
          ],
          "tools": [ { "name": "write", "description": "write file", "input_schema": { "type": "object" } } ],
          "tool_choice": { "type": "auto", "disable_parallel_tool_use": true }
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody("Anthropic", "Responses", anthropicBody, "upstream-model", enableStreaming: true);

        var root = JsonNode.Parse(result)!.AsObject();
        root["model"]!.GetValue<string>().Should().Be("upstream-model");
        root["instructions"]!.GetValue<string>().Should().Be("you are helpful");
        root["max_output_tokens"]!.GetValue<int>().Should().Be(1024);
        root["store"]!.GetValue<bool>().Should().BeFalse("Responses 上游要求 store=false，由出口 NormalizeResponsesBody 补齐");
        root["stream"]!.GetValue<bool>().Should().BeTrue();

        var input = root["input"]!.AsArray();
        // user text / assistant message / function_call / function_call_output / user text
        input.Should().HaveCount(5);
        input[0]!["type"]!.GetValue<string>().Should().Be("message");
        input[0]!["role"]!.GetValue<string>().Should().Be("user");
        input[1]!["type"]!.GetValue<string>().Should().Be("message");
        input[1]!["role"]!.GetValue<string>().Should().Be("assistant");
        input[2]!["type"]!.GetValue<string>().Should().Be("function_call");
        input[2]!["call_id"]!.GetValue<string>().Should().Be("toolu_1");
        input[2]!["name"]!.GetValue<string>().Should().Be("write");
        input[3]!["type"]!.GetValue<string>().Should().Be("function_call_output");
        input[3]!["call_id"]!.GetValue<string>().Should().Be("toolu_1");
        input[4]!["type"]!.GetValue<string>().Should().Be("message");

        var tools = root["tools"]!.AsArray();
        tools.Should().HaveCount(1);
        tools[0]!["type"]!.GetValue<string>().Should().Be("function");
        tools[0]!["name"]!.GetValue<string>().Should().Be("write");
        tools[0]!["parameters"]!["type"]!.GetValue<string>().Should().Be("object");

        root["tool_choice"]!.GetValue<string>().Should().Be("auto");
        root["parallel_tool_calls"]!.GetValue<bool>().Should().BeFalse("disable_parallel_tool_use=true 应映射为 parallel_tool_calls=false");
    }

    [Fact]
    public void PrepareRequestBody_anthropic_to_responses_maps_document_to_input_file()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "messages": [
            { "role": "user", "content": [
              { "type": "document", "title": "doc.pdf", "source": { "type": "base64", "media_type": "application/pdf", "data": "QUJD" } },
              { "type": "text", "text": "read this" }
            ] }
          ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody("Anthropic", "Responses", anthropicBody, "m", enableStreaming: false);
        var input = JsonNode.Parse(result)!["input"]!.AsArray();

        var filePart = input[0]!["content"]!.AsArray()[0]!.AsObject();
        filePart["type"]!.GetValue<string>().Should().Be("input_file");
        filePart["filename"]!.GetValue<string>().Should().Be("doc.pdf");
        filePart["file_data"]!.GetValue<string>().Should().Be("data:application/pdf;base64,QUJD");
    }

    [Fact]
    public void PrepareRequestBody_anthropic_to_responses_applies_reasoning_effort_override()
    {
        var anthropicBody = """{ "model": "m", "max_tokens": 100, "messages": [ { "role": "user", "content": "hi" } ] }""";

        var result = ProxyProtocolBridge.PrepareRequestBody("Anthropic", "Responses", anthropicBody, "m", enableStreaming: false, overrideReasoningEffort: "high");

        var root = JsonNode.Parse(result)!.AsObject();
        root["reasoning"]!["effort"]!.GetValue<string>().Should().Be("high", "思考等级强覆盖必须在直转后的最终请求体上生效");
    }

    // ============ 请求方向：Responses → Anthropic 直转 ============

    [Fact]
    public void PrepareRequestBody_responses_to_anthropic_maps_instructions_and_tool_round_trip()
    {
        var responsesBody = """
        {
          "model": "route-entry",
          "instructions": "sys prompt",
          "max_output_tokens": 2048,
          "input": [
            { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "hi" } ] },
            { "type": "function_call", "call_id": "call_1", "name": "write", "arguments": "{\"path\":\"a\"}" },
            { "type": "function_call_output", "call_id": "call_1", "output": "done" }
          ],
          "tools": [ { "type": "function", "name": "write", "parameters": { "type": "object" } } ],
          "tool_choice": "required",
          "reasoning": { "effort": "high" }
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody("Responses", "Anthropic", responsesBody, "claude-model", enableStreaming: false);

        var root = JsonNode.Parse(result)!.AsObject();
        root["model"]!.GetValue<string>().Should().Be("claude-model");
        root["system"]!.GetValue<string>().Should().Be("sys prompt");
        root["max_tokens"]!.GetValue<int>().Should().Be(2048);

        var messages = root["messages"]!.AsArray();
        // 第一条必须是 user（Anthropic 规范）
        messages[0]!["role"]!.GetValue<string>().Should().Be("user");

        var assistant = messages[1]!.AsObject();
        assistant["role"]!.GetValue<string>().Should().Be("assistant");
        var toolUse = assistant["content"]!.AsArray()[0]!.AsObject();
        toolUse["type"]!.GetValue<string>().Should().Be("tool_use");
        toolUse["id"]!.GetValue<string>().Should().Be("call_1");
        toolUse["input"]!["path"]!.GetValue<string>().Should().Be("a");

        var userResult = messages[2]!.AsObject();
        userResult["role"]!.GetValue<string>().Should().Be("user");
        userResult["content"]!.AsArray()[0]!["type"]!.GetValue<string>().Should().Be("tool_result");

        var tools = root["tools"]!.AsArray();
        tools[0]!["name"]!.GetValue<string>().Should().Be("write");
        tools[0]!["input_schema"]!["type"]!.GetValue<string>().Should().Be("object");

        root["tool_choice"]!["type"]!.GetValue<string>().Should().Be("any");
        root["thinking"]!["budget_tokens"]!.GetValue<int>().Should().Be(4096, "reasoning.effort=high 应映射为 thinking.budget_tokens");
        root["output_config"]!["effort"]!.GetValue<string>().Should().Be("high");
    }

    // ============ thinking 签名桥接往返 ============

    [Fact]
    public void Anthropic_thinking_signature_survives_round_trip_through_responses_bridge()
    {
        var anthropicResponse = """
        {
          "id": "msg_1",
          "type": "message",
          "role": "assistant",
          "model": "claude-x",
          "content": [
            { "type": "thinking", "thinking": "let me think", "signature": "sig-abc" },
            { "type": "text", "text": "answer" }
          ],
          "stop_reason": "end_turn",
          "usage": { "input_tokens": 100, "cache_read_input_tokens": 40, "cache_creation_input_tokens": 10, "output_tokens": 20 }
        }
        """;

        var responsesBody = ProxyProtocolBridge.ConvertAnthropicResponseToResponses(anthropicResponse);
        var responseRoot = JsonNode.Parse(responsesBody)!.AsObject();

        // thinking → reasoning 输出项，签名进入桥接载体
        var reasoning = responseRoot["output"]!.AsArray()[0]!.AsObject();
        reasoning["type"]!.GetValue<string>().Should().Be("reasoning");
        reasoning["encrypted_content"]!.GetValue<string>().Should().StartWith("aitool-anthropic-thinking-v1:");

        // usage：Responses 口径 input 含缓存（100+40+10=150）
        var usage = responseRoot["usage"]!.AsObject();
        usage["input_tokens"]!.GetValue<int>().Should().Be(150);
        usage["input_tokens_details"]!["cached_tokens"]!.GetValue<int>().Should().Be(40);
        usage["input_tokens_details"]!["cached_creation_tokens"]!.GetValue<int>().Should().Be(10);

        // 客户端回传 reasoning 项 → 请求方向还原带签名的 thinking block
        var nextRequest = $$"""
        {
          "model": "m",
          "input": [
            { "type": "reasoning", "encrypted_content": {{System.Text.Json.JsonSerializer.Serialize(reasoning["encrypted_content"]!.GetValue<string>())}} },
            { "type": "function_call", "call_id": "call_x", "name": "write", "arguments": "{}" }
          ]
        }
        """;
        var anthropicRequest = ProxyProtocolBridge.PrepareRequestBody("Responses", "Anthropic", nextRequest, "claude-x", enableStreaming: false);

        var requestRoot = JsonNode.Parse(anthropicRequest)!.AsObject();
        var assistant = requestRoot["messages"]!.AsArray().First(m => m!["role"]!.GetValue<string>() == "assistant")!.AsObject();
        var firstBlock = assistant["content"]!.AsArray()[0]!.AsObject();
        firstBlock["type"]!.GetValue<string>().Should().Be("thinking", "thinking 块必须位于 assistant 内容块最前");
        firstBlock["thinking"]!.GetValue<string>().Should().Be("let me think");
        firstBlock["signature"]!.GetValue<string>().Should().Be("sig-abc", "签名必须从桥接载体还原");
    }

    // ============ 响应方向：Responses → Anthropic 直转 ============

    [Fact]
    public void BuildAnthropicResponseFromResponses_converts_usage_and_stop_reason()
    {
        var responsesBody = """
        {
          "id": "resp_1",
          "object": "response",
          "status": "completed",
          "model": "gpt-x",
          "output": [
            { "type": "reasoning", "summary": [ { "type": "summary_text", "text": "hmm" } ] },
            { "type": "message", "role": "assistant", "content": [ { "type": "output_text", "text": "hello" } ] },
            { "type": "function_call", "call_id": "call_1", "name": "write", "arguments": "{\"a\":1}" }
          ],
          "usage": { "input_tokens": 1000, "output_tokens": 50, "input_tokens_details": { "cached_tokens": 300, "cached_creation_tokens": 100 } }
        }
        """;

        var result = ProxyProtocolBridge.BuildAnthropicResponseFromResponses(responsesBody, "gpt-x", 0, 0, 0);
        var root = JsonNode.Parse(result)!.AsObject();

        var content = root["content"]!.AsArray();
        content[0]!["type"]!.GetValue<string>().Should().Be("thinking");
        content[1]!["type"]!.GetValue<string>().Should().Be("text");
        content[2]!["type"]!.GetValue<string>().Should().Be("tool_use");
        content[2]!["id"]!.GetValue<string>().Should().Be("call_1");
        content[2]!["input"]!["a"]!.GetValue<int>().Should().Be(1);

        root["stop_reason"]!.GetValue<string>().Should().Be("tool_use");

        // Responses input_tokens 含缓存 → Anthropic 出口 fresh 口径：1000-300-100=600
        var usage = root["usage"]!.AsObject();
        usage["input_tokens"]!.GetValue<int>().Should().Be(600);
        usage["cache_read_input_tokens"]!.GetValue<int>().Should().Be(300);
        usage["cache_creation_input_tokens"]!.GetValue<int>().Should().Be(100);
        usage["output_tokens"]!.GetValue<int>().Should().Be(50);
    }

    [Fact]
    public void Responses_failed_status_is_rejected_even_with_partial_output()
    {
        var failedBody = """
        {
          "id": "resp_2",
          "status": "failed",
          "model": "gpt-x",
          "output": [ { "type": "message", "role": "assistant", "content": [ { "type": "output_text", "text": "partial" } ] } ],
          "usage": { "input_tokens": 10, "output_tokens": 5 }
        }
        """;

        ProxyProtocolBridge.BuildAnthropicResponseFromResponses(failedBody, "gpt-x", 0, 0, 0)
            .Should().BeNullOrEmpty("status=failed 且带部分 output 的响应不能当成功转换");
        ProxyProtocolBridge.ConvertResponsesResponseToChat(failedBody, "gpt-x", 0, 0, 0)
            .Should().BeNullOrEmpty("ConvertResponsesResponseToChat 同样要拒绝 failed 终态");
    }

    // ============ 流式：Responses → Anthropic 直转状态机 ============

    [Fact]
    public void ConvertResponsesSseEventToAnthropic_produces_full_anthropic_event_sequence()
    {
        var state = new ProxyProtocolBridge.ResponsesToAnthropicStreamState();

        var output = new System.Text.StringBuilder();
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.created",
            """{"type":"response.created","response":{"id":"resp_1","model":"gpt-x","usage":null}}""", state));

        // reasoning 输出项
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.output_item.added",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.reasoning_summary_text.delta",
            """{"type":"response.reasoning_summary_text.delta","delta":"hmm"}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.output_item.done",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}""", state));

        // message 输出项
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.output_item.added",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant","content":[]}}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.output_text.delta",
            """{"type":"response.output_text.delta","output_index":1,"delta":"hello"}""", state));

        // function_call 输出项
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.output_item.added",
            """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","call_id":"call_1","name":"write","arguments":""}}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.function_call_arguments.delta",
            """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"a\":1}"}""", state));

        // completed：写入 usage（input_tokens 含缓存，需归一化为 fresh）
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.completed",
            """{"type":"response.completed","response":{"id":"resp_1","model":"gpt-x","usage":{"input_tokens":1000,"output_tokens":50,"input_tokens_details":{"cached_tokens":300,"cached_creation_tokens":100}}}}""", state));

        // 收尾（控制器在流结束时调用）
        output.Append(ProxyProtocolBridge.CompleteAnthropicStream(state.Core));

        var text = output.ToString();
        text.Should().Contain("\"type\":\"thinking\"", "reasoning 输出项应转成 thinking 块");
        text.Should().Contain("\"thinking_delta\"");
        text.Should().Contain("\"text_delta\"");
        text.Should().Contain("\"type\":\"tool_use\"");
        text.Should().Contain("\"input_json_delta\"");
        text.Should().Contain("\"stop_reason\":\"tool_use\"");

        // usage 归一化：fresh = 1000-300-100 = 600
        text.Should().Contain("\"input_tokens\":600");
        text.Should().Contain("\"cache_read_input_tokens\":300");
        text.Should().Contain("\"cache_creation_input_tokens\":100");

        state.Completed.Should().BeTrue();
        state.Failed.Should().BeFalse();
        state.Core.ReceivedDoneEvent.Should().BeTrue();
    }

    [Fact]
    public void ConvertResponsesSseEventToAnthropic_marks_failed_terminal_event()
    {
        var state = new ProxyProtocolBridge.ResponsesToAnthropicStreamState();
        ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.failed",
            """{"type":"response.failed","response":{"error":{"message":"boom"}}}""", state);

        state.Failed.Should().BeTrue();
        state.Completed.Should().BeFalse();
    }

    // ============ 流式：Anthropic → Responses 的签名捕获与 reasoning 项生命周期 ============

    [Fact]
    public void Anthropic_stream_thinking_emits_reasoning_item_with_signature_bridge()
    {
        var state = new ChatToResponsesStreamState { Model = "claude-x" };
        var output = new System.Text.StringBuilder();

        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_start",
            """{"type":"message_start","message":{"id":"msg_1","model":"claude-x","usage":{"input_tokens":10}}}""", state));
        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_start",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""", state));
        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"let me think"}}""", state));
        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-xyz"}}""", state));
        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_start",
            """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""", state));
        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_delta",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"answer"}}""", state));
        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_delta",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}""", state));
        output.Append(ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_stop",
            """{"type":"message_stop"}""", state));

        var text = output.ToString();

        // reasoning 输出项生命周期 + 签名桥接载体
        text.Should().Contain("\"type\":\"reasoning\"");
        text.Should().Contain("aitool-anthropic-thinking-v1:", "signature_delta 必须编码进 encrypted_content 桥接载体");

        // reasoning 项占 output_index 0，message 项顺延到 1
        text.Should().Contain("\"output_index\":0,\"item\":{\"type\":\"reasoning\"");
        text.Should().Contain("\"output_index\":1,\"item\":{\"type\":\"message\"");

        state.Done.Should().BeTrue();
    }

    // ============ F2：SSE 无空格 data: 兼容 ============

    [Fact]
    public void TryExtractSseFieldPayload_accepts_no_space_sse_lines()
    {
        ProxyProtocolBridge.TryExtractSseFieldPayload("data:{\"a\":1}", "data", out var noSpace).Should().BeTrue();
        noSpace.Should().Be("{\"a\":1}");

        ProxyProtocolBridge.TryExtractSseFieldPayload("data: {\"a\":1}", "data", out var withSpace).Should().BeTrue();
        withSpace.Should().Be("{\"a\":1}");

        ProxyProtocolBridge.TryExtractSseFieldPayload("event:message_start", "event", out var eventName).Should().BeTrue();
        eventName.Should().Be("message_start");

        ProxyProtocolBridge.TryExtractSseFieldPayload("id:1", "data", out _).Should().BeFalse();
    }

    [Fact]
    public void Aggregate_streaming_conversion_accepts_no_space_sse_lines()
    {
        // 无空格 data: 是 SSE 规范合法写法，聚合式转换器不能再整行跳过。
        var openAiSse = "data:{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata:{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var anthropicSse = ProxyProtocolBridge.AdaptResponseBodyForClient("Anthropic", "OpenAI", openAiSse, isStreaming: true, "gpt-x", 0, 0, 0);

        anthropicSse.Should().Contain("text_delta", "无空格 data: 行必须被解析而不是跳过");
        anthropicSse.Should().Contain("\"stop_reason\":");
    }

    // ============ F3：Responses 透传不注入 stream_options ============

    [Fact]
    public void Responses_passthrough_does_not_inject_chat_only_stream_options()
    {
        var body = """{ "model": "m", "input": "hi", "stream": true }""";

        var result = ProxyProtocolBridge.PrepareRequestBody("Responses", "Responses", body, "upstream", enableStreaming: true);

        var root = JsonNode.Parse(result)!.AsObject();
        root["stream_options"].Should().BeNull("stream_options 是 Chat Completions 专用字段，原生 Responses 上游会拒绝未知参数");
        root["model"]!.GetValue<string>().Should().Be("upstream");
        root["store"]!.GetValue<bool>().Should().BeFalse();

        // OpenAI 同协议透传仍应注入 include_usage（Chat 流式 token 统计依赖它）。
        var chatBody = """{ "model": "m", "messages": [ { "role": "user", "content": "hi" } ], "stream": true }""";
        var chatResult = JsonNode.Parse(ProxyProtocolBridge.PrepareRequestBody("OpenAI", "OpenAI", chatBody, "upstream", enableStreaming: true))!.AsObject();
        chatResult["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();
    }

    // ============ F5：finish_reason 幂等 ============

    [Fact]
    public void Chat_stream_duplicate_finish_reason_emits_single_completed_event()
    {
        var state = new ChatToResponsesStreamState { Model = "gpt-x" };
        var finishChunk = """{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

        var first = ProxyProtocolBridge.ConvertChatStreamChunkToResponses(finishChunk, state);
        var second = ProxyProtocolBridge.ConvertChatStreamChunkToResponses(finishChunk, state);

        first.Should().Contain("response.completed");
        second.Should().NotContain("response.completed", "重复的 finish_reason 分片不能再发完成事件");
    }

    // ============ F7：cache write 字段族 ============

    [Fact]
    public void ExtractUsageFromElement_reads_cache_write_tokens()
    {
        var usage = """
        {
          "input_tokens": 1000,
          "output_tokens": 50,
          "input_tokens_details": { "cached_tokens": 300, "cached_creation_tokens": 100 }
        }
        """;

        var extracted = ProxyProtocolBridge.ExtractUsageFromElement(JsonDocument.Parse(usage).RootElement, "Responses");

        extracted.InputTokens.Should().Be(600, "缓存读+写都要从 input_tokens 中扣除");
        extracted.CachedTokens.Should().Be(400, "缓存列合并读+写");
        extracted.OutputTokens.Should().Be(50);
    }

    [Fact]
    public void ExtractUsageFromElement_reads_cache_write_tokens_alias()
    {
        var usage = """
        {
          "prompt_tokens": 500,
          "completion_tokens": 20,
          "prompt_tokens_details": { "cached_tokens": 100, "cache_write_tokens": 50 }
        }
        """;

        var extracted = ProxyProtocolBridge.ExtractUsageFromElement(JsonDocument.Parse(usage).RootElement, "OpenAI");

        extracted.InputTokens.Should().Be(350);
        extracted.CachedTokens.Should().Be(150);
        extracted.OutputTokens.Should().Be(20);
    }

    // ============ F8：Responses→Chat 字段补齐 ============

    [Fact]
    public void Responses_to_chat_copies_parallel_tool_calls_service_tier_and_text_format()
    {
        var responsesBody = """
        {
          "model": "m",
          "input": "hi",
          "parallel_tool_calls": false,
          "service_tier": "priority",
          "text": { "format": { "type": "json_schema", "json_schema": { "name": "out", "schema": { "type": "object" } } } }
        }
        """;

        var result = ProxyProtocolBridge.ConvertResponsesRequestToChat(responsesBody, "m", enableStreaming: false);
        var root = JsonNode.Parse(result)!.AsObject();

        root["parallel_tool_calls"]!.GetValue<bool>().Should().BeFalse();
        root["service_tier"]!.GetValue<string>().Should().Be("priority");
        root["response_format"]!["type"]!.GetValue<string>().Should().Be("json_schema");
        root["response_format"]!["json_schema"]!["name"]!.GetValue<string>().Should().Be("out");
    }

    // ============ usage 分桶回归：流式桥出口的 message_delta 三桶不得重复计缓存写 ============

    [Fact]
    public void OpenAi_stream_usage_buckets_stay_split_for_anthropic_egress()
    {
        var state = new ProxyProtocolBridge.AnthropicOpenAiStreamState();

        // usage 同时携带缓存读（30）与缓存写（10），输入 100 含缓存。
        ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{"content":"hi"}}],"usage":{"prompt_tokens":100,"prompt_tokens_details":{"cached_tokens":30,"cached_creation_tokens":10},"completion_tokens":5}}""",
            state);
        var closing = ProxyProtocolBridge.CompleteAnthropicStream(state);

        // 出口三桶：fresh=100-30-10=60，cache_read=30，cache_creation=10；
        // 若 CachedTokens 误用"读+写"合并值，cache_read 会变成 40 并与 cache_creation 重复计费。
        closing.Should().Contain("\"input_tokens\":60");
        closing.Should().Contain("\"cache_read_input_tokens\":30");
        closing.Should().Contain("\"cache_creation_input_tokens\":10");
        closing.Should().Contain("\"output_tokens\":5");

        state.CachedTokens.Should().Be(30);
        state.CacheCreationTokens.Should().Be(10);
    }

    [Fact]
    public void OpenAi_stream_usage_reads_cache_write_alias_and_newapi_output_fallback()
    {
        var state = new ProxyProtocolBridge.AnthropicOpenAiStreamState();

        // cache_write_tokens 别名 + output_tokens=0 回退 completion_tokens（newapi 中间层形态）。
        ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(
            """{"choices":[{"delta":{}}],"usage":{"input_tokens":50,"input_tokens_details":{"cached_tokens":5,"cache_write_tokens":5},"output_tokens":0,"completion_tokens":7}}""",
            state);
        var closing = ProxyProtocolBridge.CompleteAnthropicStream(state);

        closing.Should().Contain("\"input_tokens\":40", "50-5-5=40 新输入");
        closing.Should().Contain("\"cache_read_input_tokens\":5");
        closing.Should().Contain("\"cache_creation_input_tokens\":5");
        closing.Should().Contain("\"output_tokens\":7", "output_tokens=0 时回退 completion_tokens");
    }

    // ============ Responses→Anthropic 流式直转的 item_id 映射 ============

    [Fact]
    public void Responses_stream_function_call_arguments_route_by_output_index()
    {
        var state = new ProxyProtocolBridge.ResponsesToAnthropicStreamState();
        var output = new System.Text.StringBuilder();

        // 两个并行 function_call 输出项，参数增量按 output_index 路由不能串线。
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.output_item.added",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_a","name":"write","arguments":""}}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.output_item.added",
            """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call_b","name":"read","arguments":""}}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.function_call_arguments.delta",
            """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"b\":2}"}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.function_call_arguments.delta",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"a\":1}"}""", state));
        output.Append(ProxyProtocolBridge.ConvertResponsesSseEventToAnthropic("response.completed",
            """{"type":"response.completed","response":{"usage":{"input_tokens":10,"output_tokens":3}}}""", state));
        output.Append(ProxyProtocolBridge.CompleteAnthropicStream(state.Core));

        var text = output.ToString();

        // 解析 content_block_start 事件，建立 call_id → 内容块索引映射。
        var blockIndexById = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text,
                     "\"type\":\"content_block_start\",\"index\":(\\d+),\"content_block\":\\{\"type\":\"tool_use\",\"id\":\"(call_a|call_b)\""))
        {
            blockIndexById[match.Groups[2].Value] = match.Groups[1].Value;
        }

        blockIndexById.Should().HaveCount(2, "两个 function_call 输出项应各开一个 tool_use 块");

        // 参数增量按 output_index 路由：交错到达不能串线（ToJsonString 默认转义引号为 \u0022）。
        var indexA = blockIndexById["call_a"];
        var indexB = blockIndexById["call_b"];
        text.Should().Contain($"\"index\":{indexA},\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":\"{{\\u0022a\\u0022:1}}\"}}",
            "output_index=0 的参数增量必须路由到 call_a 的内容块");
        text.Should().Contain($"\"index\":{indexB},\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":\"{{\\u0022b\\u0022:2}}\"}}",
            "output_index=1 的参数增量必须路由到 call_b 的内容块");
        text.Should().Contain("\"stop_reason\":\"tool_use\"");
    }

    // ============ 状态 failed 的流式与非流式守卫（F4 补充） ============

    [Fact]
    public void BuildAnthropicStreamFromResponses_rejects_failed_stream_and_requires_completed()
    {
        // 没有任何 response.completed 事件的流不能被当作成功响应重建。
        var sseWithoutCompleted = """
        event: response.output_text.delta
        data: {"type":"response.output_text.delta","delta":"partial"}

        """;
        ProxyProtocolBridge.BuildAnthropicStreamFromResponses(sseWithoutCompleted, "gpt-x", 0, 0, 0)
            .Should().BeNullOrEmpty("缺少 response.completed 的流按转换失败处理（保留 fallback）");
    }

    // ============ Codex 远程压缩：compact 端点不强制 stream ============

    [Fact]
    public void Codex_compact_removes_stream_field_instead_of_forcing_it()
    {
        var body = """{ "model": "gpt-5.3-codex", "input": "hi", "stream": true, "temperature": 0.7 }""";

        // 压缩端点只接受非流式：stream 字段被删除（对照 CPA executeCompact），unsupported 参数照常剔除。
        var compact = JsonNode.Parse(ProxyProtocolBridge.PrepareRequestBody(
            "Responses", "Responses", body, "gpt-5.3-codex", enableStreaming: false,
            targetBaseUrl: "https://chatgpt.com/backend-api/codex", isPassthrough: true, isCompact: true))!.AsObject();
        compact["stream"].Should().BeNull("压缩端点不接受流式，stream 字段应被删除");
        compact["temperature"].Should().BeNull("Codex unsupported 参数照常剔除");
        compact["store"]!.GetValue<bool>().Should().BeFalse();

        // 普通 Codex 请求行为不变：仍强制 stream=true（正常对话聚合链路依赖它）。
        var normal = JsonNode.Parse(ProxyProtocolBridge.PrepareRequestBody(
            "Responses", "Responses", body, "gpt-5.3-codex", enableStreaming: false,
            targetBaseUrl: "https://chatgpt.com/backend-api/codex", isPassthrough: true, isCompact: false))!.AsObject();
        normal["stream"]!.GetValue<bool>().Should().BeTrue("普通 Codex 请求仍强制 stream=true");
        normal["temperature"].Should().BeNull();
    }

    [Fact]
    public void Non_codex_compact_keeps_stream_untouched()
    {
        var body = """{ "model": "m", "input": "hi" }""";

        // 非 Codex 的 Responses 站点：compact 不改变 stream 语义（仍只补 store=false）。
        var result = JsonNode.Parse(ProxyProtocolBridge.PrepareRequestBody(
            "Responses", "Responses", body, "m", enableStreaming: false,
            targetBaseUrl: "https://api.openai.com", isPassthrough: true, isCompact: true))!.AsObject();
        result["stream"].Should().BeNull("客户端未带 stream 时不应注入");
        result["store"]!.GetValue<bool>().Should().BeFalse();
    }
}
