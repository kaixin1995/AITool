using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 性能 sanity 门：真实 Kestrel + 本地 mock 上游下，连续非流式转发的平均延迟上限。
/// 防止流式增强（心跳/可恢复缓冲）或鉴权中间件对主链路引入可感知的额外开销。
/// 阈值取保守值（本地 mock 网络 + .NET 启动抖动），显著超出即视为回归信号。
/// </summary>
[Collection("CoreStandalone")]
public sealed class PerformanceSanityTests
{
    private const string AccessKey = "test-access-key-standalone";

    [Fact]
    public async Task Non_streaming_forward_latency_within_budget()
    {
        using var upstream = new MockOpenAiUpstream();
        var core = await CoreProcessLauncher.StartAsync(upstream.Port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{core.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

            // 预热（首请求含缓存冷启动）。
            var warmup = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "warm" } } });
            warmup.StatusCode.Should().Be(HttpStatusCode.OK);

            const int n = 100;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
            {
                var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", messages = new[] { new { role = "user", content = $"ping-{i}" } } });
                response.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            stopwatch.Stop();
            var avgMs = stopwatch.ElapsedMilliseconds / (double)n;
            avgMs.Should().BeLessThan(100, $"本地 mock 上游下平均转发延迟应 <100ms（实测 {avgMs:F1}ms）");
        }
        finally
        {
            core.Dispose();
        }
    }

    [Fact]
    public async Task Event_spool_stays_quiet_when_idle_after_streams()
    {
        // 心跳/缓冲在流结束后不得有残留活动（进程存活且健康即可，主要验证无泄漏性异常）。
        using var upstream = new MockOpenAiUpstream(streamByDefault: true, frameCount: 3, frameDelayMs: 10);
        var core = await CoreProcessLauncher.StartAsync(upstream.Port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{core.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

            for (var i = 0; i < 20; i++)
            {
                var response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                    {
                        Content = JsonContent.Create(new { model = "gpt-conserve", stream = true, messages = new[] { new { role = "user", content = $"s-{i}" } } })
                    },
                    HttpCompletionOption.ResponseHeadersRead);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                await response.Content.ReadAsStringAsync(); // 完整消费
            }

            var health = await client.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK, "连续流式请求后宿主应保持健康");
        }
        finally
        {
            core.Dispose();
        }
    }
}