using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITool.Application.Proxy;
using AITool.IntegrationTests.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// 内存维护服务的空闲裁剪链路回归：
/// 判定真值表（<see cref="MemoryMaintenanceService.ShouldTrimProcess"/>）、
/// 在飞计数器的持票语义（<see cref="ProxyTrafficGauge"/>）、
/// 中间件在 /v1 请求全程持票并在响应结束后释放、
/// 平台裁剪调用（<see cref="ProcessMemoryTrimmer"/>）自身绝不抛出。
/// </summary>
public sealed class MemoryMaintenanceTrimTests
{
    [Fact]
    public void ShouldTrimProcess_requires_zero_active_traffic_and_quiet_window()
    {
        var now = DateTimeOffset.UtcNow;

        // 无流量 + 静默超过阈值 → 允许裁剪。
        MemoryMaintenanceService.ShouldTrimProcess(0, now - TimeSpan.FromMinutes(3), now).Should().BeTrue();
        // 无流量 + 静默超过阈值（边界：恰好等于阈值）→ 允许。
        MemoryMaintenanceService.ShouldTrimProcess(0, now - TimeSpan.FromMinutes(10), now).Should().BeTrue();
        // 静默不足阈值 → 拒绝（把请求间隙误判为空闲）。
        MemoryMaintenanceService.ShouldTrimProcess(0, now - TimeSpan.FromMinutes(1), now).Should().BeFalse();
        // 仍有在飞代理请求 → 一律拒绝。
        MemoryMaintenanceService.ShouldTrimProcess(1, now - TimeSpan.FromMinutes(3), now).Should().BeFalse();
        MemoryMaintenanceService.ShouldTrimProcess(5, now - TimeSpan.FromHours(1), now).Should().BeFalse();
        // 启动后从未有流量（ticks=0）→ 天然视为静默，允许启动后的例行清理。
        MemoryMaintenanceService.ShouldTrimProcess(0, new DateTimeOffset(0, TimeSpan.Zero), now).Should().BeTrue();
    }

    [Fact]
    public void Gauge_hold_reflects_at_least_one_active_ticket()
    {
        ProxyTrafficGauge.Begin();
        try
        {
            // 持票期间计数必然 ≥1（本票据）；并发套件中的其它请求只会更高，断言用下界。
            ProxyTrafficGauge.ActiveCount.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            ProxyTrafficGauge.End();
        }
    }

    [Fact]
    public void ProcessMemoryTrimmer_never_throws_on_current_platform()
    {
        // 尽力而为的维护调用：Windows 上执行 K32EmptyWorkingSet，Linux glibc 执行 malloc_trim，
        // musl 标记跳过；任何失败路径都必须被内部吞掉（否则维护服务反而成为不稳定源）。
        var act = () => ProcessMemoryTrimmer.TrimCurrentProcess();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Middleware_holds_traffic_gauge_for_inflight_proxy_request_and_releases_after_completion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new GateForwardService(entered, release);
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService(), configureServices: services =>
        {
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(fake);
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent("{\"model\":\"auto\",\"input\":\"hi\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var responseTask = client.SendAsync(request);
        // 阻塞桩已被控制器调用 = 请求正卡在上游转发中，此时票据必须被中间件持有。
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ProxyTrafficGauge.ActiveCount.Should().BeGreaterThanOrEqualTo(1);

        release.SetResult();
        using var response = await responseTask;
        response.Should().NotBeNull();

        // 响应结束后票据必须释放：全局计数归零（宽限窗口覆盖并发套件中的短命在飞请求）。
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (ProxyTrafficGauge.ActiveCount != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        ProxyTrafficGauge.ActiveCount.Should().Be(0);
    }

    /// <summary>
    /// 阻塞式转发桩：进入 ForwardAsync/ForwardStreamingAsync 时置 entered，
    /// 阻塞在 release 上模拟慢上游，用于把请求稳定地停在"在飞"状态。
    /// </summary>
    private sealed class GateForwardService(TaskCompletionSource entered, TaskCompletionSource release) : IProxyForwardService
    {
        public async Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Ok();
        }

        public async Task<ProxyForwardResult> ForwardStreamingAsync(
            ProxyForwardRequest request, Func<string, CancellationToken, Task> onSseDataAsync, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await onSseDataAsync("data: [DONE]\n\n", cancellationToken);
            return Ok();
        }

        private static ProxyForwardResult Ok() => new()
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"
        };
    }
}
