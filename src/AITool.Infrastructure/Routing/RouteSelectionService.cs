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
        var route = await _dbContext.ProxyRouteRules
            .Where(r => r.ExternalModelName == externalModelName && r.IsEnabled)
            .OrderBy(r => r.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        return new RouteSelectionResult { Route = route };
    }
}
