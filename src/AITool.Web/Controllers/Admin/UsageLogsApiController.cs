using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 前端查询用量日志列表时的请求参数，支持分页、时间范围、站点、来源和状态筛选。
/// </summary>
public sealed class UsageLogListQueryDto
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
    /// 开始时间。
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }
    /// <summary>
    /// 结束时间。
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid? SiteId { get; set; }
    /// <summary>
    /// 来源标识。
    /// </summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>
    /// 状态筛选。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 模型搜索关键字。
    /// </summary>
    public string ModelKeyword { get; set; } = string.Empty;
}

/// <summary>
/// 用量日志分页列表响应，包含分页信息和当前页的日志条目列表。
/// </summary>
public sealed class UsageLogListResponseDto
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
    /// 列表项。
    /// </summary>
    public List<UsageLogListItemDto> Items { get; set; } = [];
}

/// <summary>
/// 单条用量日志在列表中的展示项，包含请求、模型、站点、Token 和耗时等信息。
/// </summary>
public sealed class UsageLogListItemDto
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
    /// 尝试调用的模型名称。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 请求状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 来源标识。
    /// </summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
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
    /// 是否流式返回。
    /// </summary>
    public bool IsStreaming { get; set; }
    /// <summary>
    /// 流是否中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }
    /// <summary>
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 流式耗时（毫秒）。
    /// </summary>
    public int StreamDurationMs { get; set; }
    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
    /// <summary>
    /// 请求时间。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; }
}

/// <summary>
/// 单次尝试的详情项，用于请求明细中展示每一轮路由尝试的结果和指标。
/// </summary>
public sealed class UsageLogAttemptDto
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
    /// 尝试调用的模型名称。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 调用模式，例如 direct 或 bridge。
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
    /// 请求状态。
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
    /// 是否流式返回。
    /// </summary>
    public bool IsStreaming { get; set; }
    /// <summary>
    /// 流是否中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }
    /// <summary>
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 流式耗时（毫秒）。
    /// </summary>
    public int StreamDurationMs { get; set; }
    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
    /// <summary>
    /// 思考强度。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;
    /// <summary>
    /// 请求时间。
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; }
}

/// <summary>
/// 请求明细响应，包含请求的基本信息和所有尝试的详细列表。
/// </summary>
public sealed class UsageLogRequestDetailDto
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
    /// 调用方式。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;
    /// <summary>
    /// 思考等级。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;
    /// <summary>
    /// 尝试明细。
    /// </summary>
    public List<UsageLogAttemptDto> Attempts { get; set; } = [];
}

/// <summary>
/// 用量统计摘要，包含请求总数、成功率、Token 总量和最大耗时。
/// </summary>
public sealed class UsageLogSummaryDto
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
    /// Token 总数。
    /// </summary>
    public int TotalTokens { get; set; }
    /// <summary>
    /// 最大耗时（毫秒）。
    /// </summary>
    public int MaxDurationMs { get; set; }
}

/// <summary>
/// 用量日志管理控制器，提供日志分页查询、请求明细和统计摘要。
/// </summary>
[ApiController]
[Route("api/admin/usage-logs")]
public sealed class UsageLogsApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 创建用量日志控制器。
    /// </summary>
    public UsageLogsApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取用量日志列表。
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<UsageLogListResponseDto>> GetList([FromQuery] UsageLogListQueryDto query, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // 先加载到内存再按时间过滤和排序，避免 SQLite 无法翻译 DateTimeOffset 比较与排序
        var filteredLogs = (await _dbContext.ProxyUsageLogs
                .ToListAsync(cancellationToken))
            .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
            .Where(x => !query.SiteId.HasValue || x.TargetSiteId == query.SiteId.Value)
            .Where(x => string.IsNullOrWhiteSpace(query.Source) || string.Equals(x.Source, query.Source, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(query.Status) || string.Equals(x.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            .Where(x => IsModelMatched(x, query.ModelKeyword))
            .OrderByDescending(x => x.RequestedAt)
            .ToList();

        var totalCount = filteredLogs.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = totalPages == 0 ? 1 : Math.Min(Math.Max(1, query.Page), totalPages);
        var items = filteredLogs
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new UsageLogListItemDto
            {
                Id = x.Id,
                RequestId = x.RequestId,
                ProtocolType = x.ProtocolType,
                RequestModel = x.RequestModel,
                AttemptedModel = x.AttemptedModel,
                SiteModelName = ResolveSiteModelName(routeRules, x.TargetSiteId, x.AttemptedModel),
                Status = x.Status,
                Source = x.Source,
                SiteName = sites.TryGetValue(x.TargetSiteId, out var siteName) ? siteName : "-",
                RetryCount = x.RetryCount,
                AttemptIndex = x.AttemptIndex,
                IsFinalResult = x.IsFinalResult,
                FallbackTriggered = x.FallbackTriggered,
                InputTokens = x.InputTokens,
                CachedTokens = x.CachedTokens,
                OutputTokens = x.OutputTokens,
                TotalTokens = x.TotalTokens,
                IsStreaming = x.IsStreaming,
                IsStreamInterrupted = x.IsStreamInterrupted,
                FirstTokenLatencyMs = x.FirstTokenLatencyMs,
                StreamDurationMs = x.StreamDurationMs,
                TotalDurationMs = x.TotalDurationMs,
                RequestedAt = x.RequestedAt
            })
            .ToList();

        return Ok(new UsageLogListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = items
        });
    }

    /// <summary>
    /// 获取请求明细。
    /// </summary>
    [HttpGet("request-detail/{requestId:guid}")]
    public async Task<ActionResult<UsageLogRequestDetailDto>> GetRequestDetail(Guid requestId, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var logs = await _dbContext.ProxyUsageLogs
            .Where(x => x.RequestId == requestId)
            .ToListAsync(cancellationToken);

        if (logs.Count == 0)
        {
            return NotFound();
        }

        var orderedLogs = logs
            .OrderBy(x => x.AttemptIndex)
            .ThenBy(x => x.RequestedAt)
            .ToList();

        var detail = new UsageLogRequestDetailDto
        {
            RequestId = requestId,
            RequestModel = orderedLogs[0].RequestModel,
            RouteEntry = orderedLogs[0].RequestModel,
            ProtocolType = orderedLogs[0].ProtocolType,
            ForwardingMode = orderedLogs.Select(x => x.ForwardingMode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            ReasoningEffort = orderedLogs.Select(x => x.ReasoningEffort).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            Attempts = orderedLogs
                .Select(x => new UsageLogAttemptDto
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
                })
                .ToList()
        };

        return Ok(detail);
    }

    /// <summary>
    /// 获取用量统计摘要。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<UsageLogSummaryDto>> GetSummary([FromQuery] UsageLogListQueryDto query, CancellationToken cancellationToken)
    {
        try
        {
            var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);

            // 先加载到内存再按时间过滤，避免 SQLite 无法翻译 DateTimeOffset 比较
            var logs = (await _dbContext.ProxyUsageLogs.ToListAsync(cancellationToken))
                .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
                .Where(x => !query.SiteId.HasValue || x.TargetSiteId == query.SiteId.Value)
                .Where(x => string.IsNullOrWhiteSpace(query.Source) || string.Equals(x.Source, query.Source, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(query.Status) || string.Equals(x.Status, query.Status, StringComparison.OrdinalIgnoreCase))
                .Where(x => IsModelMatched(x, query.ModelKeyword))
                .ToList();

            // 按 RequestId 分组，每组取最后一条记录作为该请求的最终状态
            var finalLogs = logs
                .GroupBy(x => x.RequestId)
                .Select(g => g.OrderByDescending(x => x.AttemptIndex).First())
                .ToList();

            var totalRequests = finalLogs.Count;
            var successRequests = finalLogs.Count(x => string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase));
            var failedRequests = totalRequests - successRequests;
            var successRate = totalRequests == 0
                ? 0d
                : Math.Round(successRequests * 100d / totalRequests, 2, MidpointRounding.AwayFromZero);
            var totalTokens = finalLogs.Sum(x => x.TotalTokens);
            var maxDurationMs = finalLogs.Count == 0
                ? 0
                : finalLogs.Max(x => x.TotalDurationMs);

            return Ok(new UsageLogSummaryDto
            {
                TotalRequests = totalRequests,
                FailedRequests = failedRequests,
                SuccessRate = successRate,
                TotalTokens = totalTokens,
                MaxDurationMs = maxDurationMs
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 请求已取消时直接结束，避免把前端主动中止记成服务异常。
            return new EmptyResult();
        }
    }

    /// <summary>
    /// 解析查询时间范围。
    /// </summary>
    private static (DateTimeOffset StartTime, DateTimeOffset EndTime) ResolveTimeRange(string? rangeType, DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        var now = DateTimeOffset.Now;
        var normalized = string.IsNullOrWhiteSpace(rangeType) ? "day" : rangeType.Trim().ToLowerInvariant();

        if (normalized == "custom")
        {
            var customStart = startTime ?? now.Date;
            var customEnd = endTime ?? now;
            if (customEnd <= customStart)
            {
                customEnd = customStart.AddDays(1);
            }

            return (customStart, customEnd);
        }

        return normalized switch
        {
            "week" => (now.Date.AddDays(-(int)now.DayOfWeek), now),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), now),
            "all" => (DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            _ => (now.Date, now)
        };
    }

    /// <summary>
    /// 判断当前记录是否命中模型搜索关键字。
    /// </summary>
    private static bool IsModelMatched(AITool.Domain.Proxy.ProxyUsageLog log, string? modelKeyword)
    {
        if (string.IsNullOrWhiteSpace(modelKeyword))
        {
            return true;
        }

        var keyword = modelKeyword.Trim();
        return ContainsIgnoreCase(log.AttemptedModel, keyword)
            || ContainsIgnoreCase(log.RequestModel, keyword);
    }

    /// <summary>
    /// 按大小写不敏感方式判断文本是否包含关键字。
    /// </summary>
    private static bool ContainsIgnoreCase(string? source, string keyword)
    {
        return !string.IsNullOrWhiteSpace(source)
            && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析站点模型名称。
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
