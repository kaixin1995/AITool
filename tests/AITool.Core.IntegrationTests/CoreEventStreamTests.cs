using System.Net;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// Core SSE 事件通知流端点集成测试。
/// 验证 SSE 端点的连接建立、事件通知推送和多客户端订阅能力。
/// </summary>
public sealed class CoreEventStreamTests
{
    /// <summary>
    /// SSE 端点应返回 text/event-stream 内容类型和正确的响应头。
    /// </summary>
    [Fact]
    public async Task Stream_returns_correct_content_type()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "api/core/events/stream",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    /// <summary>
    /// 当有事件写入总线后，SSE 端点应推送包含 latestSequenceId 的通知。
    /// </summary>
    [Fact]
    public async Task Stream_pushes_notification_when_events_arrive()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 启动 SSE 连接，在后台读取
        using var response = await client.GetAsync(
            "api/core/events/stream",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var eventBus = factory.Services.GetRequiredService<CoreAdminEventBus>();

        // 发布一条事件到总线，触发通知
        var envelope = new CoreAdminEventEnvelope
        {
            SequenceId = 42,
            EventType = "usage-log",
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = """{"requestId":"00000000-0000-0000-0000-000000000001"}"""
        };
        await eventBus.PublishAsync(envelope);

        // 手动触发通知（模拟 SpoolBackgroundService 的行为）
        eventBus.NotifyNewEvents(42);

        // 读取 SSE 流中的数据行
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var receivedData = false;
        var deadline = DateTime.UtcNow.AddSeconds(4);

        while (DateTime.UtcNow < deadline && !cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;

            // 跳过空行和注释行（心跳）
            if (string.IsNullOrEmpty(line) || line.StartsWith(':')) continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var json = line["data: ".Length..];
                json.Should().Contain("latestSequenceId");
                json.Should().Contain("42");
                receivedData = true;
                break;
            }
        }

        receivedData.Should().BeTrue("SSE 端点应在事件写入后推送通知");
    }

    /// <summary>
    /// SSE 端点应支持多个客户端同时连接，每个客户端独立接收通知。
    /// </summary>
    [Fact]
    public async Task Stream_supports_multiple_concurrent_subscribers()
    {
        await using var factory = new CoreHostWebApplicationFactory();

        // 创建两个独立的 SSE 客户端连接
        using var client1 = factory.CreateClient();
        using var client2 = factory.CreateClient();

        using var response1 = await client1.GetAsync(
            "api/core/events/stream",
            HttpCompletionOption.ResponseHeadersRead);
        using var response2 = await client2.GetAsync(
            "api/core/events/stream",
            HttpCompletionOption.ResponseHeadersRead);

        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var eventBus = factory.Services.GetRequiredService<CoreAdminEventBus>();

        // 发布一条事件并触发通知
        var envelope = new CoreAdminEventEnvelope
        {
            SequenceId = 99,
            EventType = "route-fallback",
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = """{"requestId":"00000000-0000-0000-0000-000000000002"}"""
        };
        await eventBus.PublishAsync(envelope);
        eventBus.NotifyNewEvents(99);

        // 两个客户端都应该收到通知
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var data1 = await ReadFirstDataLineAsync(response1, cts.Token);
        var data2 = await ReadFirstDataLineAsync(response2, cts.Token);

        data1.Should().Contain("99");
        data2.Should().Contain("99");
    }

    /// <summary>
    /// 从 SSE 响应流中读取第一条 data 行内容。
    /// 跳过空行和注释行（心跳），超时返回 null。
    /// </summary>
    private static async Task<string?> ReadFirstDataLineAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var deadline = DateTime.UtcNow.AddSeconds(4);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) return null;

            // 跳过空行和注释行（心跳）
            if (string.IsNullOrEmpty(line) || line.StartsWith(':')) continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line["data: ".Length..];
            }
        }

        return null;
    }
}
