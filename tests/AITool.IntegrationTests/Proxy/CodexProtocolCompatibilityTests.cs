using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Protocol;
using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests.Proxy;

public sealed class CodexProtocolCompatibilityTests
{
    [Fact]
    public void Chat_null_usage_details_does_not_break_stream_completion()
    {
        var state = new ChatToResponsesStreamState { DeferChatCompletion = true };
        ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[{"delta":{"content":"hi"}}]}""", state);
        ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""", state);
        var text = ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2,"prompt_tokens_details":null}}""", state);
        state.ConversionFailed.Should().BeFalse();
        var usage = Events(text).Single(e => e["type"]!.ToString() == "response.completed")["response"]!["usage"]!;
        usage["input_tokens"]!.GetValue<int>().Should().Be(10);
        usage["output_tokens"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Additional_tools_definitions_do_not_create_empty_history_messages()
    {
        const string request = """{"input":[{"type":"additional_tools","id":"at","role":"developer","tools":[{"type":"function","name":"run","parameters":{"type":"object"}}]},{"type":"message","role":"user","content":"hello"}]}""";
        var chat = JsonNode.Parse(ProxyProtocolBridge.ConvertResponsesRequestToChat(request, "m", false))!;
        chat["messages"]!.AsArray().Should().HaveCount(1);
        chat["tools"]!.AsArray().Should().HaveCount(1);
        var anthropic = JsonNode.Parse(ProxyProtocolBridge.BuildAnthropicRequestFromResponses(JsonNode.Parse(request)!.AsObject(), "m", false))!;
        anthropic["messages"]!.AsArray().Should().HaveCount(1);
        anthropic["tools"]!.AsArray().Should().HaveCount(1);
    }
    private const string Request = """
    {"model":"m","input":[
      {"type":"additional_tools","id":"at_1","role":"developer","tools":[
        {"type":"namespace","name":"functions","tools":[{"type":"custom","name":"exec","description":"run"}]},
        {"type":"namespace","name":"mcp","tools":[{"type":"function","name":"read","parameters":{"type":"object"}}]}]},
      {"type":"message","role":"user","content":"run"},
      {"type":"custom_tool_call","id":"ct_1","call_id":"c1","name":"exec","namespace":"functions","input":"hello"},
      {"type":"custom_tool_call_output","call_id":"c1","output":"done"},
      {"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"abc"}}
    ]}
    """;

    [Fact]
    public async Task Guard_accepts_complete_codex_items_but_still_resolves_explicit_references()
    {
        var store = new RecordingStore();
        var guard = new UpstreamReferenceGuard(store);
        (await guard.ResolveRequestAsync("client-a", Request)).Should().BeNull();
        await guard.ResolveRequestAsync("client-a", """{"input":[{"type":"item_reference","id":"secret"}]}""");
        store.Count.Should().Be(1);
        var act = () => guard.ResolveRequestAsync("client-a", """{"input":[{"type":"custom_tool_call","id":"ct","name":"exec"}]}""");
        (await act.Should().ThrowAsync<UpstreamReferenceException>()).Which.Code.Should().Be("invalid_reference_shape");
    }

    [Fact]
    public void Chat_and_anthropic_requests_keep_custom_tool_definitions_and_history()
    {
        var chat = JsonNode.Parse(ProxyProtocolBridge.ConvertResponsesRequestToChat(Request, "m", false))!;
        chat["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.ToString()).Should().Contain(["exec", "mcp__read"]);
        var call = chat["messages"]!.AsArray().First(m => m!["tool_calls"] is not null)!;
        call["tool_calls"]![0]!["function"]!["arguments"]!.ToString().Should().Contain("hello");
        chat["messages"]!.AsArray().Should().Contain(m => m!["role"]!.ToString() == "tool" && m["content"]!.ToString() == "done");

        var claude = JsonNode.Parse(ProxyProtocolBridge.BuildAnthropicRequestFromResponses(JsonNode.Parse(Request)!.AsObject(), "m", false))!;
        claude["tools"]!.AsArray().Select(t => t!["name"]!.ToString()).Should().Contain(["exec", "mcp__read"]);
        claude.ToJsonString().Should().Contain("tool_use").And.Contain("tool_result").And.Contain("hello").And.Contain("done");
    }

    [Fact]
    public void Chat_stream_returns_complete_custom_call_and_replayable_output()
    {
        var state = new ChatToResponsesStreamState();
        ProxyProtocolBridge.ConfigureResponsesTools(state, Request);
        var text = ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"id":"c","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"exec","arguments":"{\"input\":\"hello\"}"}}]}}]}""", state);
        text += ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", state);
        var events = Events(text);
        var done = events.Single(e => e["type"]!.ToString() == "response.output_item.done")["item"]!;
        done["type"]!.ToString().Should().Be("custom_tool_call");
        done["input"]!.ToString().Should().Be("hello");
        done["id"]!.ToString().Should().NotBeNullOrEmpty();
        events.Should().Contain(e => e["type"]!.ToString() == "response.custom_tool_call_input.done");
        var output = events.Single(e => e["type"]!.ToString() == "response.completed")["response"]!["output"]!.AsArray();
        output.Should().HaveCount(1);
        output[0]!["call_id"]!.ToString().Should().Be("call_1");
    }

    [Fact]
    public void Chat_deferred_completion_keeps_later_usage_when_finish_chunk_already_contains_usage()
    {
        var state = new ChatToResponsesStreamState { DeferChatCompletion = true };
        ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[{"delta":{"content":"hi"}}]}""", state);
        var first = ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":1}}""", state);
        first.Should().NotContain("response.completed");
        var final = ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5}}""", state);
        var usage = Events(final).Single(e => e["type"]!.ToString() == "response.completed")["response"]!["usage"]!;
        usage["input_tokens"]!.GetValue<int>().Should().Be(100);
        usage["output_tokens"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public void Anthropic_stream_preserves_reasoning_tool_call_and_length_status()
    {
        var state = new ChatToResponsesStreamState();
        ProxyProtocolBridge.ConfigureResponsesTools(state, Request);
        var text = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_start", """{"index":0,"content_block":{"type":"tool_use","id":"c1","name":"exec"}}""", state);
        text += ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_delta", """{"index":0,"delta":{"type":"input_json_delta","partial_json":"{\"input\":\"hello\"}"}}""", state);
        text += ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_delta", """{"delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":3}}""", state);
        text += ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_stop", "{}", state);
        var events = Events(text);
        var response = events.Single(e => e["type"]!.ToString() == "response.incomplete")["response"]!;
        response["incomplete_details"]!["reason"]!.ToString().Should().Be("max_output_tokens");
        response["output"]![0]!["type"]!.ToString().Should().Be("custom_tool_call");
        response["output"]![0]!["input"]!.ToString().Should().Be("hello");
    }

    [Fact]
    public void Anthropic_stream_usage_matches_nonstreaming_including_cache_creation()
    {
        var state = new ChatToResponsesStreamState();
        var text = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_start", """{"message":{"usage":{"input_tokens":100,"cache_read_input_tokens":40,"cache_creation_input_tokens":10}}}""", state);
        text += ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_start", """{"index":0,"content_block":{"type":"text"}}""", state);
        text += ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("content_block_delta", """{"index":0,"delta":{"type":"text_delta","text":"hi"}}""", state);
        text += ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_delta", """{"delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":20}}""", state);
        text += ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses("message_stop", "{}", state);
        var usage = Events(text).Single(e => e["type"]!.ToString() == "response.completed")["response"]!["usage"]!;
        usage["input_tokens"]!.GetValue<int>().Should().Be(150);
        usage["total_tokens"]!.GetValue<int>().Should().Be(170);
        usage["input_tokens_details"]!["cached_creation_tokens"]!.GetValue<int>().Should().Be(10);
    }

    [Fact]
    public void Non_streaming_outputs_restore_custom_tool_type()
    {
        const string chat = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c","type":"function","function":{"name":"exec","arguments":"{\"input\":\"patch\"}"}}]},"finish_reason":"tool_calls"}]}""";
        const string claude = """{"content":[{"type":"tool_use","id":"c","name":"exec","input":{"input":"patch"}}],"stop_reason":"tool_use"}""";
        foreach (var result in new[] { ProxyProtocolBridge.ConvertChatResponseToResponses(chat, Request), ProxyProtocolBridge.ConvertAnthropicResponseToResponses(claude, Request) })
        {
            var item = JsonNode.Parse(result)!["output"]![0]!;
            item["type"]!.ToString().Should().Be("custom_tool_call");
            item["input"]!.ToString().Should().Be("patch");
        }
    }

    [Fact]
    public void Local_compaction_round_trips_summary_and_rejects_incomplete_summary()
    {
        const string response = """{"id":"resp_1","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Remember the fix"}]}]}""";
        var compact = JsonNode.Parse(ProxyProtocolBridge.BuildCompactResponse(response)!)!;
        var request = new JsonObject { ["input"] = compact["output"]!.DeepClone() }.ToJsonString();
        var expanded = ProxyProtocolBridge.ExpandLocalCompactions(request);
        expanded.Should().Contain("Remember the fix").And.NotContain("encrypted_content");
        ProxyProtocolBridge.ContainsOpaqueCompaction(expanded).Should().BeFalse();
        ProxyProtocolBridge.ContainsOpaqueCompaction("""{"input":[{"type":"compaction","encrypted_content":"foreign"}]}""").Should().BeTrue();
        ProxyProtocolBridge.BuildCompactResponse(response.Replace("completed", "incomplete")).Should().BeNull();
    }

    [Fact]
    public void Anthropic_redacted_thinking_survives_responses_roundtrip()
    {
        const string source = """{"content":[{"type":"redacted_thinking","data":"opaque"},{"type":"text","text":"done"}],"stop_reason":"end_turn"}""";
        var response = JsonNode.Parse(ProxyProtocolBridge.ConvertAnthropicResponseToResponses(source))!;
        var request = new JsonObject { ["input"] = response["output"]!.DeepClone() };
        var anthropic = ProxyProtocolBridge.BuildAnthropicRequestFromResponses(request, "m", false);
        anthropic.Should().Contain("redacted_thinking").And.Contain("opaque");
    }

    [Fact]
    public void Anthropic_request_has_required_output_limit_and_valid_thinking_budget()
    {
        var request = JsonNode.Parse("""{"input":"hi","reasoning":{"effort":"max"}}""")!.AsObject();
        var result = JsonNode.Parse(ProxyProtocolBridge.BuildAnthropicRequestFromResponses(request, "m", false))!;
        result["max_tokens"]!.GetValue<int>().Should().Be(8192);
        result["thinking"]!["budget_tokens"]!.GetValue<int>().Should().BeLessThan(8192);
    }

    [Fact]
    public void Codex_reasoning_history_can_be_retained_for_chat_upstreams_that_require_it()
    {
        const string request = """{"input":[{"type":"reasoning","summary":[{"type":"summary_text","text":"thought"}]},{"type":"function_call","call_id":"c","name":"run","arguments":"{}"},{"type":"function_call_output","call_id":"c","output":"ok"}]}""";
        var body = ProxyProtocolBridge.ConvertResponsesRequestToChat(request, "m", false, keepReasoning: true);
        var assistant = JsonNode.Parse(body)!["messages"]![0]!;
        assistant["reasoning_content"]!.ToString().Should().Be("thought");
        assistant["tool_calls"]![0]!["id"]!.ToString().Should().Be("c");
    }

    [Theory]
    [InlineData("length", "response.incomplete")]
    [InlineData("error", "response.failed")]
    public void Chat_stream_does_not_mark_truncation_or_failure_as_completed(string reason, string terminal)
    {
        var state = new ChatToResponsesStreamState();
        var text = ProxyProtocolBridge.ConvertChatStreamChunkToResponses("""{"choices":[{"delta":{"content":"hello"}}]}""", state);
        text += ProxyProtocolBridge.ConvertChatStreamChunkToResponses($$"""{"choices":[{"delta":{},"finish_reason":"{{reason}}"}]}""", state);
        var events = Events(text);
        events.Should().Contain(e => e["type"]!.ToString() == terminal);
        events.Should().NotContain(e => e["type"]!.ToString() == "response.completed");
    }

    private static List<JsonNode> Events(string text) => text.Split('\n').Where(x => x.StartsWith("data: "))
        .Select(x => JsonNode.Parse(x[6..])!).ToList();

    private sealed class RecordingStore : IUpstreamReferenceOwnerStore
    {
        public int Count { get; private set; }
        public Task<UpstreamReferenceRestriction?> ResolveAsync(string clientScope, IReadOnlyCollection<UpstreamReferenceLookup> references, CancellationToken ct = default)
        { Count = references.Count; return Task.FromResult<UpstreamReferenceRestriction?>(null); }
        public Task CommitAsync(UpstreamReferenceContext context, IReadOnlyCollection<UpstreamReferenceLookup> references, bool native, CancellationToken ct = default) => Task.CompletedTask;
    }
}
