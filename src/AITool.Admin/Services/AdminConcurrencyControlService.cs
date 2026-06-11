namespace AITool.Admin.Services;

/// <summary>
/// Admin 宿主侧的并发控制门面。
/// 负责在模型配置变更后，将新的并发限制参数同步到 Core 运行时。
/// 当前为占位实现，后续将通过 CoreAdminClient 向 Core 下发并发限制变更。
/// </summary>
public sealed class AdminConcurrencyControlService
{
    /// <summary>
    /// 配置变更后同步新的最大并发数，并尽快唤醒可立即放行的等待请求。
    /// <para>
    /// 当前为空实现，后续通过 CoreAdminClient 向 Core 下发并发限制变更。
    /// </para>
    /// </summary>
    public void UpdateLimit(Guid siteId, string remoteModelName, int maxConcurrency)
    {
        // 空实现，后续通过 CoreAdminClient 代理
    }
}
