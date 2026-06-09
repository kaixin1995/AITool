using System.Threading.Channels;
using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 最小事件总线。
/// 当前阶段先用内存通道把事件集中起来，为后续接入 Admin 实时消费、ack、spool 与 replay 做准备。
/// </summary>
public sealed class CoreAdminEventBus
{
    private readonly Channel<CoreAdminEventEnvelope> _channel = Channel.CreateUnbounded<CoreAdminEventEnvelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    /// <summary>
    /// 发布一条事件到总线。
    /// </summary>
    public ValueTask PublishAsync(CoreAdminEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return _channel.Writer.WriteAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// 返回事件读取器，供后续 Admin 通信层消费。
    /// </summary>
    public ChannelReader<CoreAdminEventEnvelope> Reader => _channel.Reader;
}
