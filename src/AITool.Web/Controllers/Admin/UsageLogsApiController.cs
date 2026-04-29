using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

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
    public int FirstTokenLatencyMs { get; set; }
    public int StreamDurationMs { get; set; }
    public int TotalDurationMs { get; set; }
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
    public async Task<ActionResult<List<UsageLogListItemDto>>> GetList([FromQuery] Guid? siteId, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites.ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var routeRules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var logs = await _dbContext.ProxyUsageLogs
            .Where(x => !siteId.HasValue || x.TargetSiteId == siteId.Value)
            .ToListAsync(cancellationToken);

        var items = logs
            .OrderByDescending(x => x.RequestedAt)
            .Take(200)
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
                FirstTokenLatencyMs = x.FirstTokenLatencyMs,
                StreamDurationMs = x.StreamDurationMs,
                TotalDurationMs = x.TotalDurationMs,
                RequestedAt = x.RequestedAt
            })
            .ToList();

        return Ok(items);
    }

    [HttpGet("request-detail/{requestId:guid}")]
    public async Task<ActionResult<UsageLogRequestDetailDto>> GetRequestDetail(Guid requestId, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites.ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
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
                    FirstTokenLatencyMs = x.FirstTokenLatencyMs,
                    StreamDurationMs = x.StreamDurationMs,
                    TotalDurationMs = x.TotalDurationMs,
                    RequestedAt = x.RequestedAt
                })
                .ToList()
        };

        return Ok(detail);
    }

    [HttpGet("summary")]
    public async Task<ActionResult<UsageLogSummaryDto>> GetSummary([FromQuery] Guid? siteId, CancellationToken cancellationToken)
    {
        var allLogs = await _dbContext.ProxyUsageLogs.ToListAsync(cancellationToken);
        var logs = allLogs
            .Where(x => !siteId.HasValue || x.TargetSiteId == siteId.Value)
            .ToList();
        var finalLogs = logs.Where(x => x.IsFinalResult).ToList();
        var requestIds = finalLogs.Select(x => x.RequestId).Distinct().ToHashSet();
        var requestStartLookup = allLogs
            .Where(x => requestIds.Contains(x.RequestId))
            .GroupBy(x => x.RequestId)
            .ToDictionary(g => g.Key, g => g.Min(x => x.RequestedAt));

        var totalRequests = finalLogs.Count;
        var successRequests = finalLogs.Count(x => string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase));
        var failedRequests = finalLogs.Count - successRequests;
        var successRate = totalRequests == 0
            ? 0d
            : Math.Round(successRequests * 100d / totalRequests, 2, MidpointRounding.AwayFromZero);
        var totalTokens = finalLogs.Sum(x => x.TotalTokens);
        var maxDurationMs = finalLogs.Count == 0
            ? 0
            : finalLogs.Max(x => (int)Math.Max(0, (x.RequestedAt - requestStartLookup[x.RequestId]).TotalMilliseconds));

        return Ok(new UsageLogSummaryDto
        {
            TotalRequests = totalRequests,
            FailedRequests = failedRequests,
            SuccessRate = successRate,
            TotalTokens = totalTokens,
            MaxDurationMs = maxDurationMs
        });
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
