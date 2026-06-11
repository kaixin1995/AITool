using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 CircuitBreaker 事件发布器将熔断参数正确投影到事件信封。
/// </summary>
public sealed class CoreCircuitBreakerEventPublisherTests
{
    /// <summary>
    /// 发布熔断事件后，信封类型应为 circuit-breaker，所有字段正确序列化。
    /// </summary>
    [Fact]
    public async Task PublishAsync_projects_circuit_opened_args_into_envelope()
    {
        var sequenceProvider = TestCoreEventSequenceProvider.Create();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreCircuitBreakerEventPublisher(sequenceProvider, eventBus);

        var routeId = Guid.NewGuid();
        var recoveryTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var args = new CircuitOpenedEventArgs
        {
            RouteId = routeId,
            FailureCount = 7,
            FailThreshold = 5,
            BlockDuration = TimeSpan.FromMinutes(5),
            RecoveryTime = recoveryTime
        };

        await publisher.PublishAsync(args);

        var envelope = await eventBus.Reader.ReadAsync();
        envelope.SequenceId.Should().Be(1);
        envelope.EventType.Should().Be("circuit-breaker");

        var payload = JsonSerializer.Deserialize<CoreCircuitBreakerEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.RouteId.Should().Be(routeId);
        payload.FailureCount.Should().Be(7);
        payload.FailThreshold.Should().Be(5);
        payload.BlockDuration.Should().Be(TimeSpan.FromMinutes(5));
        payload.RecoveryTime.Should().Be(recoveryTime);
        payload.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 传入 null 参数应抛出 ArgumentNullException。
    /// </summary>
    [Fact]
    public async Task PublishAsync_throws_on_null_args()
    {
        var sequenceProvider = TestCoreEventSequenceProvider.Create();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreCircuitBreakerEventPublisher(sequenceProvider, eventBus);

        var act = () => publisher.PublishAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// 连续发布多条事件时，序号应递增。
    /// </summary>
    [Fact]
    public async Task PublishAsync_sequential_calls_increment_sequence()
    {
        var sequenceProvider = TestCoreEventSequenceProvider.Create();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreCircuitBreakerEventPublisher(sequenceProvider, eventBus);

        for (int i = 0; i < 3; i++)
        {
            var args = new CircuitOpenedEventArgs
            {
                RouteId = Guid.NewGuid(),
                FailureCount = i + 3,
                FailThreshold = 3,
                BlockDuration = TimeSpan.FromMinutes(5),
                RecoveryTime = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            await publisher.PublishAsync(args);
        }

        var env1 = await eventBus.Reader.ReadAsync();
        var env2 = await eventBus.Reader.ReadAsync();
        var env3 = await eventBus.Reader.ReadAsync();

        env1.SequenceId.Should().Be(1);
        env2.SequenceId.Should().Be(2);
        env3.SequenceId.Should().Be(3);
    }
}
