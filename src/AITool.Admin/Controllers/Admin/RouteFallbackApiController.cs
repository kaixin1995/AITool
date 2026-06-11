using AITool.Infrastructure.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 路由回退事件查询请求参数。
/// </summary>
public sealed class RouteFallbackListQueryDto
{
    /// <summary>
    /// 页码。
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// 每页条数。
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// 请求模型关键字筛选。
    /// </summary>
    public string ModelKeyword { get; set; } = string.Empty;

    /// <summary>
    /// 回退原因关键字筛选。
    /// </summary>
    public string ReasonKeyword { get; set; } = string.Empty;
}

/// <summary>
/// 路由回退事件分页列表响应。
/// </summary>
public sealed class RouteFallbackListResponseDto
{
    /// <summary>
    /// 页码。
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// 每页条数。
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// 总记录数。
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// 总页数。
    /// </summary>
    public int TotalPages { get; set; }

    /// <summary>
    /// 当前页数据。
    /// </summary>
    public List<RouteFallbackListItemDto> Items { get; set; } = [];
}

/// <summary>
/// 路由回退事件列表项。
/// </summary>
public sealed class RouteFallbackListItemDto
{
    /// <summary>
    /// 关联的代理请求标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 请求模型名。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 回退源站点模型名。
    /// </summary>
    public string FromSiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 回退源站点标识。
    /// </summary>
    public Guid FromSiteId { get; set; }

    /// <summary>
    /// 回退目标站点模型名。
    /// </summary>
    public string ToSiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 回退目标站点标识。
    /// </summary>
    public Guid ToSiteId { get; set; }

    /// <summary>
    /// 触发回退的原因。
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// 回退发生时间。
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// 路由回退事件摘要统计。
/// </summary>
public sealed class RouteFallbackSummaryDto
{
    /// <summary>
    /// 当前记录总数。
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// 回退涉及的唯一源站点数。
    /// </summary>
    public int UniqueFromSites { get; set; }

    /// <summary>
    /// 回退涉及的唯一目标站点数。
    /// </summary>
    public int UniqueToSites { get; set; }

    /// <summary>
    /// 最近一条回退的发生时间。
    /// </summary>
    public DateTimeOffset? LatestOccurredAt { get; set; }
}

/// <summary>
/// 路由回退事件监控接口。
/// 数据来源是 AdminRouteFallbackStore 内存存储，由 CoreEventPullService 定期从 Core 宿主拉取事件后写入。
/// 不涉及数据库查询，适合实时诊断路由健康状态。
/// </summary>
[ApiController]
[Route("api/admin/route-fallback")]
public sealed class RouteFallbackApiController : ControllerBase
{
    /// <summary>
    /// 路由回退事件内存存储。
    /// </summary>
    private readonly AdminRouteFallbackStore _store;

    /// <summary>
    /// 初始化路由回退事件监控接口。
    /// </summary>
    public RouteFallbackApiController(AdminRouteFallbackStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 获取路由回退事件列表。
    /// 支持按模型名和回退原因筛选，按发生时间倒序分页展示。
    /// </summary>
    [HttpGet("list")]
    public ActionResult<RouteFallbackListResponseDto> GetList([FromQuery] RouteFallbackListQueryDto query)
    {
        var allEvents = _store.List();

        // 按模型关键字和回退原因关键字过滤
        var filtered = allEvents
            .Where(x => string.IsNullOrWhiteSpace(query.ModelKeyword)
                || x.RequestModel.Contains(query.ModelKeyword, StringComparison.OrdinalIgnoreCase)
                || x.FromSiteModelName.Contains(query.ModelKeyword, StringComparison.OrdinalIgnoreCase)
                || x.ToSiteModelName.Contains(query.ModelKeyword, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(query.ReasonKeyword)
                || x.Reason.Contains(query.ReasonKeyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var totalCount = filtered.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = totalPages == 0 ? 1 : Math.Min(Math.Max(1, query.Page), totalPages);

        var items = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new RouteFallbackListItemDto
            {
                RequestId = x.RequestId,
                RequestModel = x.RequestModel,
                FromSiteModelName = x.FromSiteModelName,
                FromSiteId = x.FromSiteId,
                ToSiteModelName = x.ToSiteModelName,
                ToSiteId = x.ToSiteId,
                Reason = x.Reason,
                OccurredAt = x.OccurredAt
            })
            .ToList();

        return Ok(new RouteFallbackListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = items
        });
    }

    /// <summary>
    /// 获取路由回退事件摘要统计。
    /// 页面顶部摘要卡片使用，快速了解回退事件的规模和范围。
    /// </summary>
    [HttpGet("summary")]
    public ActionResult<RouteFallbackSummaryDto> GetSummary()
    {
        var (totalCount, uniqueFromSites, uniqueToSites) = _store.GetSummary();

        // 获取最新一条回退的时间
        var latest = _store.List().FirstOrDefault();

        return Ok(new RouteFallbackSummaryDto
        {
            TotalCount = totalCount,
            UniqueFromSites = uniqueFromSites,
            UniqueToSites = uniqueToSites,
            LatestOccurredAt = latest?.OccurredAt
        });
    }
}
