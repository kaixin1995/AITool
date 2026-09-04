using AITool.Application.Common;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Scheduling;

/// <summary>
/// 日志保留清理调度服务：每天本地时间 03:00 后触发一次 <see cref="ILogRetentionService.PruneAsync"/>。
/// <para>
/// 替代原 Hangfire RecurringJob（"0 3 * * *"）：Hangfire 常驻 worker 线程与内存存储开销
/// 换来的是重试/仪表盘等用不上的能力，这里只需要"每天一次"的极简语义。
/// </para>
/// <para>
/// 与 Hangfire 的两点有意差异：① 每小时对表一次，到点且当天未执行过即触发——若进程在凌晨 3 点
/// 恰好不在运行（如夜间停机），当天的清理会在进程启动后的首次检查补做，而不是无限顺延；
/// ② 已执行标记启动时从持久化的 <c>LastUsageLogPrunedAt</c> 恢复，重启不会造成当天重复清理。
/// </para>
/// </summary>
public sealed class LogRetentionPruneService : BackgroundService
{
    /// <summary>对表间隔：每小时检查一次触发条件，空转成本可忽略。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    /// <summary>触发时刻（本地时间）：与原 Cron "0 3 * * *" 保持一致，晚于该时刻的当天首次检查即触发。</summary>
    private const int PruneHourLocal = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LogRetentionPruneService> _logger;
    private readonly IHostEnvironment _environment;

    /// <summary>当天（本地日期）是否已执行过清理；null 表示尚未从持久化状态恢复。</summary>
    private DateTime? _lastPrunedLocalDate;

    public LogRetentionPruneService(
        IServiceScopeFactory scopeFactory,
        ILogger<LogRetentionPruneService> logger,
        IHostEnvironment environment)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _environment = environment;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 测试环境跳过：避免定时清理影响集成测试对日志数据的断言。
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            await SeedLastPrunedDateAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            // 恢复失败只影响"重启后是否会重复清理一次"，不影响主流程。
            _logger.LogWarning(ex, "日志清理的上次执行时间恢复失败，将从内存状态重新计数");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryPruneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "日志保留清理执行异常，下一小时自动重试");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 从持久化的 <c>LastUsageLogPrunedAt</c> 恢复"当天已执行"标记，避免进程重启后当天重复清理。
    /// </summary>
    private async Task SeedLastPrunedDateAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings?.LastUsageLogPrunedAt is { } lastPruned)
        {
            var localDate = lastPruned.ToLocalTime().Date;
            if (localDate == DateTime.Today)
            {
                _lastPrunedLocalDate = localDate;
            }
        }
    }

    /// <summary>
    /// 检查触发条件并执行清理：本地时间已过 <see cref="PruneHourLocal"/> 且当天未执行过。
    /// </summary>
    private async Task TryPruneAsync(CancellationToken cancellationToken)
    {
        // 以触发时刻的日期作为"当日已执行"标记：清理可能耗时（大量删除），
        // 若跨午夜完成，不能把次日错标为已清理而跳过一天。
        var triggerLocal = DateTime.Now;
        if (!ShouldPrune(triggerLocal, _lastPrunedLocalDate))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<ILogRetentionService>();
        // 用 CancellationToken.None：关停时不中断删除写入（PruneAsync 内部是分批幂等删除），
        // 与旧 Hangfire 任务使用 CancellationToken.None 的语义一致。
        var result = await retention.PruneAsync(CancellationToken.None);
        _lastPrunedLocalDate = triggerLocal.Date;
        _logger.LogInformation("日志保留清理完成，删除 {Count} 条过期使用日志", result.UsageLogPrunedCount);
    }

    /// <summary>
    /// 触发判定（纯函数，便于测试）：本地时间已过清理时刻，且当天（本地日期）尚未执行过。
    /// </summary>
    public static bool ShouldPrune(DateTime nowLocal, DateTime? lastPrunedLocalDate, int pruneHourLocal = PruneHourLocal)
    {
        if (nowLocal.Hour < pruneHourLocal)
        {
            return false;
        }
        return lastPrunedLocalDate != nowLocal.Date;
    }
}
