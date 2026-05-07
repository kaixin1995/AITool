using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

public sealed class UsageLogListQueryDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string RangeType { get; set; } = "day";
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public Guid? SiteId { get; set; }
}

public sealed class UsageLogListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<UsageLogListItemDto> Items { get; set; } = [];
}

public sealed class UsageLogListItemDto
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public int AttemptIndex { get; set; }
    public bool IsFinalResult { get; set; }
    public bool FallbackTriggered { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsStreamInterrupted { get; set; }
    public int FirstTokenLatencyMs { get; set; }
    public int StreamDurationMs { get; set; }
    public int TotalDurationMs { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
}

public sealed class UsageLogAttemptDto
{
    public Guid Id { get; set; }
    public int AttemptIndex { get; set; }
    public string AttemptedModel { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsFinalResult { get; set; }
    public bool FallbackTriggered { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsStreamInterrupted { get; set; }
    public int FirstTokenLatencyMs { get; set; }
    public int StreamDurationMs { get; set; }
    public int TotalDurationMs { get; set; }
    public string ReasoningEffort { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
}

public sealed class UsageLogRequestDetailDto
{
    public Guid RequestId { get; set; }
    public string RequestModel { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public List<UsageLogAttemptDto> Attempts { get; set; } = [];
}

public sealed class UsageLogSummaryDto
{
    public int TotalRequests { get; set; }
    public int FailedRequests { get; set; }
    public double SuccessRate { get; set; }
    public int TotalTokens { get; set; }
    public int MaxDurationMs { get; set; }
}

// 使用日志查询 API，提供列表、请求详情和汇总统计接口
[ApiController]
[Route("api/admin/usage-logs")]
public sealed class UsageLogsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public UsageLogsApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("list")]
    public async Task<ActionResult<UsageLogListResponseDto>> GetList([FromQuery] UsageLogListQueryDto query, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(x => x.IsEnabled)
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // 先加载到内存再按时间过滤和排序，避免 SQLite 无法翻译 DateTimeOffset 比较与排序
        var filteredLogs = (await _dbContext.ProxyUsageLogs
                .ToListAsync(cancellationToken))
            .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
            .Where(x => !query.SiteId.HasValue || x.TargetSiteId == query.SiteId.Value)
            .Where(x => sites.ContainsKey(x.TargetSiteId))
            .OrderByDescending(x => x.RequestedAt)
            .ToList();

        var totalCount = filteredLogs.Count;
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
            TotalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize),
            Items = items
        });
    }

    [HttpGet("request-detail/{requestId:guid}")]
    public async Task<ActionResult<UsageLogRequestDetailDto>> GetRequestDetail(Guid requestId, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(x => x.IsEnabled)
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var logs = await _dbContext.ProxyUsageLogs
            .Where(x => x.RequestId == requestId)
            .ToListAsync(cancellationToken);

        logs = logs.Where(x => sites.ContainsKey(x.TargetSiteId)).ToList();

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
            ProtocolType = orderedLogs[0].ProtocolType,
            Attempts = orderedLogs
                .Select(x => new UsageLogAttemptDto
                {
                    Id = x.Id,
                    AttemptIndex = x.AttemptIndex,
                    AttemptedModel = x.AttemptedModel,
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

    [HttpGet("summary")]
    public async Task<ActionResult<UsageLogSummaryDto>> GetSummary([FromQuery] UsageLogListQueryDto query, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(x => x.IsEnabled)
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);

        // 先加载到内存再按时间过滤，避免 SQLite 无法翻译 DateTimeOffset 比较
        var logs = (await _dbContext.ProxyUsageLogs.ToListAsync(cancellationToken))
            .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
            .Where(x => !query.SiteId.HasValue || x.TargetSiteId == query.SiteId.Value)
            .Where(x => sites.ContainsKey(x.TargetSiteId))
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

    // 根据预设范围或指定起止时间生成过滤区间。
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

    // 使用路由规则反查站点实际命中的站点模型名，便于前端展示尝试链路。
    private static string ResolveSiteModelName(IEnumerable<AITool.Domain.Proxy.ProxyRouteRule> routeRules, Guid siteId, string attemptedModel)
    {
        return routeRules
            .Where(x => x.SiteId == siteId && x.UpstreamModelName == attemptedModel)
            .OrderBy(x => x.Priority)
            .Select(x => x.SiteModelName)
            .FirstOrDefault() ?? string.Empty;
    }
}
