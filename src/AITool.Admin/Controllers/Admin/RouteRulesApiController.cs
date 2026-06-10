using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 路由规则辅助接口，提供 Conversations 页面所需的路由入口下拉数据。
/// 与 AITool.Web 中的 RouteRulesApiController 不同，这里直接从数据库查询，不依赖运行时缓存。
/// </summary>
[ApiController]
[Route("api/admin/route-rules")]
public sealed class RouteRulesApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public RouteRulesApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取所有路由主入口名称，用于 Conversations 页面的"路由入口"下拉筛选。
    /// 合并 ProxyRouteEntries 表和 ProxyRouteRules 表中的 ExternalModelName 去重后返回。
    /// </summary>
    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(CancellationToken cancellationToken)
    {
        // 从 ProxyRouteRules 中按 ExternalModelName 分组统计候选数
        var candidateCounts = await _dbContext.ProxyRouteRules
            .AsNoTracking()
            .GroupBy(x => x.ExternalModelName)
            .Select(g => new { EntryName = g.Key, CandidateCount = g.Count() })
            .ToListAsync(cancellationToken);

        // 从 ProxyRouteEntries 中获取已注册的主入口名称
        var storedEntries = await _dbContext.ProxyRouteEntries
            .AsNoTracking()
            .OrderBy(x => x.EntryName)
            .Select(x => x.EntryName)
            .ToListAsync(cancellationToken);

        var countsByName = candidateCounts.ToDictionary(x => x.EntryName, x => x.CandidateCount, StringComparer.Ordinal);

        // 合并两个来源并去重，保持与 Core 宿主中 ProxyRequestMetadataCache.GetRouteEntriesAsync 相同的合并逻辑
        var result = storedEntries
            .Concat(candidateCounts.Select(x => x.EntryName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(entryName => new
            {
                entryName,
                candidateCount = countsByName.GetValueOrDefault(entryName, 0)
            })
            .ToList();

        return Ok(result);
    }
}
