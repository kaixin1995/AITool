namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 代理流量在飞计数器，供 <see cref="MemoryMaintenanceService"/> 判定"进程空闲"。
/// <para>
/// 由 <see cref="GatewayEnhancementMiddleware"/> 在每个 /v1 与 /mcp 请求（含流式与 WebSocket）
/// 的全生命周期持票：进入时 <see cref="Begin"/>，响应完全结束后 <see cref="End"/>。
/// 计数归零且静默超过阈值即视为空闲，允许执行进程内存裁剪。
/// </para>
/// <para>
/// 管理后台 API 请求不参与计数：它们通常毫秒级且不含长流；即使空闲裁剪与管理请求偶发重叠，
/// 裁剪也只影响该请求的首次触达延迟（软缺页换回），不影响正确性。
/// </para>
/// </summary>
internal static class ProxyTrafficGauge
{
    private static int _active;
    private static long _lastActivityTicks;

    /// <summary>请求进入时持票，并刷新最近活动时刻。</summary>
    public static void Begin()
    {
        Interlocked.Increment(ref _active);
        Volatile.Write(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>请求响应完全结束后释放票据。</summary>
    public static void End() => Interlocked.Decrement(ref _active);

    /// <summary>当前在飞的代理请求数。</summary>
    public static int ActiveCount => Volatile.Read(ref _active);

    /// <summary>最近一次代理请求进入的时刻（UTC）；进程启动后从未有流量时为 ticks=0。</summary>
    public static DateTimeOffset LastActivityAt => new(Volatile.Read(ref _lastActivityTicks), TimeSpan.Zero);
}
