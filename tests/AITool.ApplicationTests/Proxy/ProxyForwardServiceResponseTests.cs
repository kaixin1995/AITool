using System.Reflection;
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
}
