using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Protocol;
using FluentAssertions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// Gemini（GeminiCLI / Antigravity 上游）协议桥回归测试：
/// 请求方向（Anthropic/OpenAI/Responses → Gemini 封套）、响应方向（Gemini → Anthropic/OpenAI/Responses）、
/// SSE 状态机、思考等级强覆盖（受保护功能）与 Antigravity CLI 封套。行为对齐 gcli2api。
/// </summary>
public sealed class ProxyProtocolBridgeGeminiTests
{
    private const string GeminiCliBaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string AntigravityBaseUrl = "https://daily-cloudcode-pa.googleapis.com";

    private static JsonObject ParseEnvelope(string body)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        root.ContainsKey("request").Should().BeTrue("Gemini 上游请求必须为 {model, project, request} 封套");
        return root;
    }

    // ============ 请求方向：Anthropic → Gemini ============

    [Fact]
    public void PrepareRequestBody_anthropic_to_gemini_builds_envelope_and_contents()
    {
        var anthropicBody = """
        {
          "model": "route-entry",
          "system": "you are helpful",
          "max_tokens": 2048,
          "temperature": 0.3,
          "top_p": 0.9,
          "messages": [
            { "role": "user", "content": [ { "type": "text", "text": "hi" } ] },
            { "role": "assistant", "content": [
              { "type": "thinking", "thinking": "internal thought", "thoughtSignature": "sig-1" },
              { "type": "text", "text": "calling tool" },
              { "type": "tool_use", "id": "toolu_1", "name": "write", "input": { "path": "a.txt" } }
            ] },
            { "role": "user", "content": [
              { "type": "tool_result", "tool_use_id": "toolu_1", "content": "ok" },
              { "type": "text", "text": "continue" }
            ] }
          ],
          "tools": [ { "name": "write", "description": "write file", "input_schema": { "type": "object", "properties": { "path": { "type": "string" } }, "required": [ "path" ] } } ],
          "tool_choice": { "type": "auto" }
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-2.5-pro", enableStreaming: false,
            targetBaseUrl: GeminiCliBaseUrl, geminiProjectId: "my-project");

        var envelope = ParseEnvelope(result);
        envelope["model"]!.GetValue<string>().Should().Be("gemini-2.5-pro");
        envelope["project"]!.GetValue<string>().Should().Be("my-project");

        var request = envelope["request"]!.AsObject();
        // system → systemInstruction
        request["systemInstruction"]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("you are helpful");

        var contents = request["contents"]!.AsArray();
        // 轮次归一合并后（保证严格 user ↔ model 交替，对齐 Google Gemini 规范）：
        // user(hi) → model(calling tool + functionCall) → user(functionResponse + continue)
        contents.Should().HaveCount(3);
        contents[0]!["role"]!.GetValue<string>().Should().Be("user");
        contents[0]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("hi");
        
        contents[1]!["role"]!.GetValue<string>().Should().Be("model");
        contents[1]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("calling tool");
        var functionCallPart = contents[1]!["parts"]!.AsArray()[1]!.AsObject();
        functionCallPart["functionCall"]!["name"]!.GetValue<string>().Should().Be("write");
        functionCallPart["functionCall"]!["id"]!.GetValue<string>().Should().Be("toolu_1");
        functionCallPart["functionCall"]!["args"]!["path"]!.GetValue<string>().Should().Be("a.txt");
        // functionCall 部件必须带官方跳过校验占位符（中转场景真实签名不可信）。
        functionCallPart["thoughtSignature"]!.GetValue<string>().Should().Be(ProxyProtocolBridge.SkipThoughtSignatureValidator);

        contents[2]!["role"]!.GetValue<string>().Should().Be("user");
        contents[2]!["parts"]!.AsArray()[0]!["functionResponse"]!["name"]!.GetValue<string>().Should().Be("write");
        contents[2]!["parts"]!.AsArray()[0]!["functionResponse"]!["id"]!.GetValue<string>().Should().Be("toolu_1");
        contents[2]!["parts"]!.AsArray()[1]!["text"]!.GetValue<string>().Should().Be("continue");

        // generationConfig：强制 maxOutputTokens=64000 / topK=64（gcli2api 同款）
        var config = request["generationConfig"]!.AsObject();
        config["maxOutputTokens"]!.GetValue<int>().Should().Be(64000);
        config["topK"]!.GetValue<int>().Should().Be(64);
        config["temperature"]!.GetValue<double>().Should().Be(0.3);

        // tools → functionDeclarations（parametersJsonSchema 保留 required）
        var tools = request["tools"]!.AsArray();
        var declaration = tools[0]!["functionDeclarations"]!.AsArray()[0]!.AsObject();
        declaration["name"]!.GetValue<string>().Should().Be("write");
        var schema = declaration["parameters"]!.AsObject();
        schema["type"]!.GetValue<string>().Should().Be("object");
        schema["required"]!.AsArray().Should().HaveCount(1);

        // tool_choice auto → AUTO
        request["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>().Should().Be("AUTO");

        // safetySettings：GeminiCLI 保留 BLOCK_NONE（10 类）
        var safety = request["safetySettings"]!.AsArray();
        safety.Should().HaveCount(10);
        safety[0]!["threshold"]!.GetValue<string>().Should().Be("BLOCK_NONE");
    }

    [Fact]
    public void PrepareRequestBody_anthropic_thinking_maps_to_thinking_config()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "thinking": { "type": "enabled", "budget_tokens": 8000 },
          "messages": [ { "role": "user", "content": "hi" } ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-2.5-pro", enableStreaming: false, targetBaseUrl: GeminiCliBaseUrl);

        var request = ParseEnvelope(result)["request"]!.AsObject();
        var thinking = request["generationConfig"]!["thinkingConfig"]!.AsObject();
        thinking["thinkingBudget"]!.GetValue<int>().Should().Be(8000);
        thinking["includeThoughts"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void PrepareRequestBody_anthropic_thinking_on_gemini3_converts_budget_to_level()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "thinking": { "type": "enabled", "budget_tokens": 24000 },
          "messages": [ { "role": "user", "content": "hi" } ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-3-pro-preview", enableStreaming: false, targetBaseUrl: GeminiCliBaseUrl);

        var request = ParseEnvelope(result)["request"]!.AsObject();
        var thinking = request["generationConfig"]!["thinkingConfig"]!.AsObject();
        // gemini-3 不支持 thinkingBudget：预算 >=16000 映射为 high 等级。
        thinking.ContainsKey("thinkingBudget").Should().BeFalse();
        thinking["thinkingLevel"]!.GetValue<string>().Should().Be("high");
        thinking["includeThoughts"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void PrepareRequestBody_anthropic_image_base64_maps_to_inline_data()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "messages": [ { "role": "user", "content": [
            { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "aGVsbG8=" } },
            { "type": "text", "text": "what is this" }
          ] } ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-2.5-flash", enableStreaming: false, targetBaseUrl: GeminiCliBaseUrl);

        // 单条 user 消息内的多个 part（图片+文本）保持在同一个 user 轮次中。
        var contents = ParseEnvelope(result)["request"]!["contents"]!.AsArray();
        contents.Should().HaveCount(1);
        contents[0]!["parts"]!.AsArray()[0]!["inlineData"]!["mimeType"]!.GetValue<string>().Should().Be("image/png");
        contents[0]!["parts"]!.AsArray()[0]!["inlineData"]!["data"]!.GetValue<string>().Should().Be("aGVsbG8=");
        contents[0]!["parts"]!.AsArray()[1]!["text"]!.GetValue<string>().Should().Be("what is this");
    }

    // ============ 请求方向：OpenAI → Gemini ============

    [Fact]
    public void PrepareRequestBody_openai_to_gemini_maps_tools_and_tool_results()
    {
        var openAiBody = """
        {
          "model": "gpt-x",
          "messages": [
            { "role": "system", "content": "be brief" },
            { "role": "user", "content": "read the file" },
            { "role": "assistant", "content": null, "tool_calls": [
              { "id": "call_1", "type": "function", "function": { "name": "read", "arguments": "{\"path\":\"a.txt\"}" } }
            ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "file body" },
            { "role": "user", "content": "summarize" }
          ],
          "tools": [ { "type": "function", "function": { "name": "read", "description": "read", "parameters": { "type": "object", "properties": { "path": { "type": "string" } } } } } ],
          "tool_choice": "auto",
          "reasoning_effort": "medium"
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", openAiBody, "gemini-2.5-pro", enableStreaming: true,
            targetBaseUrl: GeminiCliBaseUrl, geminiProjectId: "p1");

        var envelope = ParseEnvelope(result);
        var request = envelope["request"]!.AsObject();
        request["systemInstruction"]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("be brief");

        var contents = request["contents"]!.AsArray();
        // 轮次归一后连续 user 轮合并：user(text) → model(functionCall) → user(functionResponse + text)
        contents.Should().HaveCount(3);
        var callPart = contents[1]!["parts"]!.AsArray()[0]!.AsObject();
        callPart["functionCall"]!["name"]!.GetValue<string>().Should().Be("read");
        callPart["functionCall"]!["args"]!["path"]!.GetValue<string>().Should().Be("a.txt");
        var responsePart = contents[2]!["parts"]!.AsArray()[0]!.AsObject();
        responsePart["functionResponse"]!["name"]!.GetValue<string>().Should().Be("read");
        responsePart["functionResponse"]!["id"]!.GetValue<string>().Should().Be("call_1");
        responsePart["functionResponse"]!["response"]!["result"]!.GetValue<string>().Should().Be("file body");
        contents[2]!["parts"]!.AsArray()[1]!["text"]!.GetValue<string>().Should().Be("summarize");

        // reasoning_effort=medium → thinkingBudget 8192
        var thinking = request["generationConfig"]!["thinkingConfig"]!.AsObject();
        thinking["thinkingBudget"]!.GetValue<int>().Should().Be(8192);
    }

    // ============ 请求方向：Responses → Gemini（经 Anthropic 桥链转）============

    [Fact]
    public void PrepareRequestBody_responses_to_gemini_chains_through_anthropic()
    {
        var responsesBody = """
        {
          "model": "gpt-x",
          "instructions": "be nice",
          "input": [
            { "type": "message", "role": "user", "content": "hello" }
          ],
          "max_output_tokens": 512
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Responses", "Gemini", responsesBody, "gemini-2.5-pro", enableStreaming: false, targetBaseUrl: GeminiCliBaseUrl);

        var envelope = ParseEnvelope(result);
        var request = envelope["request"]!.AsObject();
        request["systemInstruction"]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("be nice");
        request["contents"]!.AsArray()[0]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("hello");
    }

    // ============ 思考等级强覆盖（受保护功能）============

    [Fact]
    public void PrepareRequestBody_gemini_effort_override_wins_over_client_thinking()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "thinking": { "type": "enabled", "budget_tokens": 48000 },
          "messages": [ { "role": "user", "content": "hi" } ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-2.5-pro", enableStreaming: false,
            overrideReasoningEffort: "low", targetBaseUrl: GeminiCliBaseUrl);

        var thinking = ParseEnvelope(result)["request"]!["generationConfig"]!["thinkingConfig"]!.AsObject();
        thinking["thinkingBudget"]!.GetValue<int>().Should().Be(1024, "覆盖值 low 必须盖过客户端 thinking.budget_tokens");
    }

    [Fact]
    public void PrepareRequestBody_gemini3_effort_override_uses_thinking_level()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "messages": [ { "role": "user", "content": "hi" } ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-3-pro-preview", enableStreaming: false,
            overrideReasoningEffort: "low", targetBaseUrl: GeminiCliBaseUrl);

        var thinking = ParseEnvelope(result)["request"]!["generationConfig"]!["thinkingConfig"]!.AsObject();
        thinking["thinkingLevel"]!.GetValue<string>().Should().Be("low");
        // gemini-3 覆盖等级时只写 thinkingLevel，不再写 thinkingBudget。
        thinking.ContainsKey("thinkingBudget").Should().BeFalse();
    }

    // ============ Antigravity CLI 封套 ============

    [Fact]
    public void PrepareRequestBody_antigravity_wrap_adds_cli_fields_and_strips_unsupported()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "stop_sequences": [ "END" ],
          "messages": [
            { "role": "user", "content": "hello antigravity" },
            { "role": "assistant", "content": "partial" }
          ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "claude-sonnet-4-5", enableStreaming: false, targetBaseUrl: AntigravityBaseUrl);

        var envelope = ParseEnvelope(result);
        envelope["userAgent"]!.GetValue<string>().Should().Be("antigravity");
        envelope["requestType"]!.GetValue<string>().Should().Be("agent");
        envelope["requestId"]!.GetValue<string>().Should().StartWith("agent/");

        var request = envelope["request"]!.AsObject();
        // CLI 不发送 safetySettings；stopSequences/presencePenalty/frequencyPenalty 剔除。
        request.ContainsKey("safetySettings").Should().BeFalse();
        var config = request["generationConfig"]!.AsObject();
        config.ContainsKey("stopSequences").Should().BeFalse();
        config.ContainsKey("presencePenalty").Should().BeFalse();
        config.ContainsKey("frequencyPenalty").Should().BeFalse();

        // sessionId + labels 注入。
        request["sessionId"].Should().NotBeNull();
        request["labels"]!["model_enum"]!.GetValue<string>().Should().Be("claude-sonnet-4-5");
        request["labels"]!["used_claude"]!.GetValue<string>().Should().Be("true");
        // toolConfig 默认 VALIDATED。
        request["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>().Should().Be("VALIDATED");

        // claude 系列不支持预填充：末尾 model 消息剥离。
        var contents = request["contents"]!.AsArray();
        contents[^1]!["role"]!.GetValue<string>().Should().Be("user");
    }

    // ============ 响应方向：Gemini → Anthropic（非流式）============

    [Fact]
    public void BuildAnthropicResponseFromGemini_maps_blocks_usage_and_stop_reason()
    {
        var geminiBody = """
        {
          "response": {
            "candidates": [ {
              "content": { "role": "model", "parts": [
                { "text": "thinking...", "thought": true, "thoughtSignature": "sig-abc" },
                { "text": "answer text" },
                { "functionCall": { "id": "fc_1", "name": "write", "args": { "path": "a.txt", "extra": null } } }
              ] },
              "finishReason": "STOP"
            } ],
            "usageMetadata": { "promptTokenCount": 120, "cachedContentTokenCount": 40, "candidatesTokenCount": 30, "thoughtsTokenCount": 10 }
          }
        }
        """;

        var result = ProxyProtocolBridge.BuildAnthropicResponseFromGemini(geminiBody, "gemini-2.5-pro");
        result.Should().NotBeNull();
        var root = JsonNode.Parse(result!)!.AsObject();
        root["type"]!.GetValue<string>().Should().Be("message");
        root["stop_reason"]!.GetValue<string>().Should().Be("tool_use");

        var content = root["content"]!.AsArray();
        content.Should().HaveCount(3);
        content[0]!["type"]!.GetValue<string>().Should().Be("thinking");
        content[0]!["thoughtSignature"]!.GetValue<string>().Should().Be("sig-abc");
        content[1]!["type"]!.GetValue<string>().Should().Be("text");
        content[2]!["type"]!.GetValue<string>().Should().Be("tool_use");
        content[2]!["input"]!["path"]!.GetValue<string>().Should().Be("a.txt");
        content[2]!["input"]!.AsObject().ContainsKey("extra").Should().BeFalse("tool_use 入参中的 null 字段需剔除");
        var usage = root["usage"]!.AsObject();
        usage["input_tokens"]!.GetValue<int>().Should().Be(80);
        usage["cache_read_input_tokens"]!.GetValue<int>().Should().Be(40);
        usage["output_tokens"]!.GetValue<int>().Should().Be(40);
    }

    [Fact]
    public void BuildAnthropicResponseFromGemini_max_tokens_maps_stop_reason()
    {
        var geminiBody = """
        {
          "candidates": [ {
            "content": { "role": "model", "parts": [ { "text": "partial" } ] },
            "finishReason": "MAX_TOKENS"
          } ]
        }
        """;

        var result = ProxyProtocolBridge.BuildAnthropicResponseFromGemini(geminiBody, "m");
        JsonNode.Parse(result!)!["stop_reason"]!.GetValue<string>().Should().Be("max_tokens");
    }

    [Fact]
    public void BuildAnthropicResponseFromGemini_filters_skip_signature_placeholder()
    {
        var geminiBody = """
        {
          "candidates": [ {
            "content": { "role": "model", "parts": [
              { "text": "...", "thoughtSignature": "skip_thought_signature_validator" },
              { "text": "real answer" }
            ] },
            "finishReason": "STOP"
          } ]
        }
        """;

        var result = ProxyProtocolBridge.BuildAnthropicResponseFromGemini(geminiBody, "m");
        var content = JsonNode.Parse(result!)!["content"]!.AsArray();
        content.Should().HaveCount(1);
        content[0]!["text"]!.GetValue<string>().Should().Be("real answer");
    }

    // ============ 响应方向：Gemini → OpenAI（非流式）============

    [Fact]
    public void BuildOpenAiResponseFromGemini_maps_message_and_tool_calls()
    {
        var geminiBody = """
        {
          "response": {
            "candidates": [ {
              "content": { "role": "model", "parts": [
                { "text": "deep thought", "thought": true },
                { "text": "the answer" },
                { "functionCall": { "id": "fc_9", "name": "read", "args": { "path": "x" } } }
              ] },
              "finishReason": "STOP"
            } ],
            "usageMetadata": { "promptTokenCount": 50, "cachedContentTokenCount": 10, "candidatesTokenCount": 5 }
          }
        }
        """;

        var result = ProxyProtocolBridge.BuildOpenAiResponseFromGemini(geminiBody, "gemini-2.5-flash");
        result.Should().NotBeNull();
        var root = JsonNode.Parse(result!)!.AsObject();
        root["object"]!.GetValue<string>().Should().Be("chat.completion");
        var choice = root["choices"]!.AsArray()[0]!.AsObject();
        choice["finish_reason"]!.GetValue<string>().Should().Be("tool_calls");
        var message = choice["message"]!.AsObject();
        message["content"]!.GetValue<string>().Should().Be("the answer");
        message["reasoning_content"]!.GetValue<string>().Should().Be("deep thought");
        message["tool_calls"]!.AsArray()[0]!["function"]!["name"]!.GetValue<string>().Should().Be("read");
        root["usage"]!["prompt_tokens"]!.GetValue<int>().Should().Be(40);
        root["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>().Should().Be(10);
    }

    // ============ 流式：Gemini SSE → Anthropic 事件 ============

    [Fact]
    public void ConvertGeminiSseChunkToAnthropic_emits_full_event_sequence()
    {
        var state = new ProxyProtocolBridge.GeminiToAnthropicStreamState();
        var output = new System.Text.StringBuilder();

        output.Append(ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(
            """{ "response": { "candidates": [ { "content": { "parts": [ { "text": "let me think", "thought": true } ] } } ] } }""",
            "gemini-2.5-pro", state));
        output.Append(ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(
            """{ "response": { "candidates": [ { "content": { "parts": [ { "text": "final answer" } ] } } ] } }""",
            "gemini-2.5-pro", state));
        output.Append(ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(
            """{ "response": { "candidates": [ { "content": { "parts": [ { "functionCall": { "id": "fc_1", "name": "write", "args": { "path": "a" } } } ] }, "finishReason": "STOP" } ], "usageMetadata": { "promptTokenCount": 30, "cachedContentTokenCount": 5, "candidatesTokenCount": 7 } } }""",
            "gemini-2.5-pro", state));
        output.Append(ProxyProtocolBridge.CompleteGeminiToAnthropicStream(state));

        var text = output.ToString();
        text.Should().Contain("event: message_start");
        text.Should().Contain("\"thinking_delta\"");
        text.Should().Contain("\"text_delta\"");
        text.Should().Contain("\"final answer\"");
        text.Should().Contain("\"tool_use\"");
        text.Should().Contain("\"input_json_delta\"");
        text.Should().Contain("event: content_block_stop");
        text.Should().Contain("event: message_delta");
        text.Should().Contain("event: message_stop");
        // usage：input = 30-5=25，output = 7。
        text.Should().Contain("\"input_tokens\":25");
        text.Should().Contain("\"output_tokens\":7");
        text.Should().Contain("\"cache_read_input_tokens\":5");
        // STOP + tool_use → tool_use 停止原因。
        text.Should().Contain("\"stop_reason\":\"tool_use\"");
    }

    [Fact]
    public void ConvertGeminiSseChunkToAnthropic_splits_thinking_block_on_signature_change()
    {
        var state = new ProxyProtocolBridge.GeminiToAnthropicStreamState();
        var first = ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(
            """{ "candidates": [ { "content": { "parts": [ { "text": "part1", "thought": true, "thoughtSignature": "sig1" } ] } } ] }""",
            "m", state);
        var second = ProxyProtocolBridge.ConvertGeminiSseChunkToAnthropic(
            """{ "candidates": [ { "content": { "parts": [ { "text": "part2", "thought": true, "thoughtSignature": "sig2" } ] } } ] }""",
            "m", state);

        var firstText = first.ToString();
        var secondText = second.ToString();
        firstText.Should().Contain("\"thoughtSignature\":\"sig1\"");
        secondText.Should().Contain("\"thoughtSignature\":\"sig2\"");
        // 签名变化必须关闭旧块再开新块。
        secondText.Should().Contain("event: content_block_stop", "签名变化时先关闭上一个 thinking 块");
    }

    // ============ 流式：Gemini SSE → OpenAI 聚合 ============

    [Fact]
    public void BuildOpenAiStreamingResponseFromGemini_produces_chunks_with_done()
    {
        var sseText = string.Join("\n\n",
            "data: " + """{ "response": { "candidates": [ { "content": { "parts": [ { "text": "hello " } ] } } ] } }""",
            "data: " + """{ "response": { "candidates": [ { "content": { "parts": [ { "text": "world" } ] }, "finishReason": "STOP" } ], "usageMetadata": { "promptTokenCount": 9, "candidatesTokenCount": 2 } } }""",
            "");

        var result = ProxyProtocolBridge.BuildOpenAiStreamingResponseFromGemini(sseText, "gemini-2.5-flash");
        result.Should().NotBeNull();
        result.Should().Contain("\"content\":\"hello \"");
        result.Should().Contain("\"content\":\"world\"");
        result.Should().Contain("\"finish_reason\":\"stop\"");
        result.Should().Contain("data: [DONE]");
        result.Should().Contain("\"prompt_tokens\":9");
    }

    // ============ 响应分派：AdaptResponseBodyForClient ============

    [Fact]
    public void AdaptResponseBodyForClient_dispatches_gemini_for_all_client_protocols()
    {
        var geminiBody = """
        {
          "candidates": [ { "content": { "role": "model", "parts": [ { "text": "hi" } ] }, "finishReason": "STOP" } ]
        }
        """;

        var anthropic = ProxyProtocolBridge.AdaptResponseBodyForClient("Anthropic", "Gemini", geminiBody, false, "m", 0, 0, 0);
        anthropic.Should().Contain("\"type\":\"message\"");

        var openAi = ProxyProtocolBridge.AdaptResponseBodyForClient("OpenAI", "Gemini", geminiBody, false, "m", 0, 0, 0);
        openAi.Should().Contain("\"chat.completion\"");

        var responses = ProxyProtocolBridge.AdaptResponseBodyForClient("Responses", "Gemini", geminiBody, false, "m", 0, 0, 0);
        responses.Should().Contain("\"status\":\"completed\"", "Responses 客户端经 Anthropic 桥链转为完成态响应");
        responses.Should().Contain("hi");
    }

    [Fact]
    public void AdaptResponseBodyForClient_gemini_stream_aggregates()
    {
        var sseText = string.Join("\n\n",
            "data: " + """{ "candidates": [ { "content": { "parts": [ { "text": "abc" } ] } } ] }""",
            "data: " + """{ "candidates": [ { "content": { "parts": [ { "text": "def" } ] }, "finishReason": "STOP" } ] }""",
            "");

        var anthropic = ProxyProtocolBridge.AdaptResponseBodyForClient("Anthropic", "Gemini", sseText, true, "m", 0, 0, 0);
        anthropic.Should().Contain("event: message_start");
        anthropic.Should().Contain("event: message_stop");

        var openAi = ProxyProtocolBridge.AdaptResponseBodyForClient("OpenAI", "Gemini", sseText, true, "m", 0, 0, 0);
        openAi.Should().Contain("data: [DONE]");
    }

    // ============ usage 提取（Gemini usageMetadata 口径）============

    [Fact]
    public void ExtractUsageFromElement_gemini_returns_fresh_input_and_full_output()
    {
        var usage = JsonSerializer.Deserialize<JsonElement>(
            """{ "promptTokenCount": 200, "cachedContentTokenCount": 60, "candidatesTokenCount": 25, "thoughtsTokenCount": 15 }""");

        var (input, cached, output) = ProxyProtocolBridge.ExtractUsageFromElement(usage, "Gemini");
        input.Should().Be(140);
        cached.Should().Be(60);
        output.Should().Be(40);
    }

    // ============ 修复回归：跨块工具索引 / 空内容兜底 / 签名占位过滤 ============

    [Fact]
    public void ConvertGeminiSseChunkToOpenAi_tool_call_index_increments_across_chunks()
    {
        var state = new ProxyProtocolBridge.GeminiToOpenAiStreamState();
        var first = ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(
            """{ "candidates": [ { "content": { "parts": [ { "functionCall": { "id": "fc_a", "name": "a", "args": {} } } ] } } ] }""",
            "m", "chatcmpl-x", state);
        var second = ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(
            """{ "candidates": [ { "content": { "parts": [ { "functionCall": { "id": "fc_b", "name": "b", "args": {} } } ] } } ] }""",
            "m", "chatcmpl-x", state);

        first.Should().Contain("\"index\":0");
        second.Should().Contain("\"index\":1", "跨块的第二个工具调用不能复用 index 0，否则客户端会把它拼进第一个调用");
    }

    [Fact]
    public void PrepareRequestBody_gemini_whitespace_only_messages_falls_back_to_default_content()
    {
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "system": "sys",
          "messages": [ { "role": "user", "content": "   " } ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-2.5-pro", enableStreaming: false, targetBaseUrl: GeminiCliBaseUrl);

        var request = ParseEnvelope(result)["request"]!.AsObject();
        var contents = request["contents"]!.AsArray();
        contents.Should().HaveCount(1);
        contents[0]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        // 兜底消息存在时 systemInstruction 不受影响。
        request["systemInstruction"].Should().NotBeNull();
    }

    [Fact]
    public void ConvertGeminiSseChunkToOpenAi_tracks_usage_across_chunks()
    {
        var state = new ProxyProtocolBridge.GeminiToOpenAiStreamState();
        ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(
            """{ "candidates": [ { "content": { "parts": [ { "text": "a" } ] } } ], "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 2 } }""",
            "m", "chatcmpl-x", state);
        ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(
            """{ "candidates": [ { "content": { "parts": [] }, "finishReason": "STOP" } ], "usageMetadata": { "promptTokenCount": 30, "cachedContentTokenCount": 6, "candidatesTokenCount": 5 } }""",
            "m", "chatcmpl-x", state);

        state.InputTokens.Should().Be(24);
        state.CachedTokens.Should().Be(6);
        state.OutputTokens.Should().Be(5);
        state.FinishReason.Should().Be("STOP");
    }

    [Fact]
    public void ConvertGeminiSseChunkToOpenAi_preserves_usage_when_later_chunk_is_partial()
    {
        var state = new ProxyProtocolBridge.GeminiToOpenAiStreamState();
        ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(
            """{ "candidates": [ { "content": { "parts": [ { "text": "a" } ] } } ], "usageMetadata": { "promptTokenCount": 30, "cachedContentTokenCount": 6, "candidatesTokenCount": 5, "thoughtsTokenCount": 2 } }""",
            "m", "chatcmpl-x", state);
        ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(
            """{ "candidates": [ { "content": { "parts": [], "role": "model" }, "finishReason": "STOP" } ], "usageMetadata": { "totalTokenCount": 43 } }""",
            "m", "chatcmpl-x", state);

        state.InputTokens.Should().Be(24);
        state.CachedTokens.Should().Be(6);
        state.OutputTokens.Should().Be(7);
        state.FinishReason.Should().Be("STOP");
    }

    [Fact]
    public void ConvertGeminiSseChunkToOpenAi_accepts_string_and_null_usage_values()
    {
        var state = new ProxyProtocolBridge.GeminiToOpenAiStreamState();

        ProxyProtocolBridge.ConvertGeminiSseChunkToOpenAi(
            """{ "candidates": [ { "content": { "parts": [ { "text": "ok" } ] } } ], "usageMetadata": { "promptTokenCount": "30", "cachedContentTokenCount": null, "candidatesTokenCount": "5", "thoughtsTokenCount": 2 } }""",
            "m", "chatcmpl-x", state);

        state.InputTokens.Should().Be(30);
        state.CachedTokens.Should().Be(0);
        state.OutputTokens.Should().Be(7);
    }

    [Fact]
    public void CompleteGeminiToOpenAiStream_is_idempotent()
    {
        var state = new ProxyProtocolBridge.GeminiToOpenAiStreamState { FinishReason = "STOP" };
        var first = ProxyProtocolBridge.CompleteGeminiToOpenAiStream("m", "chatcmpl-x", state);
        var second = ProxyProtocolBridge.CompleteGeminiToOpenAiStream("m", "chatcmpl-x", state);
        first.Should().Contain("data: [DONE]");
        second.Should().BeNull("收尾块只允许发送一次");
    }

    // ============ 封套与工具 schema清理 ============

    [Fact]
    public void CleanJsonSchemaForGemini_resolves_ref_and_flattens_all_of()
    {
        // 通过 tools 间接验证：$ref 与 allOf 需要被解析/拍平（Code Assist 不支持原样透传）。
        var anthropicBody = """
        {
          "model": "m",
          "max_tokens": 100,
          "messages": [ { "role": "user", "content": "hi" } ],
          "tools": [ { "name": "t", "input_schema": {
            "$defs": { "Path": { "type": "string" } },
            "allOf": [
              { "type": "object", "properties": { "path": { "$ref": "#/$defs/Path" }, "n": { "type": [ "integer", "null" ] } } }
            ]
          } } ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "Gemini", anthropicBody, "gemini-2.5-pro", enableStreaming: false, targetBaseUrl: GeminiCliBaseUrl);

        var declaration = ParseEnvelope(result)["request"]!["tools"]!.AsArray()[0]!["functionDeclarations"]!.AsArray()[0]!.AsObject();
        var schemaText = declaration["parameters"]!.ToJsonString();
        schemaText.Should().NotContain("$ref", "$ref 需解析后内联");
        schemaText.Should().NotContain("allOf", "allOf 需拍平");
        schemaText.Should().Contain("\"path\"");
        schemaText.Should().Contain("\"n\"");
    }

    [Fact]
    public void CleanJsonSchemaForGemini_strips_required_properties_that_are_unspecified_to_prevent_400()
    {
        // 覆盖上游报 requires unspecified property 'title' 场景：
        // 客户端声明了 required: ["title", "validProp"]，但 properties 字典中只有 "validProp"。
        // 桥接层必须自动将不存在的 "title" 从 required 数组中剥离，避免 Google 报 400 INVALID_ARGUMENT。
        var openAiBody = """
        {
          "model": "1M",
          "messages": [ { "role": "user", "content": "hi" } ],
          "tools": [
            {
              "type": "function",
              "function": {
                "name": "deepseek_tool",
                "parameters": {
                  "type": "object",
                  "properties": {
                    "meta": {
                      "type": "object",
                      "properties": {
                        "phases": {
                          "type": "object",
                          "properties": {
                            "items": {
                              "type": "object",
                              "properties": {
                                "description": { "type": "string" }
                              },
                              "required": [ "title", "description" ]
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", openAiBody, "gemini-2.5-pro", enableStreaming: false, targetBaseUrl: AntigravityBaseUrl);

        var declaration = ParseEnvelope(result)["request"]!["tools"]!.AsArray()[0]!["functionDeclarations"]!.AsArray()[0]!.AsObject();
        var schemaText = declaration["parameters"]!.ToJsonString();
        
        // 校验：不存在的 "title" 已经被从 required 数组中清洗掉，只保留了 "description"
        schemaText.Should().NotContain("\"title\"", "未在 properties 中定义的 required 项必须被剔除以防 Google 报 400");
        schemaText.Should().Contain("\"description\"");
    }

    [Fact]
    public void PrepareRequestBody_openai_to_gemini_handles_malformed_arguments_and_merges_consecutive_roles()
    {
        // 模拟客户端历史记录中包含被截断/非法的参数 JSON，以及连续的 user 消息
        var openAiBody = """
        {
          "model": "1M",
          "messages": [
            { "role": "user", "content": "hello" },
            { "role": "assistant", "content": null, "tool_calls": [
              { "id": "call_1", "type": "function", "function": { "name": "edit", "arguments": "{\"truncated\": \"..." } }
            ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "file updated" },
            { "role": "user", "content": "first user follow up" },
            { "role": "user", "content": "second user follow up" }
          ]
        }
        """;

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", openAiBody, "gemini-3.7-flash-high", enableStreaming: true,
            targetBaseUrl: AntigravityBaseUrl, geminiProjectId: "test-proj");

        var envelope = ParseEnvelope(result);
        var contents = envelope["request"]!["contents"]!.AsArray();

        // 验证：
        // 1. 即使 arguments 损坏，tool_call 也不会被丢弃（兜底为 {}），从而保留了与 tool 响应的配对关系
        // 2. 连续的 user 消息被合并，保证全链路严格交替（user → model → user）
        contents.Should().HaveCount(3);
        contents[0]!["role"]!.GetValue<string>().Should().Be("user");
        contents[0]!["parts"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("hello");

        contents[1]!["role"]!.GetValue<string>().Should().Be("model");
        var call = contents[1]!["parts"]!.AsArray()[0]!["functionCall"]!.AsObject();
        call["name"]!.GetValue<string>().Should().Be("edit");
        call["id"]!.GetValue<string>().Should().Be("call_1");
        call["args"]!.AsObject().Should().NotBeNull();

        contents[2]!["role"]!.GetValue<string>().Should().Be("user");
        var resp = contents[2]!["parts"]!.AsArray()[0]!["functionResponse"]!.AsObject();
        resp["name"]!.GetValue<string>().Should().Be("edit");
        resp["id"]!.GetValue<string>().Should().Be("call_1");
        contents[2]!["parts"]!.AsArray()[1]!["text"]!.GetValue<string>().Should().Be("first user follow up");
        contents[2]!["parts"]!.AsArray()[2]!["text"]!.GetValue<string>().Should().Be("second user follow up");
    }
}

