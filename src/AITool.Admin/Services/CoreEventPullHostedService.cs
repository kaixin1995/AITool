using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// Admin 侧定时事件拉取后台服务。
/// 周期性地创建 <see cref="CoreEventPullService"/> 实例执行拉取 → 消费 → 确认流程。
/// <para>
/// 使用独立的 DI scope 确保每次轮询使用新的数据库上下文和 HTTP 连接。
/// 核心拉取逻辑委托给 <see cref="CoreEventPullService"/>，便于独立测试。
/// </para>
/// </summary>
public sealed class CoreEventPullHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CoreEventPullHostedService> _logger;

    /// <summary>
    /// 拉取周期，每 10 秒执行一次。
    /// </summary>
    private static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 启动后首次拉取前的等待时间，确保 Core 宿主可能已经先启动完成。
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 初始化事件拉取后台服务。
    /// </summary>
    public CoreEventPullHostedService(
        IServiceProvider serviceProvider,
        ILogger<CoreEventPullHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// 后台服务主循环：周期性拉取事件、消费入库、提交确认。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后等待一小段时间，确保 Core 宿主可能已经启动完成
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Core 事件拉取服务已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 每轮创建独立的 DI scope，确保数据库上下文和 HTTP 连接不跨周期复用
                await using var scope = _serviceProvider.CreateAsyncScope();
                var pullService = ActivatorUtilities.CreateInstance<CoreEventPullService>(
                    scope.ServiceProvider);

                await pullService.PullAndProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常关闭
            }
            catch (Exception ex)
            {
                // 单轮处理失败不影响下一轮
                _logger.LogWarning(ex, "Core 事件拉取处理异常，将在下个周期重试");
            }

            // 等待下一个周期
            try
            {
                await Task.Delay(PullInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Core 事件拉取服务已停止");
    }
}
