using AITool.Admin.Services;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// Core 状态探测器单测：缓存 TTL 生效（TTL 内复用结果不再发握手请求）、
/// 离线降级文案、探测结果包含同步状态。
/// </summary>
public sealed class CoreStatusProbeTests
{
    private sealed class CountingHandler : HttpClientHandler
    {
        public int Requests;
        public bool Offline;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (Offline)
            {
                throw new HttpRequestException("mock offline");
            }

            var body = """{"coreInstanceId":"core-1","coreStartedAt":"2026-09-05T10:00:00Z","appliedConfigVersion":1727000000000,"appliedConfigHash":"h","ready":true,"latestSequenceId":0,"activeRequestCount":0,"configSyncDecision":"none","hasSpoolBacklog":false}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"))
            });
        }
    }

    private static (CoreStatusProbe Probe, CountingHandler Handler) Create(ILogger<CoreStatusProbe> logger)
    {
        var handler = new CountingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://core.test/") };
        var client = new CoreAdminClient(httpClient);
        var store = new CoreSyncStatusStore();
        var probe = new CoreStatusProbe(client, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<CoreStatusProbe>.Instance);
        return (probe, handler);
    }

    [Fact]
    public async Task Probe_caches_result_within_ttl()
    {
        var (probe, handler) = Create(NullLogger<CoreStatusProbe>.Instance);
        handler.Offline = false;

        var r1 = await probe.ProbeAsync(CancellationToken.None);
        var r2 = await probe.ProbeAsync(CancellationToken.None);

        handler.Requests.Should().Be(1, "TTL 内第二次探测应复用缓存，不再发握手");
        r1.Status.Should().Contain("在线");
        r2.Status.Should().Be(r1.Status);
    }

    [Fact]
    public async Task Probe_offline_degrades_with_reason()
    {
        var (probe, handler) = Create(NullLogger<CoreStatusProbe>.Instance);
        handler.Offline = true;

        var r = await probe.ProbeAsync(CancellationToken.None);

        r.Status.Should().Contain("离线");
        r.Status.Should().Contain("HttpRequestException");
    }

    private static Microsoft.Extensions.Logging.ILogger NullLoggerFactoryLogger()
        => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}