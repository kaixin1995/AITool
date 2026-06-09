using AITool.Application.UsageLogs;
using AITool.Infrastructure.CoreRuntime;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 使用日志服务实现，将代理请求使用情况投递到后台批量写入队列。
/// </summary>
public sealed class UsageLogService : IUsageLogService
{
    /// <summary>
    /// 后台批量写入器，负责将日志条目投递到队列并异步刷盘。
    /// </summary>
    private readonly ProxyUsageLogBatchWriter _batchWriter;
    /// <summary>
    /// Core 事件发布器，负责把同一条 UsageLog 投影成事件送入最小事件总线。
    /// 这样后续接入 Admin 事件消费时，就不需要再回头改代理主链路的日志出口。
    /// </summary>
    private readonly CoreUsageLogEventPublisher _eventPublisher;

    /// <summary>
    /// 注入批量写入器与事件发布器。
    /// </summary>
    public UsageLogService(ProxyUsageLogBatchWriter batchWriter, CoreUsageLogEventPublisher eventPublisher)
    {
        _batchWriter = batchWriter;
        _eventPublisher = eventPublisher;
    }

    /// <summary>
    /// 主链路先写入现有数据库批量队列，再并行发布一份 Core 事件。
    /// 当前阶段依旧保留原有落库行为，先把事件链路以旁路方式接进来，避免影响现网统计与页面查询。
    /// </summary>
    public async Task LogAsync(UsageLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _batchWriter.EnqueueAsync(entry, cancellationToken);
        await _eventPublisher.PublishAsync(entry, cancellationToken);
    }
}
