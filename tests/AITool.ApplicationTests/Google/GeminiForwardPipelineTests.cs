using System.Net;
using System.Text;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Google;

/// <summary>
/// Gemini 上游（v1internal）端到端转发链路验证：真实 ProxyForwardService + mock HttpMessageHandler。
/// 覆盖基础设施层接线——目标路径（generateContent / streamGenerateContent?alt=sse）、Bearer 鉴权、
/// 封套请求体、usageMetadata 三段口径提取、以及无 [DONE] 标记的流式完成判定（finishReason）。
/// </summary>
public sealed class GeminiForwardPipelineTests
{
    private const string GeminiCliBaseUrl = "https://cloudcode-pa.googleapis.com";

    private sealed class CapturedRequest
    {
        public Uri? Url;
        public string? Body;
        public byte[]? RawBody;
    }

    private static ProxyForwardService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance);
    }

    private static ProxyForwardRequest GeminiRequest(bool streaming, string preparedBody) => new()
    {
        TargetBaseUrl = GeminiCliBaseUrl,
        TargetEndpointPathMode = "standard-root",
        TargetApiKey = "ya29-test-token",
        ProtocolType = "Gemini",
        TargetModelName = "gemini-2.5-pro",
        RequestBody = preparedBody,
        PreparedRequestBody = preparedBody,
        EnableStreaming = streaming,
        RequestTimeoutSeconds = 30,
        RetryCount = 0,
        ForwardHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "GeminiCLI/0.35.2/gemini-2.5-pro (win32; x64; cloud-shell)"
        },
        TargetPath = streaming ? "/v1internal:streamGenerateContent?alt=sse" : "/v1internal:generateContent"
    };

    [Fact]
    public async Task ForwardAsync_gemini_non_stream_posts_envelope_to_generate_content()
    {
        var captured = new CapturedRequest();
        var service = CreateService(request =>
        {
            captured.Url = request.RequestUri;
            captured.Body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            captured.RawBody = null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"response":{"candidates":[{"content":{"role":"model","parts":[{"text":"你好"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":100,"cachedContentTokenCount":25,"candidatesTokenCount":8,"thoughtsTokenCount":4}}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var preparedBody = "{\"model\":\"gemini-2.5-pro\",\"project\":\"proj-1\",\"request\":{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"hi\"}]}]}}";
        var result = await service.ForwardAsync(GeminiRequest(streaming: false, preparedBody));

        result.Success.Should().BeTrue();
        captured.Url!.AbsolutePath.Should().Be("/v1internal:generateContent", "非流式走 generateContent 端点");

        // usage 三段口径：input = prompt - cached；output = candidates + thoughts。
        result.InputTokens.Should().Be(75);
        result.CachedTokens.Should().Be(25);
        result.OutputTokens.Should().Be(12);

        result.ResponseBody.Should().Contain("你好");
    }

    [Fact]
    public async Task ForwardStreamingAsync_gemini_stream_marks_completion_on_finish_reason()
    {
        var firstChunk = "{\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Hel\"}]}}],\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":1}}}";
        var secondChunk = "{\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"lo\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":20,\"cachedContentTokenCount\":4,\"candidatesTokenCount\":3}}}";
        var sse = string.Join("\n", $"data: {firstChunk}", "", $"data: {secondChunk}", "", "");

        var chunks = new List<string>();
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });

        var preparedBody = "{\"model\":\"gemini-2.5-pro\",\"project\":\"proj-1\",\"request\":{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"hi\"}]}]}}";
        var result = await service.ForwardStreamingAsync(
            GeminiRequest(streaming: true, preparedBody),
            async (line, _) =>
            {
                chunks.Add(line);
                await Task.CompletedTask;
            });

        // Gemini 流没有 [DONE]/message_stop：finishReason 出现即视为正常完成，不能误判为中断。
        result.Success.Should().BeTrue();
        result.IsStreamInterrupted.Should().BeFalse("finishReason 已出现，应视为正常完成");
        result.ErrorMessage.Should().BeNull();
        result.InputTokens.Should().Be(16);
        result.CachedTokens.Should().Be(4);
        result.OutputTokens.Should().Be(3);
        chunks.Should().Contain(line => line.StartsWith("data:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForwardStreamingAsync_gemini_stream_without_finish_reason_is_interrupted()
    {
        var chunk = "{\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"partial\"}]}}]}}";
        var sse = $"data: {chunk}\n\n";

        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });

        var preparedBody = "{\"model\":\"gemini-2.5-pro\",\"project\":\"proj-1\",\"request\":{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"hi\"}]}]}}";
        var result = await service.ForwardStreamingAsync(
            GeminiRequest(streaming: true, preparedBody),
            (line, _) => Task.CompletedTask);

        // 有内容但未见 finishReason：按中断处理（与控制器层桥接的判定口径一致）。
        result.Success.Should().BeTrue();
        result.HasStartedStreaming.Should().BeTrue();
        result.IsStreamInterrupted.Should().BeTrue("流有内容但未出现 finishReason，应视为中断");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
