using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Admin.Controllers.Admin;

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
    /// 当前阶段先提供页面最低可用所需的数据筛选和分页能力。
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> GetList(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? rangeType = null,
        [FromQuery] DateTimeOffset? startTime = null,
        [FromQuery] DateTimeOffset? endTime = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string? source = null,
        [FromQuery] string? status = null,
        [FromQuery] string? modelKeyword = null,
        CancellationToken cancellationToken = default)
    {
        var (rangeStart, rangeEnd) = ResolveTimeRange(rangeType, startTime, endTime);
        var allLogs = await _dbContext.ProxyUsageLogs
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var filtered = allLogs
            .Where(x => x.RequestedAt >= rangeStart && x.RequestedAt < rangeEnd)
            .Where(x => !siteId.HasValue || x.TargetSiteId == siteId.Value)
            .Where(x => string.IsNullOrWhiteSpace(source) || string.Equals(x.Source, source, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(status) || string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(modelKeyword)
                || (!string.IsNullOrWhiteSpace(x.RequestModel) && x.RequestModel.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(x.AttemptedModel) && x.AttemptedModel.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.RequestedAt)
            .ToList();

        var sites = await _dbContext.Sites
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var totalCount = filtered.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)Math.Max(1, pageSize));
        var normalizedPage = totalPages == 0 ? 1 : Math.Min(Math.Max(1, page), totalPages);
        var items = filtered
            .Skip((normalizedPage - 1) * Math.Max(1, pageSize))
            .Take(Math.Max(1, pageSize))
            .Select(x => new
            {
                x.Id,
                x.RequestId,
                x.ProtocolType,
                x.RequestModel,
                x.AttemptedModel,
                SiteName = sites.TryGetValue(x.TargetSiteId, out var siteName) ? siteName : "-",
                x.Status,
                x.Source,
                x.InputTokens,
                x.CachedTokens,
                x.OutputTokens,
                x.TotalTokens,
                x.IsStreaming,
                x.IsStreamInterrupted,
                x.FirstTokenLatencyMs,
                x.StreamDurationMs,
                x.TotalDurationMs,
                x.RequestedAt
            })
            .ToList();

        return Ok(new
        {
            page = normalizedPage,
            pageSize = Math.Max(1, pageSize),
            totalCount,
            totalPages,
            items
        });
    }

    /// <summary>
    /// 获取调用日志汇总信息。
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? rangeType = null,
        [FromQuery] DateTimeOffset? startTime = null,
        [FromQuery] DateTimeOffset? endTime = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string? source = null,
        [FromQuery] string? status = null,
        [FromQuery] string? modelKeyword = null,
        CancellationToken cancellationToken = default)
    {
        var (rangeStart, rangeEnd) = ResolveTimeRange(rangeType, startTime, endTime);
        var logs = await _dbContext.ProxyUsageLogs
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var filtered = logs
            .Where(x => x.RequestedAt >= rangeStart && x.RequestedAt < rangeEnd)
            .Where(x => !siteId.HasValue || x.TargetSiteId == siteId.Value)
            .Where(x => string.IsNullOrWhiteSpace(source) || string.Equals(x.Source, source, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(status) || string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(modelKeyword)
                || (!string.IsNullOrWhiteSpace(x.RequestModel) && x.RequestModel.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(x.AttemptedModel) && x.AttemptedModel.Contains(modelKeyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var totalRequests = filtered.Count;
        var failedRequests = filtered.Count(x => string.Equals(x.Status, "fail", StringComparison.OrdinalIgnoreCase));
        var successRequests = filtered.Count(x => string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase));
        var successRate = totalRequests == 0
            ? 0d
            : Math.Round(successRequests * 100d / totalRequests, 2, MidpointRounding.AwayFromZero);

        return Ok(new
        {
            totalRequests,
            failedRequests,
            successRate,
            totalTokens = filtered.Sum(x => x.TotalTokens),
            maxDurationMs = filtered.Count == 0 ? 0 : filtered.Max(x => x.TotalDurationMs)
        });
    }

    /// <summary>
    /// 获取指定请求的链路详情。
    /// </summary>
    [HttpGet("request-detail/{requestId:guid}")]
    public async Task<IActionResult> GetRequestDetail(Guid requestId, CancellationToken cancellationToken)
    {
        var logs = await _dbContext.ProxyUsageLogs
            .AsNoTracking()
            .Where(x => x.RequestId == requestId)
            .ToListAsync(cancellationToken);
        if (logs.Count == 0)
        {
            return NotFound(new { message = "请求不存在" });
        }

        logs = logs
            .OrderBy(x => x.AttemptIndex)
            .ThenBy(x => x.RequestedAt)
            .ToList();

        var sites = await _dbContext.Sites
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        return Ok(new
        {
            requestId,
            routeEntry = logs[0].RequestModel,
            protocolType = logs[0].ProtocolType,
            forwardingMode = logs.Select(x => x.ForwardingMode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            reasoningEffort = logs.Select(x => x.ReasoningEffort).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
            attempts = logs.Select(x => new
            {
                x.Id,
                x.AttemptIndex,
                x.AttemptedModel,
                SiteName = sites.TryGetValue(x.TargetSiteId, out var siteName) ? siteName : "-",
                x.Status,
                x.IsFinalResult,
                x.FallbackTriggered,
                x.ErrorMessage,
                x.InputTokens,
                x.CachedTokens,
                x.OutputTokens,
                x.TotalTokens,
                x.IsStreaming,
                x.IsStreamInterrupted,
                x.FirstTokenLatencyMs,
                x.StreamDurationMs,
                x.TotalDurationMs,
                x.ReasoningEffort,
                x.RequestedAt
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

        return normalized switch
        {
            "week" => (new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7)), now),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), now),
            "all" => (DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            _ => (new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), now)
        };
    }
}
