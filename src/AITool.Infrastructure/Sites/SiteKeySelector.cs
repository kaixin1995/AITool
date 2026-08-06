using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Sites;

/// <summary>
/// 站点密钥选择器，供站点级、非路由的操作（模型目录拉取、健康检测）取用一个可用密钥。
/// <para>
/// 转发链路在缓存层按 Key 展开多候选，走的是另一套逻辑；本选择器只服务于
/// "站点级单次调用"场景：取该站点优先级最高（Priority 最小）的启用 Key，
/// 若站点没有任何启用的 SiteKey（Codex 托管站点或未迁移），则回退到 <see cref="Site.ApiKey"/>。
/// </para>
/// </summary>
public sealed class SiteKeySelector
{
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 注入数据库上下文。
    /// </summary>
    public SiteKeySelector(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 取指定站点的活动密钥（优先级最高、已启用）。
    /// 站点没有启用的 SiteKey 时回退到 Site.ApiKey；若站点也不存在则返回空字符串。
    /// </summary>
    public async Task<string> GetActiveKeyAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var primaryKeys = await _dbContext.SiteKeys

            .Where(k => k.SiteId == siteId && k.IsEnabled)
            .OrderBy(k => k.Priority)
            .ThenBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .Select(k => k.KeyValue)
            .ToListAsync(cancellationToken);

        if (primaryKeys.Count > 0 && !string.IsNullOrEmpty(primaryKeys[0]))
        {
            return primaryKeys[0];
        }

        // 回退：站点没有启用的 SiteKey（Codex 托管站点 / 未迁移），用 Site.ApiKey
        var siteApiKeys = await _dbContext.Sites

            .Where(s => s.Id == siteId)
            .Select(s => s.ApiKey)
            .ToListAsync(cancellationToken);

        return siteApiKeys.Count > 0 ? (siteApiKeys[0] ?? string.Empty) : string.Empty;
    }
}
