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

    [Fact]
    public async Task RateLimit_retry_waits_backoff_delay_between_attempts()
    {
        // Retry-After: 1 → 每次重试前等待 1 秒；两次 429 重试后成功，总耗时至少 ~2 秒。
        var service = CreateService(Seq(
            _ => WithRetryAfter(Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), 1),
            _ => WithRetryAfter(Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), 1),
            _ => Json(HttpStatusCode.OK, SuccessBody)), out _);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.ForwardAsync(BasicRequest(3), CancellationToken.None);
        stopwatch.Stop();

        result.Success.Should().BeTrue();
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(1900, "两次重试各等待 Retry-After=1s，不允许零延迟重击");
    }

    [Fact]
    public async Task RateLimit_retry_count_is_reported_on_result()
    {
        // 前 2 次 429、第 3 次成功：结果应上报实际重试 2 次（usage 链路据此展示）。
        var service = CreateService(Seq(
            _ => WithRetryAfter(Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), 0),
            _ => WithRetryAfter(Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), 0),
            _ => Json(HttpStatusCode.OK, SuccessBody)), out _);

        var result = await service.ForwardAsync(BasicRequest(3), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.RateLimitRetryCount.Should().Be(2, "两次 429 各触发一次重试");
    }

    [Fact]
    public async Task RateLimit_retry_count_reported_when_budget_exhausted()
    {
        // N=3 恒定 429：第 3 次 429 判定失败，此前执行了 2 次重试。
        var service = CreateService(_ => WithRetryAfter(Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"), 0), out _);

        var result = await service.ForwardAsync(BasicRequest(3), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.RateLimitRetryCount.Should().Be(2, "第 3 次 429 耗尽预算，前两次为重试");
    }

    [Fact]
    public void Resolve429RetryDelay_honors_header_delta_and_caps()
    {
        var withDelta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        withDelta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        ProxyForwardService.Resolve429RetryDelay(withDelta).Should().Be(TimeSpan.FromSeconds(3));

        var huge = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        huge.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        ProxyForwardService.Resolve429RetryDelay(huge).Should().Be(TimeSpan.FromSeconds(10), "Retry-After 封顶 10 秒");

        var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        ProxyForwardService.Resolve429RetryDelay(past).Should().Be(TimeSpan.FromMilliseconds(1500), "非法/零值回退默认 1.5s");

        ProxyForwardService.Resolve429RetryDelay(new HttpResponseMessage(HttpStatusCode.TooManyRequests))
            .Should().Be(TimeSpan.FromMilliseconds(1500), "无头回退默认 1.5s");
    }

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response, int seconds)
    {
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
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
