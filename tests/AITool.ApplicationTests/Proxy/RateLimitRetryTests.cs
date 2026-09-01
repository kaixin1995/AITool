using System.Net;
using System.Text;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 验证 429（速率限制）连续重试语义：
/// - RateLimitRetryCount=0：一次 429 即失败（原逻辑）。
/// - RateLimitRetryCount=N：连续 N 次 429 才失败；中间成功一次即成功。
/// - 429 重试不消耗通用 RetryCount 预算。
/// </summary>
public sealed class RateLimitRetryTests
{
    private static ProxyForwardService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder, out StubRateLimitHandler handler)
    {
        handler = new StubRateLimitHandler(responder);
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance);
    }

    private static ProxyForwardRequest BasicRequest(int rateLimitRetryCount) => new()
    {
        TargetBaseUrl = "https://upstream.example.com",
        TargetEndpointPathMode = "standard-root",
        TargetApiKey = "sk-test",
        ProtocolType = "OpenAI",
        TargetModelName = "gpt-test",
        RequestBody = "{\"model\":\"gpt-test\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
        RequestTimeoutSeconds = 10,
        RetryCount = 0,
        RateLimitRetryCount = rateLimitRetryCount
    };

    [Fact]
    public async Task RateLimitRetryCount_zero_first_429_fails_immediately()
    {
        var service = CreateService(_ => Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), out var handler);

        var result = await service.ForwardAsync(BasicRequest(0), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(429);
        handler.CallCount.Should().Be(1, "默认 0：一次 429 即失败，不重试");
    }

    [Fact]
    public async Task RateLimitRetryCount_three_fails_after_three_consecutive_429s()
    {
        var service = CreateService(_ => Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), out var handler);

        var result = await service.ForwardAsync(BasicRequest(3), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(429);
        handler.CallCount.Should().Be(3, "连续 3 次 429 即判定失败（N=允许的429响应总次数）");
    }

    [Fact]
    public async Task RateLimitRetryCount_success_after_two_429s_counts_as_success()
    {
        // 前 2 次返回 429，第 3 次成功——设置 3 时不应失败。
        var service = CreateService(Seq(
            _ => Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"),
            _ => Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"),
            _ => Json(HttpStatusCode.OK, SuccessBody)), out var handler);

        var result = await service.ForwardAsync(BasicRequest(3), CancellationToken.None);

        result.Success.Should().BeTrue("429 之后成功一次即算成功");
        result.StatusCode.Should().Be(200);
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task RateLimitRetryCount_zero_success_on_first_try_unchanged()
    {
        var service = CreateService(_ => Json(HttpStatusCode.OK, SuccessBody), out var handler);

        var result = await service.ForwardAsync(BasicRequest(0), CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RateLimitRetryCount_does_not_consume_generic_retry_budget()
    {
        // 上游恒定 429：RateLimitRetryCount=2（允许 2 次 429）、通用 RetryCount=1。
        var service = CreateService(_ => Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), out var handler);
        var request = BasicRequest(2);
        request.RetryCount = 1;

        var result = await service.ForwardAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.CallCount.Should().Be(2, "429 预算耗尽立即失败顺位下一候选：N=2 恰好 2 次调用，不消耗通用重试预算");
    }

    private const string SuccessBody = "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static Func<HttpRequestMessage, HttpResponseMessage> Seq(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        var index = 0;
        return request =>
        {
            var i = Math.Min(index, responders.Length - 1);
            index++;
            return responders[i](request);
        };
    }

    private sealed class StubRateLimitHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }
}
