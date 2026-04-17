namespace AITool.Application.SiteCatalog;

// 站点模型拉取命令，指定要拉取模型的站点
public sealed class PullSiteModelsCommand
{
    // 目标站点标识
    public Guid SiteId { get; }

    public PullSiteModelsCommand(Guid siteId)
    {
        SiteId = siteId;
    }
}

// 站点模型拉取结果
public sealed class PullSiteModelsResult
{
    // 本次导入的模型数量
    public int ImportedCount { get; set; }
}
