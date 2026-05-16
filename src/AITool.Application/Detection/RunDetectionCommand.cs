namespace AITool.Application.Detection;

/// <summary>
/// 执行模型检测的命令，用于描述一次手动或批量检测所需的最小输入参数。
/// </summary>
public sealed class RunDetectionCommand
{
    /// <summary>
    /// 指定需要检测的站点，服务层会据此找到目标配置并发起请求。
    /// </summary>
    public Guid SiteId { get; }

    /// <summary>
    /// 指定本次要检测的模型库条目，用于确定实际探测的模型名称。
    /// </summary>
    public Guid ModelLibraryItemId { get; }

    /// <summary>
    /// 使用站点标识和模型条目标识构造检测命令，避免调用方遗漏必要参数。
    /// </summary>
    public RunDetectionCommand(Guid siteId, Guid modelLibraryItemId)
    {
        SiteId = siteId;
        ModelLibraryItemId = modelLibraryItemId;
    }
}

/// <summary>
/// 模型检测结果，用于向上层返回本次检测是否成功以及耗时情况。
/// </summary>
public sealed class RunDetectionResult
{
    /// <summary>
    /// 标记本次检测是否通过。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 记录本次检测从开始到结束的总耗时，单位为毫秒。
    /// </summary>
    public int DurationMs { get; set; }
}
