using System.Diagnostics;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// AllInOne 单进程形态的事件消费者：直接从 <see cref="CoreAdminEventBus"/> 内存通道读取事件，
/// 批量交给 <see cref="CoreEventPullService.ProcessEnvelopesAsync"/> 入库（与分离形态的
/// replay→ingest→ack 链路共享同一消费逻辑；进程内无需 ack/序列号推进）。
/// 仅在 AllInOne 宿主注册（Core 宿主经磁盘 spool + Admin 拉取，Admin 宿主不产生代理事件）。
/// </summary>
public sealed class InProcessCoreEventConsumerHostedService : BackgroundService
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxBatchWindow = TimeSpan.FromMilliseconds(100);

    private readonly CoreAdminEventBus _eventBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InProcessCoreEventConsumerHostedService> _logger;

    public InProcessCoreEventConsumerHostedService(
        CoreAdminEventBus eventBus,
        IServiceScopeFactory scopeFactory,
        ILogger<InProcessCoreEventConsumerHostedService> logger)
    {
        _eventBus = eventBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 逐条即时消费：AllInOne 为低频单进程形态，保证事件零滞留；
        // 失败由 FlushAsync 内部重试一次，再失败丢弃并告警。
        await foreach (var envelope in _eventBus.Reader.ReadAllAsync(stoppingToken))
        {
            await FlushAsync([envelope], stoppingToken);
        }
    }

    private async Task FlushAsync(IReadOnlyList<CoreAdminEventEnvelope> batch, CancellationToken cancellationToken)
    {
        // 失败重试一次（瞬时故障容错）；再失败才丢弃（AllInOne 无 spool 兜底，日志注明）。
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var pullService = scope.ServiceProvider.GetRequiredService<CoreEventPullService>();
                await pullService.ProcessEnvelopesAsync(batch, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= 2)
                {
                    _logger.LogError(ex, "进程内事件消费失败（已重试），本批次 {Count} 条将被丢弃。", batch.Count);
                    return;
                }

                _logger.LogWarning(ex, "进程内事件消费失败，500ms 后重试（批次 {Count} 条）。", batch.Count);
                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}