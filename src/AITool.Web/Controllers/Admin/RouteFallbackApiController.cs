using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 路由回退监控 API，根据同一请求链路中的多次尝试日志还原回退事件。
/// </summary>
[ApiController]
[Route("api/admin/route-fallback")]
public sealed class RouteFallbackApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public RouteFallbackApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取路由回退事件分页列表。
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<RouteFallbackListResponseDto>> GetList([FromQuery] RouteFallbackQueryDto query, CancellationToken cancellationToken)
    {
        var events = await BuildEventsAsync(cancellationToken);
        var filtered = ApplyFilters(events, query).ToList();
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var totalCount = filtered.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = totalPages == 0 ? 1 : Math.Min(Math.Max(1, query.Page), totalPages);

        return Ok(new RouteFallbackListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = filtered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList()
        });
    }

    /// <summary>
    /// 获取路由回退摘要统计。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<RouteFallbackSummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        var events = await BuildEventsAsync(cancellationToken);
        var list = events.ToList();
        return Ok(new RouteFallbackSummaryDto
        {
            TotalCount = list.Count,
            UniqueFromSites = list.Select(x => x.FromSiteId).Where(x => x != Guid.Empty).Distinct().Count(),
            UniqueToSites = list.Select(x => x.ToSiteId).Where(x => x != Guid.Empty).Distinct().Count(),
            LatestOccurredAt = list.FirstOrDefault()?.OccurredAt
        });
    }

    private async Task<IEnumerable<RouteFallbackEventDto>> BuildEventsAsync(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        // 回退事件只依赖最近的多尝试链路；限制读取量，避免监控页在大日志库上全表扫描。
        var logs = await _dbContext.ProxyUsageLogs
            .OrderBy(x => x.RequestedAt, SqlSugar.OrderByType.Desc)
            .Take(5000)
            .ToListAsync(cancellationToken);

        return logs
            .GroupBy(x => x.RequestId)
            .SelectMany(group => BuildEventsForRequest(group.OrderBy(x => x.AttemptIndex).ThenBy(x => x.RequestedAt).ToList(), sites))
            .OrderByDescending(x => x.OccurredAt)
            .ToList();
    }

    private static IEnumerable<RouteFallbackEventDto> BuildEventsForRequest(IReadOnlyList<ProxyUsageLog> logs, IReadOnlyDictionary<Guid, string> sites)
    {
        if (logs.Count < 2)
        {
            return [];
        }

        var events = new List<RouteFallbackEventDto>();
        for (var i = 0; i < logs.Count - 1; i++)
        {
            var from = logs[i];
            var to = logs[i + 1];
            if (!from.FallbackTriggered && from.Status != "fail" && from.TargetSiteId == to.TargetSiteId && from.AttemptedModel == to.AttemptedModel)
            {
                continue;
            }

            events.Add(new RouteFallbackEventDto
            {
                RequestId = from.RequestId,
                RequestModel = from.RequestModel,
                FromSiteId = from.TargetSiteId,
                FromSiteName = sites.TryGetValue(from.TargetSiteId, out var fromSiteName) ? fromSiteName : "-",
                FromSiteModelName = from.AttemptedModel,
                ToSiteId = to.TargetSiteId,
                ToSiteName = sites.TryGetValue(to.TargetSiteId, out var toSiteName) ? toSiteName : "-",
                ToSiteModelName = to.AttemptedModel,
                Reason = string.IsNullOrWhiteSpace(from.ErrorMessage) ? "上游调用失败，已切换到下一候选站点" : from.ErrorMessage,
                OccurredAt = to.RequestedAt
            });
        }

        return events;
    }

    private static IEnumerable<RouteFallbackEventDto> ApplyFilters(IEnumerable<RouteFallbackEventDto> events, RouteFallbackQueryDto query)
    {
        if (!string.IsNullOrWhiteSpace(query.ModelKeyword))
        {
            events = events.Where(x => Contains(x.RequestModel, query.ModelKeyword)
                || Contains(x.FromSiteModelName, query.ModelKeyword)
                || Contains(x.ToSiteModelName, query.ModelKeyword));
        }

        if (!string.IsNullOrWhiteSpace(query.ReasonKeyword))
        {
            events = events.Where(x => Contains(x.Reason, query.ReasonKeyword));
        }

        return events;
    }

    private static bool Contains(string value, string keyword)
    {
        return value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RouteFallbackQueryDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string ModelKeyword { get; set; } = string.Empty;
    public string ReasonKeyword { get; set; } = string.Empty;
}

public sealed class RouteFallbackListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<RouteFallbackEventDto> Items { get; set; } = [];
}

public sealed class RouteFallbackSummaryDto
{
    public int TotalCount { get; set; }
    public int UniqueFromSites { get; set; }
    public int UniqueToSites { get; set; }
    public DateTimeOffset? LatestOccurredAt { get; set; }
}

public sealed class RouteFallbackEventDto
{
    public Guid RequestId { get; set; }
    public string RequestModel { get; set; } = string.Empty;
    public Guid FromSiteId { get; set; }
    public string FromSiteName { get; set; } = string.Empty;
    public string FromSiteModelName { get; set; } = string.Empty;
    public Guid ToSiteId { get; set; }
    public string ToSiteName { get; set; } = string.Empty;
    public string ToSiteModelName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
