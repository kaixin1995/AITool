using AITool.Domain.Sites;

namespace AITool.Application.SiteCatalog;

// 站点目录客户端接口，用于拉取站点支持的模型列表
public interface ISiteCatalogClient
{
    // 拉取指定站点支持的模型名列表
    Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken);
}
