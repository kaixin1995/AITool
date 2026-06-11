using System.Threading.Channels;
using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 最小事件总线。
/// 当前阶段先用内存通道把事件集中起来，为后续接入 Admin 实时消费、ack、spool 与 replay 做准备。
/// <para>
/// 除了主事件通道外，还提供轻量 SSE 订阅机制，
/// 用于 SSE 端点实时推送"有新事件可拉取"的信号给 Admin 宿主，
/// 避免 Admin 以固定间隔轮询 Core 的 replay 端点。
/// 支持多个 SSE 客户端同时订阅，每个订阅者独立接收通知。
/// </para>
/// </summary>
public sealed class CoreAdminEventBus
{
    private readonly Channel<CoreAdminEventEnvelope> _channel = Channel.CreateUnbounded<CoreAdminEventEnvelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    /// <summary>
    /// 所有活跃 SSE 订阅者的列表。
    /// 使用 lock 保护，因为订阅和取消订阅可能并发发生。
    /// </summary>
    private readonly List<WeakReference<SseSubscription>> _subscriptions = [];
    private readonly object _subscriptionLock = new();

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

    /// <summary>
    /// 发送"有新事件"通知，携带当前最新事件序号。
    /// 由 <see cref="CoreEventSpoolBackgroundService"/> 在写入事件到磁盘后调用。
    /// 通知所有活跃 SSE 订阅者，清理已被 GC 回收的死引用。
    /// </summary>
    /// <param name="latestSequenceId">当前已写入 spool 的最新事件序号。</param>
    public void NotifyNewEvents(long latestSequenceId)
    {
        lock (_subscriptionLock)
        {
            for (var i = _subscriptions.Count - 1; i >= 0; i--)
            {
                if (_subscriptions[i].TryGetTarget(out var subscription))
                {
                    // TryWrite 不阻塞，通道满时丢弃通知——安全因为序号单调递增
                    subscription.Writer.TryWrite(latestSequenceId);
                }
                else
                {
                    // 订阅者已被 GC 回收，清理死引用
                    _subscriptions.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// 创建一个新的 SSE 订阅，用于接收新事件通知。
    /// 返回的订阅对象实现 <see cref="IDisposable"/>，应在 SSE 连接关闭时释放。
    /// </summary>
    public SseSubscription Subscribe()
    {
        var subscription = new SseSubscription();

        lock (_subscriptionLock)
        {
            _subscriptions.Add(new WeakReference<SseSubscription>(subscription));
        }

        return subscription;
    }

    /// <summary>
    /// SSE 订阅对象，封装每个 SSE 客户端独立的通知通道。
    /// 使用 WeakReference 存储在总线中，确保断连后不泄漏。
    /// </summary>
    public sealed class SseSubscription : IDisposable
    {
        /// <summary>
        /// 每个订阅者的独立通知通道，缓冲深度 64。
        /// 慢消费者不会阻塞其他订阅者或 Spool 写入。
        /// </summary>
        private readonly Channel<long> _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        /// <summary>
        /// 通知通道写入器，供总线广播通知使用。
        /// </summary>
        internal ChannelWriter<long> Writer => _channel.Writer;

        /// <summary>
        /// 异步等待下一条通知，返回最新事件序号。
        /// 如果通道中没有数据则阻塞等待，直到收到通知或取消。
        /// </summary>
        public ValueTask<long> WaitNextAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAsync(cancellationToken);
        }

        /// <summary>
        /// 释放订阅资源，关闭通知通道。
        /// </summary>
        public void Dispose()
        {
            _channel.Writer.TryComplete();
        }
    }
}
