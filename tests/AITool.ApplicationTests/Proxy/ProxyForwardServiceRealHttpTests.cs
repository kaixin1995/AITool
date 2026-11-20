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
    [Fact]
    public async Task Anthropic_partial_usage_keeps_read_write_separate_and_accepts_zero()
    {
        const string sse = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":600,\"cache_creation_input_tokens\":100,\"output_tokens\":9}}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"cache_creation_input_tokens\":50,\"output_tokens\":0}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var request = ResponsesRequest(); request.ProtocolType = "Anthropic"; request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        result.Success.Should().BeTrue(); result.InputTokens.Should().Be(100); result.CachedTokens.Should().Be(650); result.OutputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Responses_partial_usage_merges_cache_and_true_zero()
    {
        const string sse = "data: {\"type\":\"response.in_progress\",\"response\":{\"usage\":{\"input_tokens\":100,\"input_tokens_details\":{\"cached_tokens\":40,\"cache_write_tokens\":20},\"output_tokens\":9}}}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"output_tokens\":0}}}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var request = ResponsesRequest(); request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        result.Success.Should().BeTrue(); result.InputTokens.Should().Be(40); result.CachedTokens.Should().Be(60); result.OutputTokens.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upstream_io_failure_before_headers_is_not_a_client_cancellation(bool streaming)
    {
        var service = CreateService(_ => throw new IOException("synthetic upstream I/O failure"));
        var request = ResponsesRequest();
        request.EnableStreaming = streaming;
        var result = streaming
            ? await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask)
            : await service.ForwardAsync(request);
        result.Success.Should().BeFalse();
        result.IsCanceled.Should().BeFalse();
        result.ErrorMessage.Should().Contain("synthetic upstream I/O failure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_remains_canceled(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(_ => throw new OperationCanceledException(cancellation.Token));
        var request = ResponsesRequest();
        request.EnableStreaming = streaming;
        var result = streaming
            ? await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask, cancellation.Token)
            : await service.ForwardAsync(request, cancellation.Token);
        result.Success.Should().BeFalse();
        result.IsCanceled.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Loopback_http_stream_distinguishes_terminal_from_disconnect(bool complete)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? line;
            do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
            const string body = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}\n\n";
            var payload = body + (complete ? "data: {\"type\":\"response.completed\"}\n\n" : "");
            var bytes = Encoding.UTF8.GetBytes(payload);
            var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\nContent-Length: " + (bytes.Length + (complete ? 0 : 12)) + "\r\n\r\n");
            await stream.WriteAsync(headers); await stream.WriteAsync(bytes); await stream.FlushAsync();
        });
        using var client = new HttpClient();
        var service = new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance);
        var request = ResponsesRequest($"http://127.0.0.1:{port}"); request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        await server;
        result.Success.Should().Be(complete);
        result.IsCanceled.Should().BeFalse();
        result.IsStreamInterrupted.Should().Be(!complete);
        result.ResponseBody.Should().Contain("response.output_text.delta");
    }

    [Fact]
    public async Task Multiline_terminal_usage_is_recorded_before_return()
    {
        const string sse = "event: response.completed\ndata: {\n" +
            "data: \"response\": {\"usage\": {\"input_tokens\": 100,\"input_tokens_details\": {\"cached_tokens\": 40},\"output_tokens\": 7}}\n" +
            "data: }\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var request = ResponsesRequest(); request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        result.Success.Should().BeTrue();
        result.InputTokens.Should().Be(60); result.CachedTokens.Should().Be(40); result.OutputTokens.Should().Be(7);
    }

    [Fact]
    public async Task Multiline_named_terminal_event_stops_without_waiting_for_eof()
    {
        const string sse = "event: response.completed\ndata: {\n" + "data: \"response\":{\"status\":\"completed\"}\n" + "data: }\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new HangingSseStream(Encoding.UTF8.GetBytes(sse))) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") } } });
        var request = ResponsesRequest(); request.EnableStreaming = true; request.StreamIdleTimeoutSeconds = 1;
        var delivered = new StringBuilder();
        var result = await service.ForwardStreamingAsync(request, (line, _) => { delivered.AppendLine(line); return Task.CompletedTask; });
        result.Success.Should().BeTrue(); result.IsStreamInterrupted.Should().BeFalse();
        delivered.ToString().Should().Contain("event: response.completed");
    }

    [Fact]
    public async Task Stream_capture_respects_limit_and_keeps_forwarding()
    {
        var sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n" +
            "data: {\"type\":\"response.completed\"}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var request = ResponsesRequest(); request.EnableStreaming = true; request.StreamCaptureLimitChars = 20;
        var forwarded = new StringBuilder();
        var result = await service.ForwardStreamingAsync(request, (line, _) => { forwarded.AppendLine(line); return Task.CompletedTask; });
        result.Success.Should().BeTrue();
        result.ResponseBody.Length.Should().Be(20);
        result.RawUpstreamResponseTruncated.Should().BeTrue();
        result.RawUpstreamResponseCaptureLimitChars.Should().Be(20);
        forwarded.ToString().Should().Contain("response.completed");
    }

    [Fact]
    public async Task Upstream_read_io_exception_is_not_classified_as_client_cancellation()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new FailingSseStream()) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") } } });
        var request = ResponsesRequest(); request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        result.IsCanceled.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.IsStreamInterrupted.Should().BeTrue();
        result.ResponseBody.Should().Contain("response.created");
    }

    private sealed class FailingSseStream : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes("data: {\"type\":\"response.created\"}\n\n");
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent) throw new IOException("simulated upstream read error");
            _sent = true; _prefix.CopyTo(buffer); return ValueTask.FromResult(_prefix.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Responses_incomplete_max_tokens_is_an_accepted_terminal()
    {
        const string sse = "data: {\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var request = ResponsesRequest(); request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        result.Success.Should().BeTrue(); result.IsStreamInterrupted.Should().BeFalse();
    }

    [Fact]
    public async Task Responses_eof_without_terminal_after_content_is_failure()
    {
        const string sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var request = ResponsesRequest(); request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        result.Success.Should().BeFalse(); result.IsStreamInterrupted.Should().BeTrue();
    }

    [Theory]
    [InlineData("response.completed", true)]
    [InlineData("response.incomplete", false)]
    [InlineData("response.failed", false)]
    public async Task Responses_terminal_events_are_distinguished(string terminal, bool success)
    {
        var sse = "data: {\"type\":\"response.created\"}\n\n" +
            "data: {\"type\":\"" + terminal + "\",\"response\":{\"status\":\"incomplete\"}}\n\n";
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var request = ResponsesRequest();
        request.EnableStreaming = true;
        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        result.Success.Should().Be(success);
        result.ResponseBody.Should().Contain(terminal);
    }

    [Fact]
    public async Task ForwardAsync_non_streaming_codex_sse_restores_completed_tool_call()
    {
        const string sse = """
            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"q\":\"test\"}","status":"completed"}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[],"usage":{"input_tokens":120,"output_tokens":7}}}

            """;
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var result = await service.ForwardAsync(ResponsesRequest());
        result.Success.Should().BeTrue();
        result.ResponseBody.Should().Contain("call_1").And.Contain("lookup");
        result.InputTokens.Should().Be(120);
        result.OutputTokens.Should().Be(7);
    }
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
        AllowNonSiteTarget = true,
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

    /// <summary>
    /// 验证流式空闲超时：上游发完首包后挂起时，超过设定秒数即判定挂起并终止，
    /// 不会无限占用读取循环（默认 0 不启用，本用例显式开启 1 秒）。
    /// </summary>
    [Fact]
    public async Task ForwardStreamingAsync_idle_timeout_terminates_hanging_upstream()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangingSseStream(
                Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n")))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") }
            }
        });

        var request = ResponsesRequest();
        request.EnableStreaming = true;
        request.StreamIdleTimeoutSeconds = 1;

        var result = await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask, CancellationToken.None);

        result.Success.Should().BeFalse("Responses 空闲超时不是成功完成；是否回退由客户端是否已接收内容决定");
        result.IsStreamInterrupted.Should().BeTrue();
        result.ErrorMessage.Should().Contain("空闲超过", "应给出明确的挂起诊断信息");
    }

    /// <summary>
    /// 先返回一段 SSE 首包，随后模拟上游挂起：ReadAsync 永久等待直到调用方取消。
    /// </summary>
    private sealed class HangingSseStream(byte[] prefix) : Stream
    {
        private int _offset;
        private bool _drained;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_drained)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                if (_offset >= prefix.Length)
                {
                    _drained = true;
                }
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
