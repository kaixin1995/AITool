namespace AITool.Domain.SiteCatalog;

// 站点与模型库的映射关系，记录每个站点支持的模型
public sealed class SiteModelMapping
{
    // 映射主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 站点标识
    public Guid SiteId { get; set; }

    // 模型库标识
    public Guid ModelLibraryItemId { get; set; }

    // 站点原始模型名，可能与统一模型名不同
    public string RemoteModelName { get; set; } = string.Empty;

    // 最近一次拉取状态
    public string LastStatus { get; set; } = "unknown";
}
