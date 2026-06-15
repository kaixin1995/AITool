using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件 spool 后台服务。
/// 持续从事件总线读取事件并追加到本地 spool 文件，作为 Admin 不在线时的最小兜底。
/// 同时定期清理超龄和超数的 spool 文件，防止磁盘空间无限增长。
/// </summary>
public sealed class CoreEventSpoolBackgroundService : BackgroundService
{
    private readonly CoreAdminEventBus _eventBus;
    private readonly CoreEventSpoolStore _spoolStore;
    private readonly ILogger<CoreEventSpoolBackgroundService> _logger;

    /// <summary>
    /// 每写入多少条事件后触发一次 spool 文件清理检查。
    /// 这样可以在事件密集产生时及时发现并清理过期文件，
    /// 同时避免每次写入都检查带来的性能开销。
    /// </summary>
    private const int PruneCheckInterval = 100;

    /// <summary>
    /// 两次清理检查之间的最大间隔（秒）。
    /// 即使事件产生很少，也不会超过此间隔才执行清理检查。
    /// </summary>
    private static readonly TimeSpan MaxPruneInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// 上次执行清理检查的时间。
    /// </summary>
    private DateTimeOffset _lastPruneTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// 自上次清理检查以来写入的事件数量。
    /// </summary>
    private int _eventsSinceLastPrune;

    /// <summary>
    /// 初始化 Core 事件 spool 后台服务。
    /// </summary>
    public CoreEventSpoolBackgroundService(
        CoreAdminEventBus eventBus,
        CoreEventSpoolStore spoolStore,
        ILogger<CoreEventSpoolBackgroundService> logger)
    {
        _eventBus = eventBus;
        _spoolStore = spoolStore;
        _logger = logger;
    }

    /// <summary>
    /// 批量写入的最大事件数。达到即立即 flush，避免延迟过高。
    /// </summary>
    private const int BatchSize = 64;

    /// <summary>
    /// 持续监听事件总线，把事件按顺序批量追加到本地 spool 文件中。
    /// 读取一条后继续非阻塞读取更多事件，积累成批后单次文件写入，
    /// 减少 FileStream 打开/关闭开销。序号顺序由 Channel 单读者保证。
    /// 每写入一定数量的事件或超过一定时间后，触发 spool 文件清理检查。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<CoreAdminEventEnvelope>(BatchSize);
        long lastSequenceId = 0;

        try
        {
            await foreach (var envelope in _eventBus.Reader.ReadAllAsync(stoppingToken))
            {
                batch.Add(envelope);
                lastSequenceId = envelope.SequenceId;

                // 非阻塞地继续读取更多已就绪事件，积累成批。
                while (batch.Count < BatchSize && _eventBus.Reader.TryRead(out var more))
                {
                    batch.Add(more);
                    lastSequenceId = more.SequenceId;
                }

                // 批量写入（单次文件打开）。
                await _spoolStore.AppendBatchAsync(batch, stoppingToken);
                // 按最新序号通知 SSE 端点，Admin 可以立即拉取。
                _eventBus.NotifyNewEvents(lastSequenceId);

                // 检查是否需要执行 spool 清理：整批只检查一次（按本批条数累加计数），
                // 避免大批量事件密集时连续触发数十次剪枝扫描导致磁盘 IO 风暴。
                if (batch.Count > 0)
                {
                    AddEventsSinceLastPrune(batch.Count);
                    if (ShouldPrune())
                    {
                        await PruneWithLoggingAsync(stoppingToken);
                    }
                }

                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Core 事件 spool 后台服务异常退出");
        }
    }

    /// <summary>
    /// 累加自上次清理以来写入的事件数量（按批次累加）。
    /// </summary>
    private void AddEventsSinceLastPrune(int count)
    {
        // _eventsSinceLastPrune 仅在 ExecuteAsync 单一消费者线程内读写，无需原子操作。
        _eventsSinceLastPrune += count;
    }

    /// <summary>
    /// 判断是否应该执行 spool 文件清理检查（不再自增计数，由 AddEventsSinceLastPrune 预先累加）。
    /// 满足以下任一条件时触发：
    /// - 自上次清理以来写入了超过 PruneCheckInterval 条事件
    /// - 距离上次清理已超过 MaxPruneInterval
    /// </summary>
    private bool ShouldPrune()
    {
        if (_eventsSinceLastPrune >= PruneCheckInterval)
        {
            return true;
        }

        // 即使事件量少，也不要超过最大间隔
        if (DateTimeOffset.UtcNow - _lastPruneTime > MaxPruneInterval)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 执行 spool 清理并记录日志。
    /// 清理失败不影响主链路事件写入。
    /// </summary>
    private async Task PruneWithLoggingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var deletedCount = await _spoolStore.PruneExpiredFilesAsync(cancellationToken);
            if (deletedCount > 0)
            {
                _logger.LogInformation("Spool 文件清理完成，删除了 {DeletedCount} 个过期文件", deletedCount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 清理失败不影响主链路
            _logger.LogWarning(ex, "Spool 文件清理检查异常，不影响事件写入");
        }
        finally
        {
            _eventsSinceLastPrune = 0;
            _lastPruneTime = DateTimeOffset.UtcNow;
        }
    }
}
