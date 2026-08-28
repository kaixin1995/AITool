using AITool.Domain.Sites;

namespace AITool.Application.Proxy;

/// <summary>
/// 请求头模板方案本地文件目录服务契约（直接读写 client-header-profiles.json，脱离数据库存储）。
/// </summary>
public interface IHeaderProfileCatalogService
{
    /// <summary>
    /// 获取全部请求头方案列表（含系统内置与自定义）。
    /// </summary>
    Task<IReadOnlyList<HeaderProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 ID 获取方案。
    /// </summary>
    Task<HeaderProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 Key 获取方案。
    /// </summary>
    Task<HeaderProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取全部已启用方案的 [Key -> HeadersJson] 映射字典。
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetActiveProfilesDictionaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建自定义方案。
    /// </summary>
    Task<HeaderProfile> CreateAsync(HeaderProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新方案。
    /// </summary>
    Task<HeaderProfile?> UpdateAsync(Guid id, Action<HeaderProfile> updateAction, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除自定义方案（内置方案不可删除）。
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重置系统内置预设为官方默认值。
    /// </summary>
    Task<IReadOnlyList<HeaderProfile>> ResetBuiltInsAsync(CancellationToken cancellationToken = default);
}
