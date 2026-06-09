namespace AITool.Application.CoreRuntime;

/// <summary>
/// Admin 与 Core 建立控制通道时的握手请求。
/// Admin 会把自己当前理解的配置版本和已确认事件位置带给 Core，供后续做配置比对和补传判断。
/// </summary>
public sealed class CoreAdminHandshakeRequest
{
    /// <summary>
    /// Admin 实例标识。
    /// </summary>
    public string AdminInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Admin 进程启动时间。
    /// </summary>
    public DateTimeOffset AdminStartedAt { get; set; }

    /// <summary>
    /// Admin 当前权威配置版本。
    /// </summary>
    public long CurrentConfigVersion { get; set; }

    /// <summary>
    /// Admin 当前权威配置哈希。
    /// </summary>
    public string CurrentConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Admin 最后一次成功确认的事件序号。
    /// 当前阶段事件流尚未接入，先保留该字段用于协议占位。
    /// </summary>
    public long LastAckedSequenceId { get; set; }
}

/// <summary>
/// Core 握手响应，用于告诉 Admin 当前实际生效的配置与最新状态。
/// </summary>
public sealed class CoreAdminHandshakeResponse
{
    /// <summary>
    /// Core 实例标识。
    /// </summary>
    public string CoreInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Core 进程启动时间。
    /// </summary>
    public DateTimeOffset CoreStartedAt { get; set; }

    /// <summary>
    /// Core 当前已应用的配置版本。
    /// </summary>
    public long AppliedConfigVersion { get; set; }

    /// <summary>
    /// Core 当前已应用的配置哈希。
    /// </summary>
    public string AppliedConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Core 是否已具备可服务配置。
    /// </summary>
    public bool Ready { get; set; }

    /// <summary>
    /// Core 当前最新事件序号。
    /// 当前阶段事件流尚未接入，先固定为 0。
    /// </summary>
    public long LatestSequenceId { get; set; }

    /// <summary>
    /// Core 当前活跃请求数。
    /// 当前阶段尚未接入活跃请求跟踪，先固定为 0。
    /// </summary>
    public int ActiveRequestCount { get; set; }

    /// <summary>
    /// Core 当前是否已具备配置积压。
    /// </summary>
    public bool HasSpoolBacklog { get; set; }

    /// <summary>
    /// 配置对齐决策。
    /// </summary>
    public string ConfigSyncDecision { get; set; } = string.Empty;
}
