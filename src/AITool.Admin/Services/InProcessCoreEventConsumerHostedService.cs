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
        var batch = new List<CoreAdminEventEnvelope>(MaxBatchSize);
        var sw = Stopwatch.StartNew();

        await foreach (var envelope in _eventBus.Reader.ReadAllAsync(stoppingToken))
        {
            batch.Add(envelope);
            if (batch.Count >= MaxBatchSize || sw.Elapsed >= MaxBatchWindow)
            {
                await FlushAsync(batch, stoppingToken);
                batch.Clear();
                sw.Restart();
            }
        }

        if (batch.Count > 0)
        {
            await FlushAsync(batch, stoppingToken);
        }
    }

    private async Task FlushAsync(List<CoreAdminEventEnvelope> batch, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var pullService = scope.ServiceProvider.GetRequiredService<CoreEventPullService>();
            await pullService.ProcessEnvelopesAsync(batch, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "进程内事件消费失败，本批次 {Count} 条将被丢弃（AllInOne 形态无磁盘 spool 兜底）。", batch.Count);
        }
    }
}