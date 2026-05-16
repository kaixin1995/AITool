using AITool.Domain.Sites;

namespace AITool.Application.SiteCatalog;

/// <summary>
/// 站点目录客户端接口，用于从上游站点读取其当前支持的模型列表。
/// </summary>
public interface ISiteCatalogClient
{
    /// <summary>
    /// 拉取指定站点可用的模型名称集合，供目录同步或能力展示使用。
    /// </summary>
    Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken);
}
