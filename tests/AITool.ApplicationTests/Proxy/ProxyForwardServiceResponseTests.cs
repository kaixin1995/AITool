using System.Reflection;
using System.Text.Json;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 验证 ProxyForwardService 对各种响应格式的可用性判断和错误信息构造。
/// </summary>
public sealed class ProxyForwardServiceResponseTests
{
    [Fact]
    public void Constructor_Disables_Default_HttpClient_Timeout()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(100)
        };

        _ = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        httpClient.Timeout.Should().Be(global::System.Threading.Timeout.InfiniteTimeSpan);
    }

    // ========== Usage ==========

    [Fact]
    public void ExtractUsageMetrics_OpenAiChatCompletions_ReturnsPromptCachedAndCompletionTokens()
    {
        var body = """{"usage":{"prompt_tokens":120,"prompt_tokens_details":{"cached_tokens":45},"completion_tokens":30}}""";

        var usage = ExtractUsageMetricsCore(body, "OpenAI");

        usage.InputTokens.Should().Be(120);
        usage.CachedTokens.Should().Be(45);
        usage.OutputTokens.Should().Be(30);
    }

    [Fact]
    public void ExtractUsageMetrics_OpenAiResponses_ReturnsInputCachedAndOutputTokens()
    {
        var body = """{"id":"resp_1","object":"response","usage":{"input_tokens":240,"input_tokens_details":{"cached_tokens":80},"output_tokens":60}}""";

        var usage = ExtractUsageMetricsCore(body, "OpenAI");

        usage.InputTokens.Should().Be(240);
        usage.CachedTokens.Should().Be(80);
        usage.OutputTokens.Should().Be(60);
    }

    [Fact]
    public void ExtractUsageMetrics_Anthropic_InputTokensExcludeCache()
    {
        // Anthropic 的 input_tokens 已包含缓存 token，输入列必须是减去缓存后的"新输入"，
        // 否则缓存会在输入列和缓存列重复统计。
        var body = """{"usage":{"input_tokens":405415,"cache_read_input_tokens":405248,"cache_creation_input_tokens":0,"output_tokens":349}}""";

        var usage = ExtractUsageMetricsCore(body, "Anthropic");

        usage.InputTokens.Should().Be(167, "input_tokens 应减去缓存，得到真实新输入");
        usage.CachedTokens.Should().Be(405248);
        usage.OutputTokens.Should().Be(349);
    }

    [Fact]
    public void ExtractUsageMetrics_Anthropic_WithCacheCreationAlsoExcluded()
    {
        // cache_read + cache_creation 都是 input_tokens 的子集，都要从输入中减掉。
        var body = """{"usage":{"input_tokens":1000,"cache_read_input_tokens":600,"cache_creation_input_tokens":100,"output_tokens":50}}""";

        var usage = ExtractUsageMetricsCore(body, "Anthropic");

        usage.InputTokens.Should().Be(300, "应减去 cache_read + cache_creation");
        usage.CachedTokens.Should().Be(700);
        usage.OutputTokens.Should().Be(50);
    }

    [Fact]
    public void ExtractUsageMetrics_Anthropic_NoNegativeInput()
    {
        // 异常数据（缓存大于 input_tokens）不应产生负数输入。
        var body = """{"usage":{"input_tokens":100,"cache_read_input_tokens":150,"output_tokens":10}}""";

        var usage = ExtractUsageMetricsCore(body, "Anthropic");

        usage.InputTokens.Should().Be(0);
        usage.CachedTokens.Should().Be(150);
        usage.OutputTokens.Should().Be(10);
    }

    // ========== HasUsableResponse ==========

    [Fact]
    public void HasUsableResponse_ChatCompletions_WithChoices_ReturnsTrue()
    {
        var body = """{"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hi"}}]}""";
        ProxyForwardService.HasUsableResponse(body, "OpenAI").Should().BeTrue();
    }

    [Fact]
    public void HasUsableResponse_ChatCompletions_EmptyChoices_ReturnsFalse()
    {
        var body = """{"id":"chatcmpl-1","object":"chat.completion","choices":[]}""";
        ProxyForwardService.HasUsableResponse(body, "OpenAI").Should().BeFalse();
    }

    [Fact]
    public void HasUsableResponse_ChatCompletions_NoChoices_ReturnsFalse()
    {
        var body = """{"id":"chatcmpl-1","object":"chat.completion"}""";
        ProxyForwardService.HasUsableResponse(body, "OpenAI").Should().BeFalse();
    }

    [Fact]
    public void HasUsableResponse_Responses_WithOutput_ReturnsTrue()
    {
        var body = """{"id":"resp_1","object":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}]}""";
        ProxyForwardService.HasUsableResponse(body, "OpenAI").Should().BeTrue();
    }

    [Fact]
    public void HasUsableResponse_Responses_EmptyOutput_ReturnsFalse()
    {
        var body = """{"id":"resp_1","object":"response","status":"completed","output":[]}""";
        ProxyForwardService.HasUsableResponse(body, "OpenAI").Should().BeFalse();
    }

    [Fact]
    public void HasUsableResponse_Responses_ErrorNull_ReturnsTrue()
    {
        // Responses 格式中 error 为 null 是正常情况
        var body = """{"id":"resp_1","object":"response","status":"completed","error":null,"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}]}""";
        ProxyForwardService.HasUsableResponse(body, "OpenAI").Should().BeTrue();
    }

    [Fact]
    public void HasUsableResponse_Responses_ActualError_ReturnsFalse()
    {
        var body = """{"error":{"message":"model not found","type":"invalid_request_error"}}""";
        ProxyForwardService.HasUsableResponse(body, "OpenAI").Should().BeFalse();
    }

    [Fact]
    public void HasUsableResponse_Anthropic_WithContent_ReturnsTrue()
    {
        var body = """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"hi"}]}""";
        ProxyForwardService.HasUsableResponse(body, "Anthropic").Should().BeTrue();
    }

    [Fact]
    public void HasUsableResponse_Anthropic_EmptyContent_ReturnsFalse()
    {
        var body = """{"id":"msg_1","type":"message","role":"assistant","content":[]}""";
        ProxyForwardService.HasUsableResponse(body, "Anthropic").Should().BeFalse();
    }

    [Fact]
    public void HasUsableResponse_Anthropic_ActualError_ReturnsFalse()
    {
        var body = """{"type":"error","error":{"type":"not_found_error","message":"model not found"}}""";
        ProxyForwardService.HasUsableResponse(body, "Anthropic").Should().BeFalse();
    }

    [Fact]
    public void HasUsableResponse_EmptyBody_ReturnsFalse()
    {
        ProxyForwardService.HasUsableResponse("", "OpenAI").Should().BeFalse();
        ProxyForwardService.HasUsableResponse("  ", "OpenAI").Should().BeFalse();
        ProxyForwardService.HasUsableResponse(null!, "OpenAI").Should().BeFalse();
    }

    [Fact]
    public void HasUsableResponse_InvalidJson_ReturnsFalse()
    {
        ProxyForwardService.HasUsableResponse("not json", "OpenAI").Should().BeFalse();
    }

    // ========== BuildFailureMessage ==========

    [Fact]
    public void BuildFailureMessage_EmptyBody_ReturnsEmptyMessage()
    {
        var msg = ProxyForwardService.BuildFailureMessage("", "OpenAI");
        msg.Should().Contain("empty");
    }

    [Fact]
    public void BuildFailureMessage_ErrorNull_ReturnsNoChoicesMessage()
    {
        // error 为 null 不应被误当成错误信息
        var body = """{"id":"resp_1","error":null,"output":[]}""";
        var msg = ProxyForwardService.BuildFailureMessage(body, "OpenAI");
        msg.Should().Contain("no usable choices");
    }

    [Fact]
    public void BuildFailureMessage_ActualErrorObject_ReturnsErrorContent()
    {
        var body = """{"error":{"message":"model not found","type":"invalid_request_error"}}""";
        var msg = ProxyForwardService.BuildFailureMessage(body, "OpenAI");
        msg.Should().Contain("model not found");
    }

    [Fact]
    public void BuildFailureMessage_ErrorString_ReturnsErrorString()
    {
        var body = """{"error":"rate limited"}""";
        var msg = ProxyForwardService.BuildFailureMessage(body, "OpenAI");
        msg.Should().Be("rate limited");
    }

    [Fact]
    public void BuildFailureMessage_AnthropicNoError_ReturnsNoContentBlocksMessage()
    {
        var body = """{"id":"msg_1","type":"message","role":"assistant","content":[]}""";
        var msg = ProxyForwardService.BuildFailureMessage(body, "Anthropic");
        msg.Should().Contain("no usable content blocks");
    }

    [Fact]
    public void BuildFailureMessage_InvalidJson_ReturnsUnreadableMessage()
    {
        var msg = ProxyForwardService.BuildFailureMessage("not json", "OpenAI");
        msg.Should().Contain("unreadable");
    }

    private static (int InputTokens, int CachedTokens, int OutputTokens) ExtractUsageMetricsCore(string responseBody, string protocolType)
    {
        var method = typeof(ProxyForwardService).GetMethod("ExtractUsageMetrics", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        return ((int InputTokens, int CachedTokens, int OutputTokens))method!
            .Invoke(null, new object?[] { responseBody, protocolType })!;
    }

    // ========== TryExtractResponsesCompletion（Codex 非流式 SSE 聚合） ==========

    // Codex /responses 强制 stream=true，客户端非流式时上游返回 SSE 流，
    // 由 TryExtractResponsesCompletion 聚合成完整 Responses JSON。
    // 历史上该方法依赖空行分块，但 Codex 事件之间不一定有空行，导致聚合失败、
    // 上层把 SSE 原文当 JSON 解析报 "no usable choices"。以下覆盖各 SSE 形态。

    // 聚合后期望得到的完整 response 对象 JSON（不含外层事件包装）
    private const string CompletedResponseJson =
        """{"id":"resp_1","object":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}],"usage":{"input_tokens":5,"input_tokens_details":{"cached_tokens":1},"output_tokens":2,"total_tokens":7}}""";

    // response.completed 事件的 data 行（外层带 type 与 response 字段）
    private const string CompletedDataLine =
        """{"type":"response.completed","response":""" + CompletedResponseJson + "}";

    private static string? TryExtractResponsesCompletionCore(string sseBody)
    {
        var method = typeof(ProxyForwardService).GetMethod(
            "TryExtractResponsesCompletion", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        return (string?)method!.Invoke(null, new object?[] { sseBody });
    }

    [Fact]
    public void TryExtractResponsesCompletion_StandardSseWithEventLines_ReturnsResponse()
    {
        // 标准 SSE：每事件带 event: 行，事件间空行分隔
        var sse = string.Concat(
            "event: response.created\n",
            """data: {"type":"response.created","response":{"id":"resp_1"}}""", "\n\n",
            "event: response.completed\n",
            "data: ", CompletedDataLine, "\n\n");

        TryExtractResponsesCompletionCore(sse).Should().Be(CompletedResponseJson);
    }

    [Fact]
    public void TryExtractResponsesCompletion_CodexStyleNoEventLinesBlankSeparated_ReturnsResponse()
    {
        // Codex 风格：无 event: 行，事件间空行分隔
        var sse = string.Concat(
            """data: {"type":"response.created","response":{"id":"resp_1"}}""", "\n\n",
            "data: ", CompletedDataLine, "\n\n");

        TryExtractResponsesCompletionCore(sse).Should().Be(CompletedResponseJson);
    }

    [Fact]
    public void TryExtractResponsesCompletion_NoBlankLineBetweenEvents_ReturnsResponse()
    {
        // Codex 事件之间无空行分隔（历史 bug 场景）：每 data 行是完整 JSON，
        // 必须逐行独立解析命中 response.completed，不能依赖空行分块。
        var sse = string.Concat(
            """data: {"type":"response.created","response":{"id":"resp_1"}}""", "\n",
            "data: ", CompletedDataLine, "\n");

        TryExtractResponsesCompletionCore(sse).Should().Be(CompletedResponseJson);
    }

    [Fact]
    public void TryExtractResponsesCompletion_OnlyCompletedEvent_ReturnsResponse()
    {
        var sse = string.Concat("data: ", CompletedDataLine, "\n\n");

        TryExtractResponsesCompletionCore(sse).Should().Be(CompletedResponseJson);
    }

    [Fact]
    public void TryExtractResponsesCompletion_NoCompletedEvent_ReturnsNull()
    {
        var sse = """data: {"type":"response.created","response":{"id":"resp_1"}}""";

        TryExtractResponsesCompletionCore(sse).Should().BeNull();
    }

    // 真实 Codex 上游的 response.completed.output 始终为空 []，
    // 内容只通过 response.output_text.delta 推送。聚合必须从 delta 重建 output message。
    // 历史 bug：delta 在 TryParsePayload 的副作用里累积，独立解析和 join 重试会重复累积同一 delta
    // （如 delta="95" 变成 "9595"）。以下用真实 Codex SSE 结构验证不重复。
    private const string CodexEmptyOutputCompletedJson =
        """{"id":"resp_1","object":"response","status":"completed","output":[],"usage":{"input_tokens":8,"output_tokens":6,"total_tokens":14}}""";

    private const string CodexEmptyOutputCompletedDataLine =
        """{"type":"response.completed","response":""" + CodexEmptyOutputCompletedJson + "}";

    [Fact]
    public void TryExtractResponsesCompletion_CodexEmptyOutputRebuildsFromDeltaWithoutDuplication()
    {
        // 真实 Codex SSE 结构：output_text.delta + output_text.done + output_item.done + completed(output=[])
        // delta="95"，聚合后 output message 的 text 必须是 "95"，不能是 "9595"（重复累积）。
        var sse = string.Concat(
            "event: response.output_text.delta\n",
            """data: {"type":"response.output_text.delta","delta":"95","output_index":1,"content_index":0}""", "\n\n",
            "event: response.output_text.done\n",
            """data: {"type":"response.output_text.done","text":"95","output_index":1,"content_index":0}""", "\n\n",
            "event: response.completed\n",
            "data: ", CodexEmptyOutputCompletedDataLine, "\n\n");

        var result = TryExtractResponsesCompletionCore(sse);
        result.Should().NotBeNullOrEmpty();

        using var document = JsonDocument.Parse(result!);
        var output = document.RootElement.GetProperty("output");
        output.GetArrayLength().Should().Be(1, "应从 delta 重建一条 message");
        var message = output[0];
        message.GetProperty("type").GetString().Should().Be("message");
        // 核心断言：delta 只累积一次，不能重复成 "9595"
        message.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("95");
    }
}
