using System.Threading.Channels;

namespace AITool.Admin.Services;

/// <summary>
/// 承载管理后台的长时间任务，避免控制器使用未受宿主生命周期管理的 fire-and-forget Task.Run。
/// </summary>
public sealed class AdminBackgroundTaskQueue : BackgroundService
{
    private const int Capacity = 8;

    private readonly Channel<Func<CancellationToken, Task>> _queue =
        Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ILogger<AdminBackgroundTaskQueue> _logger;

    /// <summary>
    /// 初始化管理后台任务队列。
    /// </summary>
    public AdminBackgroundTaskQueue(ILogger<AdminBackgroundTaskQueue> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 尝试将任务加入队列。任务入队后由宿主负责执行和取消。
    /// </summary>
    public bool TryQueue(Func<CancellationToken, Task> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return _queue.Writer.TryWrite(workItem);
    }

    /// <summary>
    /// 消费管理后台任务，并统一记录未处理异常。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var workItem in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 宿主停止时取消当前任务，不把正常停机误记为异常。
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "管理后台任务执行失败");
            }
        }
    }

    /// <summary>
    /// 停止接收新任务，再交给 BackgroundService 等待当前任务结束。
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}
