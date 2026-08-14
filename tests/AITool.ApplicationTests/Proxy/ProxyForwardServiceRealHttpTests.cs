using System.Net;
using System.Text;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 用真实 ProxyForwardService + mock HttpMessageHandler 验证非流式 Responses 转发链路。
/// 复现 chat 页面访问 Responses 站点非流式空回复的问题。
/// </summary>
public sealed class ProxyForwardServiceRealHttpTests
{
    // 真实 CPA 上游非流式 Responses 返回的 JSON（已脱敏，结构保真）
    private const string UpstreamResponsesJson = """
    {"id":"resp_023316174082d7a9016a7cc4e382748199847d20d120dd066d","object":"response","created_at":1786561763,"status":"completed","error":null,"model":"gpt-5.6-luna","output":[{"id":"msg_1","type":"message","status":"completed","content":[{"type":"output_text","annotations":[],"text":"Hi! How can I help?"}],"phase":"final_answer","role":"assistant"}],"usage":{"input_tokens":5,"input_tokens_details":{"cached_tokens":1},"output_tokens":2,"total_tokens":7}}
    """;

    // 真实 Codex 上游流式 SSE（强制 stream=true）：
    // 关键特征——response.completed.response.output 为空数组 []，
    // 内容只通过 response.output_text.delta 事件推送。
    // 非流式聚合必须从 delta 重建 output message，否则 HasUsableResponse 判定失败。
    private const string CodexStreamingSse = """
    event: response.created
    data: {"type":"response.created","response":{"id":"resp_1","object":"response","status":"in_progress","output":[]}}

    event: response.output_text.delta
    data: {"type":"response.output_text.delta","delta":"Hi","output_index":0,"content_index":0}

    event: response.output_text.delta
    data: {"type":"response.output_text.delta","delta":"!","output_index":0,"content_index":0}

    event: response.completed
    data: {"type":"response.completed","response":{"id":"resp_1","object":"response","status":"completed","output":[],"usage":{"input_tokens":8,"input_tokens_details":{"cached_tokens":0},"output_tokens":6,"total_tokens":14}}}

    """;

    private static ProxyForwardService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance);
    }

    private static ProxyForwardRequest ResponsesRequest(string baseUrl = "https://cpa.example/v1/responses") => new()
    {
        TargetBaseUrl = baseUrl,
        TargetEndpointPathMode = "standard-root",
        TargetApiKey = "sk-test",
        ProtocolType = "Responses",
        TargetModelName = "gpt-5.6-luna",
        RequestBody = "{\"model\":\"gpt-5.6-luna\",\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"say hi\"}]}],\"stream\":false,\"store\":false}",
        PreparedRequestBody = "{\"model\":\"gpt-5.6-luna\",\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"say hi\"}]}],\"stream\":false,\"store\":false}",
        EnableStreaming = false,
        RequestTimeoutSeconds = 30,
        RetryCount = 0
    };

    [Fact]
    public async Task ForwardAsync_non_streaming_responses_json_is_returned_as_success()
    {
        // CPA 上游非流式直接返回 application/json（非 SSE）
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(UpstreamResponsesJson, Encoding.UTF8, "application/json")
        });

        var result = await service.ForwardAsync(ResponsesRequest());

        result.Success.Should().BeTrue("非流式 Responses 上游返回了含 output 的标准 JSON，应判定成功");
        result.ResponseBody.Should().Contain("Hi! How can I help?", "响应体应保留上游原文");
        // 上游 input_tokens=5 已含缓存（cached=1），日志输入统一为不含缓存的新输入。
        result.InputTokens.Should().Be(4, "应从 usage.input_tokens 提取并减去缓存");
        result.OutputTokens.Should().Be(2, "应从 usage.output_tokens 提取");
        result.CachedTokens.Should().Be(1, "应从 input_tokens_details.cached_tokens 提取");
    }

    [Fact]
    public async Task ForwardAsync_non_streaming_codex_sse_with_empty_output_rebuilds_from_delta()
    {
        // Codex 上游强制 stream=true，客户端非流式时上游返回 SSE。
        // response.completed.output 为空，内容只在 output_text.delta 里。
        // 聚合必须从 delta 重建 output message，否则上层报 "no usable choices"。
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CodexStreamingSse, Encoding.UTF8, "text/event-stream")
        });

        var result = await service.ForwardAsync(ResponsesRequest());

        result.Success.Should().BeTrue("Codex SSE 含 output_text.delta 内容，聚合后应判定成功");
        result.ResponseBody.Should().Contain("Hi!", "聚合后的 output 应包含 delta 累积的文本");
        result.InputTokens.Should().Be(8, "应从 usage.input_tokens 提取");
        result.OutputTokens.Should().Be(6, "应从 usage.output_tokens 提取");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
