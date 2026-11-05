using System.Net;
using System.Text;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Proxy;

public sealed class ProxyForwardServiceReferenceGuardTests
{
    private const string UpstreamSseWithUsage = """
        data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"prompt_tokens_details":{"cached_tokens":2}}}

        data: {"id":"chatcmpl-test","choices":[{"delta":{"role":"assistant","content":"hello"}}]}

        data: {"choices":[{"delta":{"content":" world"}}]}

        data: [DONE]

        """;

    private static ProxyForwardService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance);
    }

    private static ProxyForwardRequest CreateStreamingRequest() => new()
    {
        TargetBaseUrl = "https://upstream.example/v1/chat/completions",
        AllowNonSiteTarget = true,
        TargetApiKey = "sk-test",
        ProtocolType = "OpenAI",
        TargetModelName = "gpt-4o",
        RequestBody = "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}",
        PreparedRequestBody = "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}",
        EnableStreaming = true,
        RequestTimeoutSeconds = 30,
        RetryCount = 0
    };

    [Fact]
    public async Task Streaming_guard_rejection_attaches_partial_result_with_status_tokens_and_empty_response_body()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(UpstreamSseWithUsage, Encoding.UTF8, "text/event-stream")
        });

        var lineCount = 0;
        var request = CreateStreamingRequest();

        Func<Task> act = async () =>
        {
            await service.ForwardStreamingAsync(
                request,
                (line, _) =>
                {
                    if (line.StartsWith("data: ") && !line.Contains("[DONE]"))
                    {
                        lineCount++;
                        // 第一条是 usage（经过提取后 input/output/cached 已赋给局部变量），
                        // 第二条是首个 content delta（触发 hasFirstContent=true），
                        // 在第三条（第2个 content delta）回调中抛出守卫异常。
                        if (lineCount >= 3)
                        {
                            var error = new UpstreamReferenceException("reference_store_unavailable", "Reference ownership could not be verified.");
                            error.SetHasExposedOutput(true);
                            throw error;
                        }
                    }
                    return Task.CompletedTask;
                },
                CancellationToken.None);
        };

        var thrown = await act.Should().ThrowAsync<UpstreamReferenceException>();
        var partial = thrown.Which.PartialResult;
        partial.Should().NotBeNull("转发层必须在抛出前附加已累计的流式统计 PartialResult");

        partial!.Success.Should().BeFalse();
        partial.StatusCode.Should().Be(200);
        partial.InputTokens.Should().Be(8, "提取自 usage.prompt_tokens (10) - cached_tokens (2)");
        partial.CachedTokens.Should().Be(2);
        partial.OutputTokens.Should().Be(5);
        partial.HasStartedStreaming.Should().BeTrue();
        partial.IsStreamInterrupted.Should().BeTrue();
        partial.IsStreaming.Should().BeTrue();
        partial.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
        partial.ResponseBody.Should().BeEmpty("守卫拒绝的流式输出绝对不能将 ResponseBody 填入 PartialResult（防止暴露未经守卫许可的内容）");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guard_snapshot_uses_downstream_exposure_not_buffered_upstream_content(bool exposed)
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.created\"}\n\ndata: {\"type\":\"response.output_text.delta\"}\n\n", Encoding.UTF8, "text/event-stream")
        });
        var linesSeen = 0;
        Func<Task> act = async () => await service.ForwardStreamingAsync(CreateStreamingRequest(), (line, _) =>
        {
            // 模拟按 SSE 块缓冲：上游首条 data 已解析，但首个内容仍可被拒绝；
            // 或者下游已写出非内容事件后再拒绝内容。
            if (line.Length == 0 && ++linesSeen == 2)
            {
                var error = new UpstreamReferenceException("reference_store_unavailable", "Reference ownership could not be verified.");
                error.SetHasExposedOutput(exposed);
                throw error;
            }
            return Task.CompletedTask;
        }, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<UpstreamReferenceException>()).Which;
        error.PartialResult.Should().NotBeNull();
        error.PartialResult!.HasStartedStreaming.Should().Be(exposed);
        error.PartialResult.IsStreamInterrupted.Should().Be(exposed);
        error.PartialResult.ResponseBody.Should().BeEmpty();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
