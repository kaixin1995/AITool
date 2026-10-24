using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITool.Application.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 路由候选策略开关的端到端接线测试：真实控制器循环 + 假转发服务，
/// 验证「设置开关 → DB 写入 → 缓存失效 → 转发循环行为变化」全链路。
/// 开关关闭时的回归路径由 ProxyFallbackFlowTests 覆盖（行为不变）。
/// </summary>
public sealed class ProxyRoutingSwitchE2eTests
{
    private static async Task EnableSwitchesAsync(ProxyFallbackWebApplicationFactory factory, params string[] switches)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.SystemRuntimeSettings.FirstAsync(x => x.Id == 1);
        foreach (var flag in switches)
        {
            switch (flag)
            {
                case "tiered": settings.TieredRouteCooldownEnabled = true; break;
                case "strict": settings.StrictClientErrorFallbackEnabled = true; break;
                case "affinity": settings.SessionAffinityEnabled = true; break;
            }
        }

        await db.UpdateAsync(settings);
        scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRuntimeSettings();
    }

    private static HttpRequestMessage ChatRequest(string userMessage = "同样的开场白")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                $"{{\"model\":\"chat-prod\",\"messages\":[{{\"role\":\"user\",\"content\":\"{userMessage}\"}}]}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        return request;
    }

    /// <summary>分级冷却：配额类 429 直接休息该候选（非计数熔断），请求无感落到下一候选。</summary>
    [Fact]
    public async Task Tiered_cooldown_rests_quota_429_candidate_until_reset()
    {
        var fake = new FakeProxyForwardService
        {
            ForwardResultFactory = request => request.TargetApiKey == "upstream-key-1"
                ? new ProxyForwardResult
                {
                    Success = false,
                    StatusCode = 429,
                    ResponseBody = """{"error":{"code":"usage_limit_reached","message":"额度不足"}}""",
                    ErrorMessage = """{"error":{"code":"usage_limit_reached","message":"额度不足"}}""",
                }
                : new ProxyForwardResult
                {
                    Success = true,
                    StatusCode = 200,
                    ResponseBody = """{"choices":[{"message":{"content":"ok"}}]}""",
                },
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        await EnableSwitchesAsync(factory, "tiered");

        var first = await client.SendAsync(ChatRequest());
        (await first.Content.ReadAsStringAsync()).Should().Contain("ok", "配额 429 应无感回退到第二候选");
        fake.Requests.Should().HaveCount(2);

        // 休息生效：第二个请求不再尝试被休息的第一候选（只打第二候选）。
        var second = await client.SendAsync(ChatRequest());
        (await second.Content.ReadAsStringAsync()).Should().Contain("ok");
        fake.Requests.Should().HaveCount(3, "被休息的候选被跳过，仅第二候选再服务");
        fake.Requests[2].TargetApiKey.Should().Be("upstream-key-2");

        // 熔断存储应记录分级休息（带原因），而非仅失败计数。
        var store = factory.Services.GetRequiredService<RouteCircuitStateStore>();
        var states = store.GetAllCircuitStates();
        states.Values.Should().Contain(s => s.IsBlocked && !string.IsNullOrEmpty(s.RestReason) && s.RestReason.Contains("配额"),
            "配额类 429 应产生带原因的分级休息");
    }

    /// <summary>严格回退：非词表命中的 400 直接回给客户端，不再消耗其余候选。</summary>
    [Fact]
    public async Task Strict_fallback_returns_shape_error_400_without_burning_candidates()
    {
        var fake = new FakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = false,
                StatusCode = 400,
                ResponseBody = """{"error":{"message":"invalid tool schema shape","type":"invalid_request_error"}}""",
                ErrorMessage = """{"error":{"message":"invalid tool schema shape","type":"invalid_request_error"}}""",
            },
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        await EnableSwitchesAsync(factory, "strict");

        var response = await client.SendAsync(ChatRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "上游 400 原样回给客户端");
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid tool schema shape");
        fake.Requests.Should().ContainSingle("纯形态错误不应消耗其余候选");
    }

    /// <summary>严格回退关闭（默认）：400 仍走全量回退，行为与旧版一致。</summary>
    [Fact]
    public async Task Strict_fallback_disabled_still_falls_through_all_candidates()
    {
        var fake = new FakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = false,
                StatusCode = 400,
                ResponseBody = """{"error":{"message":"invalid tool schema shape"}}""",
                ErrorMessage = """{"error":{"message":"invalid tool schema shape"}}""",
            },
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(ChatRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.Requests.Should().HaveCount(2, "开关关闭时全量回退（全部候选尝试），与旧行为一致");
    }

    /// <summary>会话亲和：同一会话（同首条用户消息）连续请求粘住同一候选。</summary>
    [Fact]
    public async Task Session_affinity_pins_same_candidate_for_same_conversation()
    {
        var fake = new FakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = """{"choices":[{"message":{"content":"ok"}}]}""",
            },
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        await EnableSwitchesAsync(factory, "affinity");

        var first = await client.SendAsync(ChatRequest("帮我重构这个函数"));
        (await first.Content.ReadAsStringAsync()).Should().Contain("ok");
        fake.Requests.Should().HaveCount(1);
        fake.Requests[0].TargetApiKey.Should().Be("upstream-key-1", "首次请求按优先级取第一候选");

        var second = await client.SendAsync(ChatRequest("帮我重构这个函数"));
        (await second.Content.ReadAsStringAsync()).Should().Contain("ok");
        fake.Requests.Should().HaveCount(2);
        fake.Requests[1].TargetApiKey.Should().Be("upstream-key-1", "同一会话（同首条消息）应粘住上次成功候选");

        // 不同会话（不同首条消息）走正常优先序，不串会话。
        var third = await client.SendAsync(ChatRequest("写个单元测试"));
        (await third.Content.ReadAsStringAsync()).Should().Contain("ok");
        fake.Requests.Should().HaveCount(3);
    }
}
