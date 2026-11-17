using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AITool.Web.Services;

/// <summary>
/// 进程内存裁剪的跨平台封装：在托管堆已完整回收之后，把原生侧可释放的驻留内存归还系统。
/// <para>
/// - Windows：K32EmptyWorkingSet 把工作集页换出，下次访问以软缺页换回——只影响空闲后
///   首个请求的触达延迟，不影响任何活跃对象；
/// - Linux(glibc)：malloc_trim(0) 遍历各 arena 归还空闲页——GlibcArenaLimiter 的 mallopt
///   阈值只能归还堆顶与 mmap 大块，arena 中部的碎片页需要显式 trim 才回到系统。
/// </para>
/// <para>
/// 裁剪是尽力而为的维护动作：任何平台/符号失败（如 musl 无 malloc_trim）都静默跳过或标记后
/// 不再重试，绝不抛出。仅在 <see cref="MemoryMaintenanceService"/> 判定进程空闲后调用。
/// </para>
/// </summary>
internal static class ProcessMemoryTrimmer
{
    /// <summary>malloc_trim 符号不可用（musl/Alpine 等）：标记后永久跳过，避免每次调用都抛异常。</summary>
    private static int _mallocTrimUnavailable;

    /// <summary>执行一次裁剪；失败静默（诊断级别记录），不影响主进程。</summary>
    public static void TrimCurrentProcess(ILogger? logger = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.GetCurrentProcess();
                _ = EmptyWorkingSet(process.Handle);
            }
            else if (OperatingSystem.IsLinux() && Volatile.Read(ref _mallocTrimUnavailable) == 0)
            {
                // 返回非 0 表示有归还内存，0 表示当前无空闲可还；两者均为调用成功，无需区分。
                _ = malloc_trim(0);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // musl 等非 glibc 的 libc 没有 malloc_trim，标记后不再重试（与 GlibcArenaLimiter 同策略）。
            if (OperatingSystem.IsLinux())
            {
                Volatile.Write(ref _mallocTrimUnavailable, 1);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "进程内存裁剪调用失败，已跳过");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")]
    private static extern int EmptyWorkingSet(IntPtr process);

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(nuint pad);
}
