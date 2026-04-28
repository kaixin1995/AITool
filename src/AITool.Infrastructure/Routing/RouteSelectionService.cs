using AITool.Application.Routing;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Routing;

// 基于数据库查询的路由选择服务，按优先级筛选最优路由
public sealed class RouteSelectionService : IRouteSelectionService
{
    private readonly AppDbContext _dbContext;

    public RouteSelectionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 查询所有匹配外部模型名称的启用路由，返回优先级最高的那条
    public async Task<RouteSelectionResult> SelectRouteAsync(
        string externalModelName,
        CancellationToken cancellationToken = default)
    {
        return await SelectRouteAsync(externalModelName, [], cancellationToken);
    }

    // 查询匹配的启用路由，排除指定站点集合（用于熔断跳过）
    public async Task<RouteSelectionResult> SelectRouteAsync(
        string externalModelName,
        HashSet<Guid> excludedSiteIds,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ProxyRouteRules
            .Where(r => r.ExternalModelName == externalModelName && r.IsEnabled);

        if (excludedSiteIds.Count > 0)
        {
            query = query.Where(r => !excludedSiteIds.Contains(r.SiteId));
        }

        var route = await query
            .OrderBy(r => r.ModelPriority)
            .ThenBy(r => r.InstancePriority)
            .ThenBy(r => r.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        return new RouteSelectionResult { Route = route };
    }

    // 获取指定模型名称的所有启用路由，按优先级升序排列
    public async Task<List<RouteSelectionResult>> SelectAllRoutesAsync(
        string externalModelName,
        CancellationToken cancellationToken = default)
    {
        var routes = await _dbContext.ProxyRouteRules
            .Where(r => r.ExternalModelName == externalModelName && r.IsEnabled)
            .OrderBy(r => r.ModelPriority)
            .ThenBy(r => r.InstancePriority)
            .ThenBy(r => r.Priority)
            .ToListAsync(cancellationToken);

        return routes.Select(r => new RouteSelectionResult { Route = r }).ToList();
    }
}
