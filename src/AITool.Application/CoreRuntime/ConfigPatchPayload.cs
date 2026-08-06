namespace AITool.Application.CoreRuntime;

/// <summary>
/// 增量配置同步载荷。
/// 与 <see cref="CoreRuntimeConfigSnapshot"/> 的全量模式不同，Patch 只携带发生变化类别的完整列表，
/// Core 端收到后仅替换对应集合，未携带的集合保持不变。
/// <para>
/// 支持的实体类别：<c>Sites</c>、<c>SiteKeys</c>、<c>Models</c>、<c>SiteModelMappings</c>、
/// <c>RouteEntries</c>、<c>RouteRules</c>、<c>AccessKeys</c>、<c>RuntimeSettings</c>。
/// 每个 Patch 至少携带一个类别，否则 Core 端会拒绝。
/// </para>
/// </summary>
public sealed class ConfigPatchPayload
{
    /// <summary>
    /// 配置版本号，Admin 端每次同步（无论全量或增量）单调递增。
    /// </summary>
    public long ConfigVersion { get; set; }

    /// <summary>
    /// Patch 内容哈希，仅对本次携带的实体数据计算。
    /// Core 端利用此哈希做去重：如果 Patch 中携带的类别数据和当前快照完全一致，则忽略。
    /// </summary>
    public string PatchHash { get; set; } = string.Empty;

    /// <summary>
    /// 本次 Patch 涉及的实体类别名称列表。
    /// 例如 ["Sites", "AccessKeys"] 表示只替换站点和密钥数据。
    /// Core 端据此决定失效哪些缓存区域。
    /// </summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// 变更后的站点完整列表，未携带时为 null。
    /// </summary>
    public List<CoreRuntimeSite>? Sites { get; set; }

    /// <summary>
    /// 变更后的站点密钥完整列表（多 Key），未携带时为 null。
    /// </summary>
    public List<CoreRuntimeSiteKey>? SiteKeys { get; set; }

    /// <summary>
    /// 变更后的模型完整列表，未携带时为 null。
    /// </summary>
    public List<CoreRuntimeModel>? Models { get; set; }

    /// <summary>
    /// 变更后的站点模型映射完整列表，未携带时为 null。
    /// </summary>
    public List<CoreRuntimeSiteModelMapping>? SiteModelMappings { get; set; }

    /// <summary>
    /// 变更后的路由主入口完整列表，未携带时为 null。
    /// </summary>
    public List<CoreRuntimeRouteEntry>? RouteEntries { get; set; }

    /// <summary>
    /// 变更后的路由规则完整列表，未携带时为 null。
    /// </summary>
    public List<CoreRuntimeRouteRule>? RouteRules { get; set; }

    /// <summary>
    /// 变更后的访问密钥完整列表，未携带时为 null。
    /// </summary>
    public List<CoreRuntimeAccessKey>? AccessKeys { get; set; }

    /// <summary>
    /// 变更后的运行时设置，未携带时为 null。
    /// </summary>
    public CoreRuntimeSettings? RuntimeSettings { get; set; }
}

/// <summary>
/// 增量 Patch 同步结果。
/// </summary>
public sealed class CorePatchSyncResult
{
    /// <summary>
    /// 是否应用了配置变更。
    /// </summary>
    public bool Applied { get; set; }

    /// <summary>
    /// 是否因为 Patch 数据与当前快照一致而被忽略。
    /// </summary>
    public bool Ignored { get; set; }

    /// <summary>
    /// 当前配置版本。
    /// </summary>
    public long ConfigVersion { get; set; }

    /// <summary>
    /// 当前配置的完整哈希（全量快照哈希，非 Patch 哈希）。
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;
}
