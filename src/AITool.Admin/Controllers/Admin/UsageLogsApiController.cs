using AITool.Admin.Services;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 前端查询调用日志列表时使用的请求参数。
/// 当前阶段先对齐 UsageLogs 页面已接入的筛选项，确保独立 Admin 宿主中的页面和只读接口使用同一套查询语义。
/// </summary>
public sealed class AdminUsageLogListQueryDto
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
    /// 时间范围类型。
    /// </summary>
    public string RangeType { get; set; } = "day";

    /// <summary>
    /// 自定义开始时间。
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// 自定义结束时间。
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid? SiteId { get; set; }

    /// <summary>
    /// 来源筛选。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 状态筛选。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 模型关键字。
    /// </summary>
    public string ModelKeyword { get; set; } = string.Empty;

    /// <summary>
    /// 访问密钥标识筛选。
    /// </summary>
    public Guid? AccessKeyId { get; set; }
}

/// <summary>
/// 调用日志分页列表响应。
/// </summary>
public sealed class AdminUsageLogListResponseDto
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
    public List<AdminUsageLogListItemDto> Items { get; set; } = [];
}

/// <summary>
/// 调用日志列表项。
/// </summary>
public sealed class AdminUsageLogListItemDto
{
    /// <summary>
    /// 记录标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 请求标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 请求模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 实际尝试的模型名称。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 来源。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 重试次数。
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 尝试序号。
    /// </summary>
    public int AttemptIndex { get; set; }

    /// <summary>
    /// 是否为最终结果。
    /// </summary>
    public bool IsFinalResult { get; set; }

    /// <summary>
    /// 是否触发回退。
    /// </summary>
    public bool FallbackTriggered { get; set; }

    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// Token 总数。
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// 是否为流式返回。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 流是否被中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>
    /// 首字延迟。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>
    /// 流式持续时间。
    /// </summary>
    public int StreamDurationMs { get; set; }

    /// <summary>
    /// 总耗时。
    /// </summary>
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// 请求时间。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>
    /// 访问密钥名称。
    /// </summary>
    public string AccessKeyName { get; set; } = string.Empty;
}

/// <summary>
/// 单次调用尝试详情。
/// </summary>
public sealed class AdminUsageLogAttemptDto
{
    /// <summary>
    /// 记录标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 尝试序号。
    /// </summary>
    public int AttemptIndex { get; set; }

    /// <summary>
    /// 实际尝试的模型名称。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;

    /// <summary>
    /// 调用模式。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 是否为最终结果。
    /// </summary>
    public bool IsFinalResult { get; set; }

    /// <summary>
    /// 是否触发回退。
    /// </summary>
    public bool FallbackTriggered { get; set; }

    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// Token 总数。
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// 是否为流式返回。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 流是否被中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }

    /// <summary>
    /// 首字延迟。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }

    /// <summary>
    /// 流式持续时间。
    /// </summary>
    public int StreamDurationMs { get; set; }

    /// <summary>
    /// 总耗时。
    /// </summary>
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// 思考等级。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 请求时间。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; }
}

/// <summary>
/// 请求链路详情响应。
/// </summary>
public sealed class AdminUsageLogRequestDetailDto
{
    /// <summary>
    /// 请求标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 请求模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 路由入口名称。
    /// </summary>
    public string RouteEntry { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 调用模式。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;

    /// <summary>
    /// 思考等级。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 所有尝试明细。
    /// </summary>
    public List<AdminUsageLogAttemptDto> Attempts { get; set; } = [];

    /// <summary>
    /// 访问密钥名称。
    /// </summary>
    public string AccessKeyName { get; set; } = string.Empty;
}

/// <summary>
/// 调用日志摘要卡片数据。
/// </summary>
public sealed class AdminUsageLogSummaryDto
{
    /// <summary>
    /// 请求总数。
    /// </summary>
    public int TotalRequests { get; set; }

    /// <summary>
    /// 失败请求数。
    /// </summary>
    public int FailedRequests { get; set; }

    /// <summary>
    /// 成功率。
    /// </summary>
    public double SuccessRate { get; set; }

    /// <summary>
    /// Token 总数（用 long 避免大窗口累计超 Int32.MaxValue 溢出）。
    /// </summary>
    public long TotalTokens { get; set; }

    /// <summary>
    /// 最大耗时。
    /// </summary>
    public long MaxDurationMs { get; set; }
}

/// <summary>
/// Admin 独立宿主中的调用日志查询接口。
/// 当前阶段先把这块只读接口迁过来，配合 UsageLogs 页面完成第一块真实页面的宿主内联动验证。
/// </summary>
[ApiController]
[Route("api/admin/usage-logs")]
public sealed class UsageLogsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化调用日志接口控制器。
    /// </summary>
    public UsageLogsApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取调用日志列表。
    /// 当前阶段对齐独立 Admin 页面已接入的筛选、分页和详情入口所需字段。
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<AdminUsageLogListResponseDto>> GetList([FromQuery] AdminUsageLogListQueryDto query, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules
            
            .ToListAsync(cancellationToken);
        var accessKeyNames = await _dbContext.ProxyAccessKeys
            
            .ToDictionaryAsync(x => x.Id, x => x.KeyName, cancellationToken);
        var (rangeStart, rangeEnd) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // 用 SqlSugar Ado 原生 SQL，时间过滤/排序/分页全部在 SQLite 引擎端完成（julianday() 解析 TEXT 列），
        // 避免全表加载到内存后客户端过滤。
        var (rows, totalCount) = await UsageLogSqlQueries.QueryListAsync(
            _dbContext.Client,
            rangeStart, rangeEnd,
            query.SiteId, query.AccessKeyId, query.Source, query.Status, query.ModelKeyword,
            Math.Max(1, query.Page), pageSize);

        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = totalPages == 0 ? 1 : Math.Min(Math.Max(1, query.Page), totalPages);

        // 若请求页超出范围（原生 SQL 返回空但 totalCount>0），回退到第 1 页重新查询。
        if (totalCount > 0 && rows.Count == 0 && page > 1)
        {
            (rows, _) = await UsageLogSqlQueries.QueryListAsync(
                _dbContext.Client,
                rangeStart, rangeEnd,
                query.SiteId, query.AccessKeyId, query.Source, query.Status, query.ModelKeyword,
                1, pageSize);
            page = 1;
        }

        var items = rows
            .Select(x => new AdminUsageLogListItemDto
            {
                Id = x.Id,
                RequestId = x.RequestId,
                ProtocolType = x.ProtocolType,
                RequestModel = x.RequestModel,
                AttemptedModel = x.AttemptedModel,
                SiteModelName = ResolveSiteModelName(routeRules, x.TargetSiteId, x.AttemptedModel),
                SiteName = sites.TryGetValue(x.TargetSiteId, out var siteName) ? siteName : "-",
                Status = x.Status,
                Source = x.Source,
                RetryCount = x.RetryCount,
                AttemptIndex = x.AttemptIndex,
                IsFinalResult = x.IsFinalResult != 0,
                FallbackTriggered = x.FallbackTriggered != 0,
                InputTokens = x.InputTokens,
                CachedTokens = x.CachedTokens,
                OutputTokens = x.OutputTokens,
                TotalTokens = x.TotalTokens,
                IsStreaming = x.IsStreaming != 0,
                IsStreamInterrupted = x.IsStreamInterrupted != 0,
                FirstTokenLatencyMs = x.FirstTokenLatencyMs,
                StreamDurationMs = x.StreamDurationMs,
                TotalDurationMs = x.TotalDurationMs,
                RequestedAt = new DateTimeOffset(x.RequestedAtDateTime, TimeSpan.Zero),
                AccessKeyName = accessKeyNames.TryGetValue(x.AccessKeyId, out var keyName) ? keyName : "-"
            })
            .ToList();

        return Ok(new AdminUsageLogListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = items
        });
    }

    /// <summary>
    /// 获取调用日志汇总信息。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<AdminUsageLogSummaryDto>> GetSummary([FromQuery] AdminUsageLogListQueryDto query, CancellationToken cancellationToken)
    {
        var (rangeStart, rangeEnd) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);

        // 用 SqlSugar Ado 原生 SQL 聚合，避免全表加载到内存后客户端聚合。
        var row = await UsageLogSqlQueries.QuerySummaryAsync(
            _dbContext.Client,
            rangeStart, rangeEnd,
            query.SiteId, query.AccessKeyId, query.Source, query.Status, query.ModelKeyword);

        var totalRequests = (int)row.TotalRequests;
        var failedRequests = (int)(row.FailedRequests ?? 0);
        var successRequests = (int)(row.SuccessRequests ?? 0);
        var successRate = totalRequests == 0
            ? 0d
            : Math.Round(successRequests * 100d / totalRequests, 2, MidpointRounding.AwayFromZero);

        var totalTokens = row.TotalTokens ?? 0;
        var maxDurationMs = row.MaxDurationMs ?? 0;

        return Ok(new AdminUsageLogSummaryDto
        {
            TotalRequests = totalRequests,
            FailedRequests = failedRequests,
            SuccessRate = successRate,
            TotalTokens = totalTokens,
            MaxDurationMs = maxDurationMs
        });
    }

    /// <summary>
    /// 获取用量日志页的筛选项（站点列表 + 访问密钥列表），供前端下拉筛选器加载。
    /// </summary>
    [HttpGet("filters")]
    public async Task<ActionResult<object>> GetFilters(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .Select(s => new { id = s.Id.ToString(), name = s.Name })
            .ToListAsync(cancellationToken);

        var accessKeys = await _dbContext.ProxyAccessKeys
            .Where(k => k.IsEnabled)
            .OrderBy(k => k.KeyName)
            .Select(k => new { id = k.Id.ToString(), name = k.KeyName })
            .ToListAsync(cancellationToken);

        return Ok(new { sites, accessKeys });
    }

    /// <summary>
    /// 获取指定请求的链路详情。
    /// </summary>
    [HttpGet("request-detail/{requestId:guid}")]
    public async Task<ActionResult<AdminUsageLogRequestDetailDto>> GetRequestDetail(Guid requestId, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var accessKeys = await _dbContext.ProxyAccessKeys
            
            .ToDictionaryAsync(x => x.Id, x => x.KeyName, cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules
            
            .ToListAsync(cancellationToken);
        var logs = await _dbContext.ProxyUsageLogs
            
            .Where(x => x.RequestId == requestId)
            .ToListAsync(cancellationToken);
        if (logs.Count == 0)
        {
            return NotFound(new { message = "请求不存在" });
        }

        // 详情按尝试顺序和发起时间重新排序，保证页面侧打开抽屉后看到的链路与真实重试顺序一致。
        var orderedLogs = logs
            .OrderBy(x => x.AttemptIndex)
            .ThenBy(x => x.RequestedAt)
            .ToList();

        return Ok(new AdminUsageLogRequestDetailDto
        {
            RequestId = requestId,
            RequestModel = orderedLogs[0].RequestModel,
            RouteEntry = orderedLogs[0].RequestModel,
            ProtocolType = orderedLogs[0].ProtocolType,
            ForwardingMode = orderedLogs.Select(x => x.ForwardingMode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            ReasoningEffort = orderedLogs.Select(x => x.ReasoningEffort).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            AccessKeyName = accessKeys.TryGetValue(orderedLogs[0].AccessKeyId, out var keyName) ? keyName : "-",
            Attempts = orderedLogs.Select(x => new AdminUsageLogAttemptDto
            {
                Id = x.Id,
                AttemptIndex = x.AttemptIndex,
                AttemptedModel = x.AttemptedModel,
                ForwardingMode = x.ForwardingMode ?? string.Empty,
                SiteModelName = ResolveSiteModelName(routeRules, x.TargetSiteId, x.AttemptedModel),
                SiteName = sites.TryGetValue(x.TargetSiteId, out var siteName) ? siteName : "-",
                Status = x.Status,
                IsFinalResult = x.IsFinalResult,
                FallbackTriggered = x.FallbackTriggered,
                ErrorMessage = x.ErrorMessage,
                InputTokens = x.InputTokens,
                CachedTokens = x.CachedTokens,
                OutputTokens = x.OutputTokens,
                TotalTokens = x.TotalTokens,
                IsStreaming = x.IsStreaming,
                IsStreamInterrupted = x.IsStreamInterrupted,
                FirstTokenLatencyMs = x.FirstTokenLatencyMs,
                StreamDurationMs = x.StreamDurationMs,
                TotalDurationMs = x.TotalDurationMs,
                ReasoningEffort = x.ReasoningEffort,
                RequestedAt = x.RequestedAt
            }).ToList()
        });
    }

    /// <summary>
    /// 解析时间范围。
    /// </summary>
    private static (DateTimeOffset Start, DateTimeOffset End) ResolveTimeRange(string? rangeType, DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        var now = DateTimeOffset.Now;
        var normalized = string.IsNullOrWhiteSpace(rangeType) ? "day" : rangeType.Trim().ToLowerInvariant();
        if (normalized == "custom")
        {
            var customStart = startTime ?? new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            var customEnd = endTime ?? now;
            if (customEnd <= customStart)
            {
                customEnd = customStart.AddDays(1);
            }

            return (customStart, customEnd);
        }

        // 结束时间统一用"今天结束"（明天0点），与 Analytics 口径一致，
        // 避免不同页面对"当天"的结束时刻定义不同导致统计范围偏差。
        var endOfToday = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(1);
        return normalized switch
        {
            "week" => (new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7)), endOfToday),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), endOfToday),
            "all" => (DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            _ => (new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), endOfToday)
        };
    }

    /// <summary>
    /// 根据站点和上游模型名称解析站点模型名称。
    /// </summary>
    private static string ResolveSiteModelName(IEnumerable<AITool.Domain.Proxy.ProxyRouteRule> routeRules, Guid siteId, string attemptedModel)
    {
        return routeRules
            .Where(x => x.SiteId == siteId && x.UpstreamModelName == attemptedModel)
            .OrderBy(x => x.Priority)
            .Select(x => x.SiteModelName)
            .FirstOrDefault() ?? string.Empty;
    }
}
