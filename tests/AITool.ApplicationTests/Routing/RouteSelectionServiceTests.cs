using System.Net;
using System.Net.Http;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Routing;

/// <summary>
/// 验证代理转发服务在重试、空响应处理和流式结束判定上的行为。
/// </summary>
public sealed class ProxyForwardServiceTests
{
    /// <summary>
    /// 单路由第一次失败、第二次成功时，应在允许的重试次数内返回最终成功结果。
    /// </summary>
    [Fact]
    public async Task ForwardAsync_retries_before_returning_success()
    {
        // 按顺序返回失败和成功响应，模拟同一路由内的重试过程。
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"first\"}")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}")
            });

        // 自定义消息处理器能精确控制每次调用的返回值。
        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "OpenAI",
            TargetModelName = "gpt-5.5-a",
            RequestBody = "{\"model\":\"chat-prod\"}",
            RetryCount = 1,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        handler.CallCount.Should().Be(2);
        result.InputTokens.Should().Be(1);
        result.OutputTokens.Should().Be(2);
    }

    /// <summary>
    /// 上游返回 200 但响应体为空时，应把这次调用视为失败并继续下一次重试。
    /// </summary>
    [Fact]
    public async Task ForwardAsync_treats_empty_response_body_as_failure_and_retries_next_attempt()
    {
        // 第一段响应故意留空，第二段响应给出正常内容，用于验证补救重试路径。
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "OpenAI",
            TargetModelName = "gpt-5.5-a",
            RequestBody = "{\"model\":\"chat-prod\"}",
            RetryCount = 1,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        handler.CallCount.Should().Be(2);
        result.ResponseBody.Should().Contain("\"content\":\"ok\"");
        result.InputTokens.Should().Be(3);
        result.OutputTokens.Should().Be(4);
    }

    /// <summary>
    /// Anthropic 原生流在收到 message_stop 事件后，应视为完整结束，而不是中断。
    /// </summary>
    [Fact]
    public async Task ForwardAsync_treats_anthropic_stream_with_message_stop_as_completed()
    {
        // 这段 SSE 内容覆盖 message_start、文本增量、message_delta 和 message_stop 四类事件。
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event: message_start\n" +
                                            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":12}}}\n\n" +
                                            "event: content_block_delta\n" +
                                            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\n" +
                                            "event: message_delta\n" +
                                            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":3},\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
                                            "event: message_stop\n" +
                                            "data: {\"type\":\"message_stop\"}\n\n")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "Anthropic",
            TargetModelName = "claude-sonnet",
            RequestBody = "{\"model\":\"chat-prod\",\"stream\":true}",
            EnableStreaming = true,
            RetryCount = 0,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        result.IsStreaming.Should().BeTrue();
        result.IsStreamInterrupted.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
        result.InputTokens.Should().Be(12);
        result.OutputTokens.Should().Be(3);
    }

    /// <summary>
    /// OpenAI 原生流如果缺少 DONE 结束标记，应保留中断状态，避免把异常流误当成成功完成。
    /// </summary>
    [Fact]
    public async Task ForwardAsync_keeps_openai_stream_without_done_as_interrupted()
    {
        // 这里只返回一段普通增量数据，不给出最终 DONE，专门测试异常收尾场景。
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n")
            });

        var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://unit.test",
            TargetApiKey = "token",
            ProtocolType = "OpenAI",
            TargetModelName = "gpt-5.5-a",
            RequestBody = "{\"model\":\"chat-prod\",\"stream\":true}",
            EnableStreaming = true,
            RetryCount = 0,
            RequestTimeoutSeconds = 5
        });

        result.Success.Should().BeTrue();
        result.IsStreaming.Should().BeTrue();
        result.IsStreamInterrupted.Should().BeTrue();
        result.ErrorMessage.Should().Be("stream interrupted before normal completion");
    }
}

/// <summary>
/// 顺序返回预设 HTTP 响应的测试处理器，用来精确模拟多次转发调用的结果。
/// </summary>
public sealed class SequenceHandler : HttpMessageHandler
{
    /// <summary>
    /// 按调用顺序依次取出的响应队列。
    /// </summary>
    private readonly Queue<HttpResponseMessage> _responses;

    /// <summary>
    /// 接收一组预设响应，后续每次发送请求时依次返回。
    /// </summary>
    public SequenceHandler(params HttpResponseMessage[] responses)
    {
        _responses = new Queue<HttpResponseMessage>(responses);
    }

    /// <summary>
    /// 记录当前处理器已经收到的请求次数，便于断言重试次数是否符合预期。
    /// </summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// 拦截 HTTP 请求并返回预设响应，不真正发起网络调用。
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No more responses configured.");
        }

        return Task.FromResult(_responses.Dequeue());
    }
}
