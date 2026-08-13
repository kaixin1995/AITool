using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Protocol;
using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 覆盖三种协议响应桥接中的空响应、推理、拒答和工具调用边界。
/// </summary>
public sealed class ProxyProtocolBridgeResponseConversionTests
{
    [Fact]
    public void Chat_response_with_reasoning_refusal_and_legacy_function_call_maps_to_responses()
    {
        var body = """
        {
          "id":"chatcmpl-test",
          "model":"test-model",
          "created":1,
          "choices":[{"message":{
            "content":null,
            "reasoning_content":"先思考",
            "refusal":"不能执行",
            "function_call":{"name":"lookup","arguments":"{\"q\":\"x\"}"}
          },"finish_reason":"function_call"}]
        }
        """;

        var converted = ProxyProtocolBridge.ConvertChatResponseToResponses(body);
        using var document = JsonDocument.Parse(converted);
        var output = document.RootElement.GetProperty("output");

        output.EnumerateArray().Select(x => x.GetProperty("type").GetString())
            .Should().ContainInOrder("reasoning", "message", "function_call");
        output[1].GetProperty("content")[0].GetProperty("type").GetString().Should().Be("refusal");
        output[2].GetProperty("name").GetString().Should().Be("lookup");
        output[2].GetProperty("arguments").GetString().Should().Contain("\"q\"");
    }

    [Fact]
    public void Chat_response_with_null_content_and_no_other_output_is_rejected()
    {
        var body = "{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"stop\"}]}";

        ProxyProtocolBridge.ConvertChatResponseToResponses(body).Should().BeEmpty();
    }

    [Fact]
    public void Responses_response_with_empty_output_is_rejected()
    {
        ProxyProtocolBridge.ConvertResponsesResponseToChat(
            "{\"id\":\"resp-empty\",\"output\":[],\"status\":\"completed\"}",
            "test-model", 0, 0, 0).Should().BeEmpty();
    }

    [Fact]
    public void Responses_non_streaming_response_extracts_output_text_to_chat_content()
    {
        // 真实 CPA 上游非流式 Responses 返回的结构（已脱敏）
        // 核心字段：output[].content[].type=output_text, text=实际回复
        var body = """
        {
          "id":"resp_023316174082d7a9016a7cc4e382748199847d20d120dd066d",
          "object":"response",
          "created_at":1786561763,
          "status":"completed",
          "error":null,
          "model":"gpt-5.6-luna",
          "output":[
            {
              "id":"msg_023316174082d7a9016a7cc4e3fa7c8199abde524ded7a72b7",
              "type":"message",
              "status":"completed",
              "content":[{"type":"output_text","annotations":[],"logprobs":[],"text":"Hi! How can I help?"}],
              "phase":"final_answer",
              "role":"assistant"
            }
          ],
          "usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}
        }
        """;

        var converted = ProxyProtocolBridge.ConvertResponsesResponseToChat(body, "gpt-5.6-luna", 5, 0, 2);
        converted.Should().NotBeEmpty("非流式 Responses 响应含 output_text，必须能转成 Chat 格式");

        using var document = JsonDocument.Parse(converted);
        var root = document.RootElement;
        root.GetProperty("object").GetString().Should().Be("chat.completion");
        var message = root.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("role").GetString().Should().Be("assistant");
        // 核心断言：上游 output_text.text 必须成为 chat content
        message.GetProperty("content").GetString().Should().Be("Hi! How can I help?");
    }

    [Fact]
    public void Responses_response_with_function_call_maps_to_chat_tool_call()
    {
        var body = """
        {
          "id":"resp-tool",
          "model":"test-model",
          "created_at":1,
          "output":[{"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{\"q\":\"x\"}"}]
        }
        """;

        var converted = ProxyProtocolBridge.ConvertResponsesResponseToChat(body, "test-model", 0, 0, 0);
        using var document = JsonDocument.Parse(converted);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("tool_calls")[0].GetProperty("id").GetString().Should().Be("call-1");
        message.GetProperty("content").GetString().Should().BeEmpty();
    }

    [Fact]
    public void Responses_stream_keeps_state_and_emits_done_only_once()
    {
        var state = new ResponsesToChatStreamState { Model = "test-model" };
        var first = ProxyProtocolBridge.ConvertResponsesStreamingToChat(
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}\n\n",
            state);
        var second = ProxyProtocolBridge.ConvertResponsesStreamingToChat(
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"model\":\"test-model\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}}\n\n",
            state);
        var duplicate = ProxyProtocolBridge.ConvertResponsesStreamingToChat(
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n",
            state);

        first.Should().Contain("hi");
        second.Should().Contain("[DONE]");
        duplicate.Should().BeEmpty();
    }

    [Fact]
    public void Anthropic_metadata_event_does_not_start_responses_stream()
    {
        var state = new ChatToResponsesStreamState { Model = "test-model" };

        var converted = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(
            "ping",
            "{\"type\":\"ping\"}",
            state);

        converted.Should().BeEmpty();
        state.ResponseStarted.Should().BeFalse();
        state.ConversionFailed.Should().BeFalse();
    }

    [Fact]
    public void Anthropic_empty_message_stop_is_rejected()
    {
        var state = new ChatToResponsesStreamState { Model = "test-model" };

        var converted = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(
            "message_stop",
            "{\"type\":\"message_stop\"}",
            state);

        converted.Should().BeEmpty();
        state.ConversionFailed.Should().BeTrue();
    }

    [Fact]
    public void Anthropic_tool_arguments_keep_their_content_block_index()
    {
        var state = new ChatToResponsesStreamState { Model = "test-model" };
        var start = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call-2\",\"name\":\"lookup\"}}",
            state);
        var delta = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(
            "content_block_delta",
            "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"q\\\":\\\"x\\\"}\"}}",
            state);

        start.Should().Contain("call-2");
        delta.Should().Contain("response.function_call_arguments.delta");
        delta.Should().Contain("q");
    }

    [Fact]
    public void Chat_stream_with_null_usage_keeps_converting_content_and_reasoning()
    {
        var state = new ChatToResponsesStreamState { Model = "test-model" };

        var converted = ProxyProtocolBridge.ConvertChatStreamChunkToResponses(
            "{\"id\":\"chatcmpl-test\",\"model\":\"test-model\",\"choices\":[{\"delta\":{\"reasoning_content\":\"先思考\",\"content\":\"正文\"}}],\"usage\":null}",
            state);

        converted.Should().Contain("response.reasoning_summary_text.delta");
        converted.Should().Contain("response.output_text.delta");
        state.ConversionFailed.Should().BeFalse();
    }

    [Fact]
    public void Responses_tool_output_index_maps_to_contiguous_chat_index()
    {
        var state = new ResponsesToChatStreamState { Model = "test-model" };
        var first = ProxyProtocolBridge.ConvertResponsesStreamingToChat(
            "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":4,\"item\":{\"type\":\"function_call\",\"call_id\":\"call-4\",\"name\":\"first\"}}\n\n",
            state);
        var second = ProxyProtocolBridge.ConvertResponsesStreamingToChat(
            "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":9,\"item\":{\"type\":\"function_call\",\"call_id\":\"call-9\",\"name\":\"second\"}}\n\n",
            state);

        first.Should().Contain("\"index\":0");
        second.Should().Contain("\"index\":1");
    }
}
