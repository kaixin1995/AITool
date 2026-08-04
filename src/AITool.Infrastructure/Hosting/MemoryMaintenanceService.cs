using System.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 定期压缩大对象堆（LOH）的后台维护服务。
/// <para>
/// 代理转发每请求都会在 LOH（≥85KB 对象）上分配大字符串（请求体、SSE 响应累积），
/// .NET 默认 LOH 不压缩，回收后留下碎片空洞，进程工作集居高不下（dump 实测 LOH 碎片达数百 MB）。
/// 本服务每隔一段时间触发一次"压缩式 GC"：设置 <see cref="GCSettings.LargeObjectHeapCompactionMode"/>
/// 为 <see cref="GCLargeObjectHeapCompactionMode.CompactOnce"/>（仅影响下一次 GC，不永久改变行为），
/// 再以 <see cref="GCCollectionMode.Optimized"/> 触发回收，由 GC 自行判断是否值得执行。
/// </para>
/// </summary>
public sealed class MemoryMaintenanceService : BackgroundService
{
    /// <summary>
    /// 相邻两次 LOH 压缩的最小间隔。5 分钟是平衡回收及时性与 CPU 开销的经验值。
    /// </summary>
    private static readonly TimeSpan CompactionInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<MemoryMaintenanceService> _logger;
    private readonly IHostEnvironment _environment;

    public MemoryMaintenanceService(ILogger<MemoryMaintenanceService> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

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

    private void CompactLargeObjectHeapOnce()
    {
        var before = GC.GetTotalMemory(forceFullCollection: false);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        var after = GC.GetTotalMemory(forceFullCollection: false);
        _logger.LogDebug("LOH 压缩已触发，托管堆 {Before} -> {After} 字节", before, after);
    }
}
