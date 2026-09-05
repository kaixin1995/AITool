using System.Runtime.InteropServices;

namespace AITool.Infrastructure.Common;

/// <summary>
/// glibc malloc 内存调优（Linux 部署），进程启动最早期从代码生效，全部由 appsettings 配置驱动。
/// <para>
/// 背景：glibc 默认按 8×CPU 核数创建线程 arena（每 arena 最大 64MB），多线程原生分配
/// （Kestrel/SQLite/HttpClient 缓冲）交错落在不同 arena，各自增长且 free 后几乎不归还系统，
/// 进程 RSS 阶梯式膨胀——实测曾以 ~4MB/分钟上涨而托管堆纹丝不动。
/// </para>
/// <para>
/// 三个旋钮（对应 glibc <c>mallopt</c>）：
/// <list type="bullet">
/// <item><c>MallocArenaMax</c>（M_ARENA_MAX）：arena 数量上限，遏制"多 arena 各自滞留"；</item>
/// <item><c>MallocTrimThresholdBytes</c>（M_TRIM_THRESHOLD）：free 时堆顶空闲超过该值即归还系统，
/// 默认 64KB（glibc 默认 128KB，收紧后归还更积极）；</item>
/// <item><c>MallocMmapThresholdBytes</c>（M_MMAP_THRESHOLD）：超过该值的分配直接走 mmap，
/// free 即整块归还系统。设为静态值同时禁用 glibc 的动态上调（动态阈值可涨到 32MB，
/// 导致大块缓冲滞留在 arena 里）——Kestrel 的大缓冲恰好落在此档，是治理 arena 棘轮最有效的一项。</item>
/// </list>
/// </para>
/// <para>
/// 生效时机：<c>MALLOC_*</c> 环境变量只能在进程启动前由外部注入（glibc 首次 malloc 时读取，
/// 进程内 SetEnvironmentVariable 无效）；本类通过运行时 <c>mallopt</c> 达到等价效果——
/// 只影响调用之后的分配行为，因此必须在任何大量原生分配发生前（Program.cs 建宿主阶段）尽早调用。
/// </para>
/// <para>
/// 配置：appsettings 的 <c>NativeMemory</c> 节；任一值设 0 或负数表示不干预该项。
/// Windows 与 musl（Alpine）自动跳过。
/// </para>
/// </summary>
public static class GlibcArenaLimiter
{
    /// <summary>glibc mallopt 参数编号。</summary>
    private const int MTrimThreshold = -1;
    private const int MMmapThreshold = -3;
    private const int MArenaMax = -8;

    /// <summary>
    /// 应用全部 malloc 调优参数。返回成功下发至少一项（仅 Linux glibc 环境）。
    /// </summary>
    public static bool TryApply(int maxArenas, int trimThresholdBytes, int mmapThresholdBytes)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var applied = false;
        TryMallopt(MArenaMax, maxArenas, ref applied);
        TryMallopt(MTrimThreshold, trimThresholdBytes, ref applied);
        TryMallopt(MMmapThreshold, mmapThresholdBytes, ref applied);
        return applied;
    }

    private static void TryMallopt(int param, int value, ref bool applied)
    {
        if (value <= 0)
        {
            return;
        }

        try
        {
            // musl 等非 glibc 的 libc 没有该符号，EntryPointNotFoundException 时直接放弃（默认行为无害）。
            if (mallopt(param, value) == 1)
            {
                applied = true;
            }
        }
        catch
        {
            // 诊断调优失败不影响启动。
        }
    }

    [DllImport("libc", EntryPoint = "mallopt")]
    private static extern int mallopt(int param, int value);
}
