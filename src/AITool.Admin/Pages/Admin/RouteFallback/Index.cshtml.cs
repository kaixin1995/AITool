using AITool.Infrastructure.CoreRuntime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages.Admin.RouteFallback;

/// <summary>
/// 路由回退监控页面模型。
/// 数据来源是 AdminRouteFallbackStore 内存存储（由 CoreEventPullService 从 Core 拉取写入），
/// 不涉及数据库或 CoreAdminClient 代理调用，页面初始化时直接读取摘要统计。
/// </summary>
public sealed class IndexModel : PageModel
{
    /// <summary>
    /// 路由回退事件内存存储。
    /// </summary>
    private readonly AdminRouteFallbackStore _store;

    /// <summary>
    /// 初始化路由回退监控页面模型。
    /// </summary>
    public IndexModel(AdminRouteFallbackStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 初始总记录数，用于页面首次加载时的摘要卡片展示。
    /// </summary>
    public int InitialTotalCount { get; private set; }

    /// <summary>
    /// 初始唯一回退源站点数。
    /// </summary>
    public int InitialUniqueFromSites { get; private set; }

    /// <summary>
    /// 初始唯一回退目标站点数。
    /// </summary>
    public int InitialUniqueToSites { get; private set; }

    /// <summary>
    /// 最新一条回退事件的发生时间。
    /// </summary>
    public DateTimeOffset? InitialLatestOccurredAt { get; private set; }

    /// <summary>
    /// 页面首次加载，获取摘要统计。
    /// </summary>
    public void OnGet()
    {
        var (totalCount, uniqueFromSites, uniqueToSites) = _store.GetSummary();
        InitialTotalCount = totalCount;
        InitialUniqueFromSites = uniqueFromSites;
        InitialUniqueToSites = uniqueToSites;

        // 取最新一条回退的发生时间
        InitialLatestOccurredAt = _store.List().FirstOrDefault()?.OccurredAt;
    }

    /// <summary>
    /// AJAX 获取路由回退事件分页列表。
    /// 前端通过定时轮询调用此接口实现自动刷新。
    /// </summary>
    public JsonResult OnGetListAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string modelKeyword = "", [FromQuery] string reasonKeyword = "")
    {
        var allEvents = _store.List();

        // 按模型关键字和回退原因关键字过滤
        var filtered = allEvents
            .Where(x => string.IsNullOrWhiteSpace(modelKeyword)
                || x.RequestModel.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase)
                || x.FromSiteModelName.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase)
                || x.ToSiteModelName.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(reasonKeyword)
                || x.Reason.Contains(reasonKeyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        pageSize = Math.Clamp(pageSize, 1, 100);
        var totalCount = filtered.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var currentPage = totalPages == 0 ? 1 : Math.Min(Math.Max(1, page), totalPages);

        var items = filtered
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.RequestId,
                x.RequestModel,
                x.FromSiteModelName,
                FromSiteId = x.FromSiteId.ToString(),
                x.ToSiteModelName,
                ToSiteId = x.ToSiteId.ToString(),
                x.Reason,
                OccurredAt = x.OccurredAt.ToString("O")
            })
            .ToList();

        return new JsonResult(new
        {
            Page = currentPage,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = items
        });
    }

    /// <summary>
    /// AJAX 获取摘要统计，供前端定时刷新摘要卡片。
    /// </summary>
    public JsonResult OnGetSummaryAsync()
    {
        var (totalCount, uniqueFromSites, uniqueToSites) = _store.GetSummary();
        var latest = _store.List().FirstOrDefault();

        return new JsonResult(new
        {
            TotalCount = totalCount,
            UniqueFromSites = uniqueFromSites,
            UniqueToSites = uniqueToSites,
            LatestOccurredAt = latest?.OccurredAt.ToString("O")
        });
    }
}
