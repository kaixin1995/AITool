using AITool.Domain.Detection;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Scheduling;

/// <summary>
/// 检测任务调度服务（秒级）：以 5 秒为 tick 轮询启用的检测任务，到点即执行，
/// 执行后按 <see cref="DetectionTask.IntervalSeconds"/> + 随机抖动计算下次触发时间。
/// <para>
/// 替代原 Hangfire RecurringJob + Cron 方案：Cron 最小粒度为分钟，无法支持 10 秒级间隔；
/// 轮询扫描对配置修改即时生效、重启安全，抖动（±20%，至少 ±3 秒）避免固定周期请求被上游识别。
/// </para>
/// </summary>
public sealed class DetectionTaskSchedulerService : BackgroundService
{
    /// <summary>轮询间隔。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    /// <summary>任务最小执行间隔（秒）。</summary>
    public const int MinIntervalSeconds = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DetectionTaskSchedulerService> _logger;

    /// <summary>每个任务的下次触发时间（内存态；重启后按“当前时间 + 间隔 + 随机抖动”重新铺开）。</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _nextRunAt = new();

    /// <summary>避免每 tick 都打日志的静默期。</summary>
    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;

    public DetectionTaskSchedulerService(
        IServiceScopeFactory scopeFactory,
        ILogger<DetectionTaskSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await MigrateLegacyCronTasksAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检测任务旧 Cron 迁移失败，不影响服务启动（任务将按默认 60s 间隔执行）");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单轮失败不终止调度，限频记录避免日志风暴。
                if (DateTimeOffset.UtcNow - _lastErrorLogAt > TimeSpan.FromMinutes(1))
                {
                    _logger.LogError(ex, "检测任务调度轮询异常，下一 tick 自动重试");
                    _lastErrorLogAt = DateTimeOffset.UtcNow;
                }
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 单轮扫描：加载启用任务，到点的执行（同步等待完成），并铺排下次触发时间。
    /// </summary>
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tasks = await dbContext.DetectionTasks
            .Where(t => t.IsEnabled)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var aliveIds = new HashSet<Guid>();

        foreach (var task in tasks)
        {
            aliveIds.Add(task.Id);
            var interval = TimeSpan.FromSeconds(Math.Max(MinIntervalSeconds, task.IntervalSeconds <= 0 ? 60 : task.IntervalSeconds));

            if (!_nextRunAt.TryGetValue(task.Id, out var due))
            {
                // 首次见到该任务：在“一个间隔”内随机铺开触发点，避免多任务同一时刻齐发。
                due = now + TimeSpan.FromSeconds(Random.Shared.NextDouble() * interval.TotalSeconds);
                _nextRunAt[task.Id] = due;
            }

            if (now < due)
            {
                continue;
            }

            // 执行（同步等待，防止上一轮未跑完又叠加一轮）。
            // 用 CancellationToken.None：与旧 Hangfire 调度一致，关停时不中断进行中的探测，
            // 避免执行记录卡在 running 状态；停机延迟以单轮检测时长为上限。
            try
            {
                await ExecuteDetectionTaskAsync(task.Id, scope, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检测任务 {TaskId} 执行异常", task.Id);
            }

            // 下次触发 = 当前 + 间隔 ± 抖动（±20%，至少 ±3 秒）。
            _nextRunAt[task.Id] = DateTimeOffset.UtcNow + ComputeJitteredDelay(interval);
        }

        // 清理已删除/禁用任务的触发点。
        foreach (var staleId in _nextRunAt.Keys.Where(id => !aliveIds.Contains(id)).ToList())
        {
            _nextRunAt.Remove(staleId);
        }
    }

    /// <summary>
    /// 计算带随机抖动的下次延迟：间隔 ±20%，抖动幅度至少 ±3 秒。
    /// </summary>
    public static TimeSpan ComputeJitteredDelay(TimeSpan interval)
    {
        var baseSeconds = Math.Max(MinIntervalSeconds, interval.TotalSeconds);
        var jitterSeconds = Math.Max(3.0, baseSeconds * 0.2);
        var offset = (Random.Shared.NextDouble() * 2 - 1) * jitterSeconds; // [-j, +j]
        var total = Math.Max(MinIntervalSeconds, baseSeconds + offset);
        return TimeSpan.FromSeconds(total);
    }

    /// <summary>
    /// 旧数据迁移：把遗留 Cron 表达式（*/N * * * * 形式，分钟粒度）换算为 IntervalSeconds。
    /// 幂等：仅处理 IntervalSeconds &lt;= 0 的任务。
    /// </summary>
    public async Task MigrateLegacyCronTasksAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var legacyTasks = await dbContext.DetectionTasks
            .Where(t => t.IntervalSeconds <= 0)
            .ToListAsync(cancellationToken);

        foreach (var task in legacyTasks)
        {
            task.IntervalSeconds = ParseLegacyCronToSeconds(task.CronExpression) ?? 60;
            await dbContext.UpdateAsync(task, cancellationToken);
        }

        if (legacyTasks.Count > 0)
        {
            _logger.LogInformation("已将 {Count} 个遗留 Cron 检测任务迁移为秒级间隔", legacyTasks.Count);
        }
    }

    /// <summary>
    /// 解析旧 Cron（仅支持旧 UI 产生的 */N * * * * 分钟步进形式），返回秒数；不匹配返回 null。
    /// </summary>
    public static int? ParseLegacyCronToSeconds(string? cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            cronExpression.Trim(), @"^\*/(\d+) \* \* \* \*$");
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var minutes) || minutes <= 0) return null;
        return minutes * 60;
    }

    /// <summary>
    /// 执行单次检测任务：按任务绑定（站点模型映射 / 模型 / 全部）逐一发起真实探测请求。
    /// </summary>
    public async Task ExecuteDetectionTaskAsync(Guid detectionTaskId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        await ExecuteDetectionTaskAsync(detectionTaskId, scope, cancellationToken);
    }

    private async Task ExecuteDetectionTaskAsync(Guid detectionTaskId, IServiceScope scope, CancellationToken cancellationToken)
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var requestService = scope.ServiceProvider.GetRequiredService<ModelHealthRequestService>();

        var detectionTask = await dbContext.DetectionTasks
            .FirstAsync(t => t.Id == detectionTaskId, cancellationToken);
        if (detectionTask is null || !detectionTask.IsEnabled) return;

        var execution = new DetectionTaskExecution
        {
            DetectionTaskId = detectionTaskId,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow
        };
        await dbContext.InsertAsync(execution, cancellationToken);

        // 目标解析优先级：绑定站点模型映射 > 绑定模型 > 全部映射。
        var query = dbContext.SiteModelMappings
            .WhereIF(detectionTask.SiteModelMappingId.HasValue, m => m.Id == detectionTask.SiteModelMappingId!.Value)
            .WhereIF(!detectionTask.SiteModelMappingId.HasValue && detectionTask.ModelLibraryItemId.HasValue,
                m => m.ModelLibraryItemId == detectionTask.ModelLibraryItemId!.Value);

        var mappings = await query.ToListAsync(cancellationToken);
        var runtimeSettings = await dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken)
            ?? new AITool.Domain.Operations.SystemRuntimeSettings();
        var successCount = 0;
        var failCount = 0;

        foreach (var batch in mappings.Chunk(Math.Max(1, runtimeSettings.DetectionConcurrency)))
        {
            var results = await Task.WhenAll(batch.Select(mapping => requestService.ProbeMappingAsync(mapping.Id, "detection-task", cancellationToken)));
            foreach (var result in results)
            {
                if (result.Status == "success") successCount++;
                else failCount++;
            }
        }

        execution.Status = "completed";
        execution.FinishedAt = DateTimeOffset.UtcNow;
        execution.Summary = $"共检测 {mappings.Count} 个映射，成功 {successCount}，失败 {failCount}";

        await dbContext.UpdateAsync(execution, cancellationToken);
    }
}
