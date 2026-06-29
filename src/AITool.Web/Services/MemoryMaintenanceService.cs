using System.Runtime;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 定期压缩大对象堆（LOH）的后台维护服务。
/// <para>
/// 代理转发服务每请求都会在 LOH（≥85KB 对象）上分配大字符串（请求体、SSE 响应累积），
/// .NET 默认 LOH 不压缩，回收后留下碎片空洞，进程工作集居高不下（dump 实测 LOH 碎片达数百 MB）。
/// 本服务每隔一段时间触发一次"压缩式 GC"：设置 <see cref="GCSettings.LargeObjectHeapCompactionMode"/>
/// 为 <see cref="GCLargeObjectHeapCompactionMode.CompactOnce"/>（仅影响下一次 GC，不永久改变行为），
/// 再以 <see cref="GCCollectionMode.Optimized"/> 触发回收，由 GC 自行判断是否值得执行。
/// </para>
/// <para>
/// 这是 .NET 官方推荐处理 LOH 碎片化的标准手段，CPU 开销受 GC 调度控制，不会无谓占用主链路。
/// </para>
/// </summary>
internal sealed class MemoryMaintenanceService : BackgroundService
{
    /// <summary>
    /// 相邻两次 LOH 压缩的最小间隔。间隔过短会增加 GC 开销，过长则碎片来不及回收。
    /// 5 分钟是平衡回收及时性与 CPU 开销的经验值。
    /// </summary>
    private static readonly TimeSpan CompactionInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<MemoryMaintenanceService> _logger;
    private readonly IHostEnvironment _environment;

    public MemoryMaintenanceService(ILogger<MemoryMaintenanceService> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    /// <summary>
    /// 后台循环：周期性触发 LOH 压缩式 GC。测试环境直接跳过，避免 GC 干扰断言。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        _logger.LogInformation("内存维护服务已启动，每 {Interval} 触发一次 LOH 压缩", CompactionInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CompactionInterval, stoppingToken);
                CompactLargeObjectHeapOnce();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 维护服务自身的异常绝不能影响主进程，记录后继续循环。
                _logger.LogError(ex, "LOH 压缩触发异常，已忽略并继续");
            }
        }
    }

    /// <summary>
    /// 触发一次 LOH 压缩式 GC。
    /// 抽成独立方法便于测试，且异常边界清晰。
    /// </summary>
    private void CompactLargeObjectHeapOnce()
    {
        var before = GC.GetTotalMemory(forceFullCollection: false);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        var after = GC.GetTotalMemory(forceFullCollection: false);
        _logger.LogDebug("LOH 压缩已触发，托管堆 {Before} -> {After} 字节", before, after);
    }
}
