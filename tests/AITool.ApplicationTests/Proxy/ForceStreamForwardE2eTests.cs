using System.Net;
using System.Text;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 强制上游流式（X-AITool-Force-Stream 站点标记，WorkBuddy code 11101 修复）的真实转发链路端到端验证：
/// ① 标记头存在时非流式请求体被改写为 stream:true，且标记头不透传上游；
/// ② 上游 SSE（chat.completion.chunk）被聚合回完整 chat.completion，客户端无感；
/// ③ 无标记头的普通站点请求体不被改写（负例，保护既有行为）。
/// </summary>
public sealed class ForceStreamForwardE2eTests
{
    private const string ChatSse = """
        data: {"id":"wb-1","object":"chat.completion.chunk","model":"deepseek-v4.1","choices":[{"index":0,"delta":{"role":"assistant","content":"你"},"finish_reason":null}]}

        data: {"id":"wb-1","object":"chat.completion.chunk","model":"deepseek-v4.1","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":null}]}

        data: {"id":"wb-1","object":"chat.completion.chunk","model":"deepseek-v4.1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}

        data: [DONE]

        """;

    private static ProxyForwardRequest ChatRequest(bool streamInBody) => new()
    {
        TargetBaseUrl = "https://copilot.tencent.com/v2",
        AllowNonSiteTarget = true,
        TargetEndpointPathMode = "versioned-base",
        TargetApiKey = "at-test",
        ProtocolType = "OpenAI",
        TargetModelName = "deepseek-v4.1",
        RequestBody = "{\"model\":\"deepseek-v4.1\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":" + (streamInBody ? "true" : "false") + "}",
        EnableStreaming = false,
        RequestTimeoutSeconds = 30,
        RetryCount = 0,
        ForwardHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "WorkBuddy/5.5.4",
            ["X-Agent-Type"] = "main",
            [ProxyForwardService.ForceStreamHeader] = "true",
        }
    };

    [Fact]
    public async Task Marker_header_forces_stream_true_strips_marker_and_aggregates_sse()
    {
        HttpRequestMessage? upstreamSeen = null;
        string? upstreamBody = null;
        var service = CreateService(req =>
        {
            upstreamSeen = req;
            upstreamBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatSse, Encoding.UTF8, "text/event-stream")
            };
        });

        var result = await service.ForwardAsync(ChatRequest(streamInBody: false));

        // 上游视角：请求体被改写为 stream:true（WorkBuddy 只接受流式），
        // 并要求流尾附带 usage（OpenAI 规范下流式默认不带计量）。
        upstreamBody.Should().Contain("\"stream\":true", "标记头应把非流式请求体强制改为流式");
        upstreamBody.Should().NotContain("\"stream\":false");
        upstreamBody.Should().Contain("\"include_usage\":true", "强制流式时必须索要 usage，否则聚合侧计量落空");
        // 标记头是内部信号，绝不能透传给上游。
        upstreamSeen!.Headers.Contains(ProxyForwardService.ForceStreamHeader).Should()
            .BeFalse("内部标记头必须在 BuildRequestMessage 剥离");
        // 客户端视角：拿到聚合后的完整 chat.completion，非流式无感。
        result.Success.Should().BeTrue("SSE 聚合后应判定成功");
        result.IsStreaming.Should().BeFalse();
        var json = System.Text.Json.JsonDocument.Parse(result.ResponseBody!);
        json.RootElement.GetProperty("object").GetString().Should().Be("chat.completion");
        json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("你好");
        json.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64().Should().Be(3);
        result.InputTokens.Should().Be(3);
        result.OutputTokens.Should().Be(2);
    }

    [Fact]
    public async Task Malformed_sse_chunk_returns_bad_gateway_instead_of_partial_answer()
    {
        const string sse = """
            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"before"}}]}

            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"lost

            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"after"},"finish_reason":"stop"}]}

            data: [DONE]
            """;
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });

        var result = await service.ForwardAsync(ChatRequest(streamInBody: false));

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(502);
        result.ResponseBody.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truncated_sse_returns_bad_gateway_instead_of_partial_answer(bool anotherChoiceFinished)
    {
        const string partial = """
            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":null}]}

            """;
        const string finishedChoice = """

            data: {"object":"chat.completion.chunk","choices":[{"index":1,"delta":{"content":"complete"},"finish_reason":"stop"}]}

            """;
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(partial + (anotherChoiceFinished ? finishedChoice : ""), Encoding.UTF8, "text/event-stream")
        });

        var result = await service.ForwardAsync(ChatRequest(streamInBody: false));

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(502);
        result.ResponseBody.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_sse_with_done_or_all_choices_finished_succeeds(bool withDone)
    {
        const string chunks = """
            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"complete"},"finish_reason":"length"},{"index":1,"delta":{"content":"answer"},"finish_reason":"stop"}]}

            """;
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(chunks + (withDone ? "\ndata: [DONE]\n\n" : ""), Encoding.UTF8, "text/event-stream")
        });

        var result = await service.ForwardAsync(ChatRequest(streamInBody: false));

        result.Success.Should().BeTrue();
        using var json = System.Text.Json.JsonDocument.Parse(result.ResponseBody!);
        var choices = json.RootElement.GetProperty("choices");
        choices[0].GetProperty("finish_reason").GetString().Should().Be("length");
        choices[1].GetProperty("finish_reason").GetString().Should().Be("stop");
    }

    [Fact]
    public async Task Empty_choices_only_sse_returns_bad_gateway()
    {
        const string sse = """
            data: {"object":"chat.completion.chunk","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":0}}

            data: [DONE]
            """;
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });

        var result = await service.ForwardAsync(ChatRequest(streamInBody: false));

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(502);
        result.ResponseBody.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Aggregation_preserves_fragmented_refusal_and_legacy_function_call_per_choice()
    {
        const string sse = """
            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","refusal":"I cannot "}},{"index":1,"delta":{"function_call":{"name":"get_","arguments":"{\"city\":"}}}]}

            data: {"object":"chat.completion.chunk","choices":[{"index":1,"delta":{"function_call":{"name":"weather","arguments":"\"Paris\"}"}}},{"index":0,"delta":{"refusal":"help with that."}}]}

            data: {"object":"chat.completion.chunk","choices":[{"index":0,"delta":{"refusal":null},"finish_reason":"stop"},{"index":1,"delta":{},"finish_reason":"function_call"}]}

            data: [DONE]
            """;
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });

        var result = await service.ForwardAsync(ChatRequest(streamInBody: false));

        result.Success.Should().BeTrue();
        using var json = System.Text.Json.JsonDocument.Parse(result.ResponseBody!);
        var choices = json.RootElement.GetProperty("choices");
        var refusal = choices[0].GetProperty("message");
        refusal.GetProperty("content").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        refusal.GetProperty("refusal").GetString().Should().Be("I cannot help with that.");
        refusal.TryGetProperty("function_call", out _).Should().BeFalse();
        var functionMessage = choices[1].GetProperty("message");
        functionMessage.TryGetProperty("refusal", out _).Should().BeFalse();
        var function = functionMessage.GetProperty("function_call");
        function.GetProperty("name").GetString().Should().Be("get_weather");
        function.GetProperty("arguments").GetString().Should().Be("{\"city\":\"Paris\"}");
        choices[1].GetProperty("finish_reason").GetString().Should().Be("function_call");
    }

    [Fact]
    public async Task Body_already_stream_true_is_not_double_modified()
    {
        string? upstreamBody = null;
        var service = CreateService(req =>
        {
            upstreamBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatSse, Encoding.UTF8, "text/event-stream")
            };
        });

        // 客户端本就 stream:true（isStreaming 判定为真）：不进入改写与聚合分支，SSE 原样透传。
        var request = ChatRequest(streamInBody: true);
        var result = await service.ForwardAsync(request);

        upstreamBody.Should().Contain("\"stream\":true");
        result.Success.Should().BeTrue();
        result.ResponseBody.Should().StartWith("data:", "已流式请求不触发非流式聚合，SSE 原样返回");
        result.ResponseBody.Should().Contain("chat.completion.chunk");
    }

    [Fact]
    public async Task Without_marker_header_non_stream_body_is_untouched()
    {
        string? upstreamBody = null;
        var service = CreateService(req =>
        {
            upstreamBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"x\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
            };
        });

        // 普通站点：没有标记头，请求体必须原样保留 stream:false（保护既有行为）。
        var request = ChatRequest(streamInBody: false);
        request.ForwardHeaders.Remove(ProxyForwardService.ForceStreamHeader);
        var result = await service.ForwardAsync(request);

        upstreamBody.Should().Contain("\"stream\":false", "无标记头时绝不改写请求体");
        result.Success.Should().BeTrue();
        result.ResponseBody.Should().Contain("ok");
    }

    [Fact]
    public async Task WorkBuddy_preset_drives_force_stream_for_non_stream_client()
    {
        HttpRequestMessage? upstreamSeen = null;
        string? upstreamBody = null;
        var service = CreateService(req =>
        {
            upstreamSeen = req;
            upstreamBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatSse, Encoding.UTF8, "text/event-stream")
            };
        });

        var request = ChatRequest(streamInBody: false);
        // 通过预设生成请求头，而不是手工塞入标记
        request.ForwardHeaders = ClientEmulationEngine.ResolveHeaders(ClientEmulationConstants.WorkBuddy, null, "deepseek-v4.1");

        // 预设应自带内部标记
        request.ForwardHeaders.Should().ContainKey(ProxyForwardService.ForceStreamHeader);
        request.ForwardHeaders[ProxyForwardService.ForceStreamHeader].Should().Be("true");

        var result = await service.ForwardAsync(request);

        // 转发层验证：上游 body 改写为 stream:true+include_usage，标记头剥离，SSE 聚合回完整 chat.completion
        upstreamBody.Should().Contain("\"stream\":true");
        upstreamBody.Should().NotContain("\"stream\":false");
        upstreamBody.Should().Contain("\"include_usage\":true");
        upstreamSeen!.Headers.Contains(ProxyForwardService.ForceStreamHeader).Should().BeFalse();

        result.Success.Should().BeTrue();
        result.IsStreaming.Should().BeFalse();
        var json = System.Text.Json.JsonDocument.Parse(result.ResponseBody!);
        json.RootElement.GetProperty("object").GetString().Should().Be("chat.completion");
        json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("你好");
        json.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64().Should().Be(3);
        result.InputTokens.Should().Be(3);
        result.OutputTokens.Should().Be(2);
    }

    [Fact]
    public async Task WorkBuddy_preset_force_stream_can_be_opted_out()
    {
        HttpRequestMessage? upstreamSeen = null;
        string? upstreamBody = null;
        var service = CreateService(req =>
        {
            upstreamSeen = req;
            upstreamBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"x\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
            };
        });

        var request = ChatRequest(streamInBody: false);
        var extraHeaders = new Dictionary<string, string>
        {
            [ProxyForwardService.ForceStreamHeader] = "false"
        };
        request.ForwardHeaders = ClientEmulationEngine.ResolveHeaders(ClientEmulationConstants.WorkBuddy, extraHeaders, "deepseek-v4.1");

        var result = await service.ForwardAsync(request);

        upstreamBody.Should().Contain("\"stream\":false", "extraHeaders 设 false 时上游 body 原样保留 stream:false");
        upstreamSeen!.Headers.Contains(ProxyForwardService.ForceStreamHeader).Should().BeFalse();
        result.Success.Should().BeTrue();
        result.ResponseBody.Should().Contain("ok");
    }

    private static ProxyForwardService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
