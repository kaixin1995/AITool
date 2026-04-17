namespace AITool.Application.Detection;

// 执行模型检测的命令
public sealed class RunDetectionCommand
{
    // 待检测的站点标识
    public Guid SiteId { get; }

    // 待检测的模型库条目标识
    public Guid ModelLibraryItemId { get; }

    public RunDetectionCommand(Guid siteId, Guid modelLibraryItemId)
    {
        SiteId = siteId;
        ModelLibraryItemId = modelLibraryItemId;
    }
}

// 模型检测结果
public sealed class RunDetectionResult
{
    // 检测是否成功
    public bool Success { get; set; }

    // 检测耗时（毫秒）
    public int DurationMs { get; set; }
}
