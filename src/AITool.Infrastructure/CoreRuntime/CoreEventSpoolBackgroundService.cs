using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件 spool 后台服务。
/// 当前阶段先从事件总线持续读取事件并落到本地磁盘，作为 Admin 不在线时的最小兜底。
/// 这样后续再接入真正的长连接推送与 ack/replay 时，不需要重做主链路出口。
/// </summary>
public sealed class CoreEventSpoolBackgroundService : BackgroundService
{
    private readonly CoreAdminEventBus _eventBus;
    private readonly CoreEventSpoolStore _spoolStore;
    private readonly ILogger<CoreEventSpoolBackgroundService> _logger;

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
    /// 持续监听事件总线，把事件按顺序追加到本地 spool 文件中。
    /// 当前阶段这是最小可靠性保障，后续再增加“已实时投递成功则可跳过写盘”的优化策略。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var envelope in _eventBus.Reader.ReadAllAsync(stoppingToken))
            {
                await _spoolStore.AppendAsync(envelope, stoppingToken);
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
}
