using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

public sealed class AnalyticsQueryDto
{
    public string RangeType { get; set; } = "week";
    public string BucketType { get; set; } = "auto";
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public string ProtocolType { get; set; } = "all";
    public string ModelName { get; set; } = "all";
    public Guid? SiteId { get; set; }
}

public sealed class AnalyticsFilterOptionsDto
{
    public List<AnalyticsSiteOptionDto> Sites { get; set; } = [];
    public List<AnalyticsModelOptionDto> Models { get; set; } = [];
}

public sealed class AnalyticsSiteOptionDto
{
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
}

public sealed class AnalyticsModelOptionDto
{
    public string ModelName { get; set; } = string.Empty;
}

public sealed class AnalyticsDashboardResponseDto
{
    public AnalyticsAppliedFilterDto AppliedFilter { get; set; } = new();
    public AnalyticsSummaryDto Summary { get; set; } = new();
    public List<AnalyticsTrendPointDto> RequestTrend { get; set; } = [];
    public List<AnalyticsResultTrendPointDto> ResultTrend { get; set; } = [];
    public List<AnalyticsTokenTrendPointDto> TokenTrend { get; set; } = [];
    public List<AnalyticsDurationTrendPointDto> DurationTrend { get; set; } = [];
    public List<AnalyticsFallbackTrendPointDto> FallbackTrend { get; set; } = [];
    public List<AnalyticsDistributionPointDto> SiteDistribution { get; set; } = [];
    public List<AnalyticsDistributionPointDto> ModelDistribution { get; set; } = [];
}

public sealed class AnalyticsAppliedFilterDto
{
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public string RangeType { get; set; } = string.Empty;
    public string BucketType { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public Guid? SiteId { get; set; }
}

public sealed class AnalyticsSummaryDto
{
    public int TotalRequests { get; set; }
    public int SuccessRequests { get; set; }
    public int FailedRequests { get; set; }
    public double SuccessRate { get; set; }
    public double FailureRate { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalCachedTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public double AverageTotalDurationMs { get; set; }
    public double AverageFirstTokenLatencyMs { get; set; }
    public int FallbackRequestCount { get; set; }
}

public sealed class AnalyticsTrendPointDto
{
    public string Label { get; set; } = string.Empty;
    public int RequestCount { get; set; }
}

public sealed class AnalyticsResultTrendPointDto
{
    public string Label { get; set; } = string.Empty;
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public double SuccessRate { get; set; }
    public double FailureRate { get; set; }
}

public sealed class AnalyticsTokenTrendPointDto
{
    public string Label { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
}

public sealed class AnalyticsDurationTrendPointDto
{
    public string Label { get; set; } = string.Empty;
    public double AverageTotalDurationMs { get; set; }
    public double AverageFirstTokenLatencyMs { get; set; }
}

public sealed class AnalyticsFallbackTrendPointDto
{
    public string Label { get; set; } = string.Empty;
    public int FallbackCount { get; set; }
    public double FallbackRate { get; set; }
}

public sealed class AnalyticsDistributionPointDto
{
    public string Label { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int TotalTokens { get; set; }
    public double AverageTotalDurationMs { get; set; }
}

// 可视化分析 API，统一输出图表和汇总统计所需的数据。
[ApiController]
[Route("api/admin/analytics")]
public sealed class AnalyticsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalyticsBackgroundQueryExecutor _queryExecutor;
    private readonly IHostEnvironment _hostEnvironment;

    public AnalyticsApiController(
        AppDbContext dbContext,
        IServiceScopeFactory scopeFactory,
        AnalyticsBackgroundQueryExecutor queryExecutor,
        IHostEnvironment hostEnvironment)
    {
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
        _queryExecutor = queryExecutor;
        _hostEnvironment = hostEnvironment;
    }

    // 返回筛选器所需的站点和模型列表。
    [HttpGet("options")]
    public async Task<ActionResult<AnalyticsFilterOptionsDto>> GetOptions(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .OrderBy(x => x.Name)
            .Select(x => new AnalyticsSiteOptionDto
            {
                SiteId = x.Id,
                SiteName = x.Name
            })
            .ToListAsync(cancellationToken);

        var models = await _dbContext.ModelLibraryItems
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.ModelName)
            .Select(x => new AnalyticsModelOptionDto
            {
                ModelName = x.ModelName
            })
            .ToListAsync(cancellationToken);

        return Ok(new AnalyticsFilterOptionsDto
        {
            Sites = sites,
            Models = models
        });
    }

    // 返回可视化大盘首版所需的全部统计结果。
    [HttpGet("dashboard")]
    public async Task<ActionResult<AnalyticsDashboardResponseDto>> GetDashboard([FromQuery] AnalyticsQueryDto query, CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(query);
        var waitBudget = ResolveWaitBudget(query.RangeType, _hostEnvironment);
        var queued = await _queryExecutor.EnqueueOrGetAsync(
            cacheKey,
            async innerCancellationToken =>
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await BuildDashboardResponseAsync(dbContext, query, innerCancellationToken);
            },
            waitBudget,
            cancellationToken);

        return queued.Status switch
        {
            AnalyticsQueueStatus.Ready when queued.Result is not null => Ok(queued.Result),
            AnalyticsQueueStatus.QueueFull => StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                status = "busy",
                retryAfterMs = 2000,
                message = "统计队列繁忙，请稍后重试"
            }),
            _ => Accepted(new
            {
                status = "pending",
                retryAfterMs = 1200,
                message = "统计任务已进入后台队列"
            })
        };
    }

    // 统计聚合实际在后台长任务线程里执行，并使用独立作用域的 DbContext。
    private static async Task<AnalyticsDashboardResponseDto> BuildDashboardResponseAsync(
        AppDbContext dbContext,
        AnalyticsQueryDto query,
        CancellationToken cancellationToken)
    {
        var allLogs = await dbContext.ProxyUsageLogs
            .AsNoTracking()
            .Select(x => new AITool.Domain.Proxy.ProxyUsageLog
            {
                Id = x.Id,
                RequestId = x.RequestId,
                AccessKeyId = x.AccessKeyId,
                ProtocolType = x.ProtocolType,
                RequestModel = x.RequestModel,
                AttemptedModel = x.AttemptedModel,
                TargetSiteId = x.TargetSiteId,
                Status = x.Status,
                Source = x.Source,
                RetryCount = x.RetryCount,
                AttemptIndex = x.AttemptIndex,
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
                RequestedAt = x.RequestedAt
            })
            .ToListAsync(cancellationToken);

        var siteNames = await dbContext.Sites
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);
        var bucketType = ResolveBucketType(query.BucketType, query.RangeType, startTime, endTime);

        // 先按时间和基础筛选收窄范围，再在内存里做聚合，兼容 SQLite 对 DateTimeOffset 的限制。
        var baseLogs = allLogs
            .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
            .Where(x => string.Equals(query.ProtocolType, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(x.ProtocolType, query.ProtocolType, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(query.ModelName, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(x.AttemptedModel, query.ModelName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 站点筛选按“命中过该站点的尝试”统计，避免回退成功后把失败站点整条请求吞掉。
        var scopedLogs = query.SiteId.HasValue
            ? baseLogs.Where(x => x.TargetSiteId == query.SiteId.Value).ToList()
            : baseLogs;

        var finalLogs = scopedLogs
            .GroupBy(x => x.RequestId)
            .Select(g => g
                .OrderByDescending(x => x.AttemptIndex)
                .ThenByDescending(x => x.RequestedAt)
                .First())
            .ToList();

        var fallbackRequestIds = scopedLogs
            .GroupBy(x => x.RequestId)
            .Where(g => g.Any(x => x.FallbackTriggered) || g.Max(x => x.AttemptIndex) > 1)
            .Select(g => g.Key)
            .ToHashSet();

        return new AnalyticsDashboardResponseDto
        {
            AppliedFilter = new AnalyticsAppliedFilterDto
            {
                StartTime = startTime,
                EndTime = endTime,
                RangeType = string.IsNullOrWhiteSpace(query.RangeType) ? "week" : query.RangeType,
                BucketType = bucketType,
                ProtocolType = string.IsNullOrWhiteSpace(query.ProtocolType) ? "all" : query.ProtocolType,
                ModelName = string.IsNullOrWhiteSpace(query.ModelName) ? "all" : query.ModelName,
                SiteId = query.SiteId
            },
            Summary = BuildSummary(finalLogs, fallbackRequestIds),
            RequestTrend = BuildRequestTrend(finalLogs, startTime, endTime, bucketType),
            ResultTrend = BuildResultTrend(finalLogs, startTime, endTime, bucketType),
            TokenTrend = BuildTokenTrend(finalLogs, startTime, endTime, bucketType),
            DurationTrend = BuildDurationTrend(finalLogs, startTime, endTime, bucketType),
            FallbackTrend = BuildFallbackTrend(finalLogs, fallbackRequestIds, startTime, endTime, bucketType),
            SiteDistribution = BuildSiteDistribution(finalLogs, siteNames),
            ModelDistribution = BuildModelDistribution(finalLogs)
        };
    }

    // 汇总卡片基于请求最终结果统计，避免多次尝试重复计数。
    private static AnalyticsSummaryDto BuildSummary(List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs, HashSet<Guid> fallbackRequestIds)
    {
        var totalRequests = finalLogs.Count;
        var successRequests = finalLogs.Count(x => IsSuccess(x.Status));
        var failedRequests = totalRequests - successRequests;

        return new AnalyticsSummaryDto
        {
            TotalRequests = totalRequests,
            SuccessRequests = successRequests,
            FailedRequests = failedRequests,
            SuccessRate = totalRequests == 0 ? 0 : Math.Round(successRequests * 100d / totalRequests, 2),
            FailureRate = totalRequests == 0 ? 0 : Math.Round(failedRequests * 100d / totalRequests, 2),
            TotalInputTokens = finalLogs.Sum(x => x.InputTokens),
            TotalCachedTokens = finalLogs.Sum(x => x.CachedTokens),
            TotalOutputTokens = finalLogs.Sum(x => x.OutputTokens),
            TotalTokens = finalLogs.Sum(x => x.TotalTokens),
            AverageTotalDurationMs = totalRequests == 0 ? 0 : Math.Round(finalLogs.Average(x => x.TotalDurationMs), 2),
            AverageFirstTokenLatencyMs = totalRequests == 0 ? 0 : Math.Round(finalLogs.Average(x => x.FirstTokenLatencyMs), 2),
            FallbackRequestCount = fallbackRequestIds.Count
        };
    }

    // 请求趋势图展示每个时间桶内的请求总量。
    private static List<AnalyticsTrendPointDto> BuildRequestTrend(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string bucketType)
    {
        return BuildBuckets(startTime, endTime, bucketType)
            .Select(bucket => new AnalyticsTrendPointDto
            {
                Label = bucket.Label,
                RequestCount = finalLogs.Count(x => x.RequestedAt >= bucket.Start && x.RequestedAt < bucket.End)
            })
            .ToList();
    }

    // 成功失败趋势图同时输出数量和比率，方便前端切换展示方式。
    private static List<AnalyticsResultTrendPointDto> BuildResultTrend(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string bucketType)
    {
        return BuildBuckets(startTime, endTime, bucketType)
            .Select(bucket =>
            {
                var bucketLogs = finalLogs
                    .Where(x => x.RequestedAt >= bucket.Start && x.RequestedAt < bucket.End)
                    .ToList();
                var total = bucketLogs.Count;
                var success = bucketLogs.Count(x => IsSuccess(x.Status));
                var fail = total - success;

                return new AnalyticsResultTrendPointDto
                {
                    Label = bucket.Label,
                    SuccessCount = success,
                    FailCount = fail,
                    SuccessRate = total == 0 ? 0 : Math.Round(success * 100d / total, 2),
                    FailureRate = total == 0 ? 0 : Math.Round(fail * 100d / total, 2)
                };
            })
            .ToList();
    }

    // Token 用量趋势图拆分输入、缓存、输出和总量，便于后续扩展堆叠图。
    private static List<AnalyticsTokenTrendPointDto> BuildTokenTrend(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string bucketType)
    {
        return BuildBuckets(startTime, endTime, bucketType)
            .Select(bucket =>
            {
                var bucketLogs = finalLogs
                    .Where(x => x.RequestedAt >= bucket.Start && x.RequestedAt < bucket.End)
                    .ToList();

                return new AnalyticsTokenTrendPointDto
                {
                    Label = bucket.Label,
                    InputTokens = bucketLogs.Sum(x => x.InputTokens),
                    CachedTokens = bucketLogs.Sum(x => x.CachedTokens),
                    OutputTokens = bucketLogs.Sum(x => x.OutputTokens),
                    TotalTokens = bucketLogs.Sum(x => x.TotalTokens)
                };
            })
            .ToList();
    }

    // 耗时趋势图同时输出总耗时和首字耗时的平均值。
    private static List<AnalyticsDurationTrendPointDto> BuildDurationTrend(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string bucketType)
    {
        return BuildBuckets(startTime, endTime, bucketType)
            .Select(bucket =>
            {
                var bucketLogs = finalLogs
                    .Where(x => x.RequestedAt >= bucket.Start && x.RequestedAt < bucket.End)
                    .ToList();

                return new AnalyticsDurationTrendPointDto
                {
                    Label = bucket.Label,
                    AverageTotalDurationMs = bucketLogs.Count == 0 ? 0 : Math.Round(bucketLogs.Average(x => x.TotalDurationMs), 2),
                    AverageFirstTokenLatencyMs = bucketLogs.Count == 0 ? 0 : Math.Round(bucketLogs.Average(x => x.FirstTokenLatencyMs), 2)
                };
            })
            .ToList();
    }

    // Fallback 趋势图用于观察回退链路是否频繁触发。
    private static List<AnalyticsFallbackTrendPointDto> BuildFallbackTrend(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        HashSet<Guid> fallbackRequestIds,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string bucketType)
    {
        return BuildBuckets(startTime, endTime, bucketType)
            .Select(bucket =>
            {
                var bucketLogs = finalLogs
                    .Where(x => x.RequestedAt >= bucket.Start && x.RequestedAt < bucket.End)
                    .ToList();
                var total = bucketLogs.Count;
                var fallbackCount = bucketLogs.Count(x => fallbackRequestIds.Contains(x.RequestId));

                return new AnalyticsFallbackTrendPointDto
                {
                    Label = bucket.Label,
                    FallbackCount = fallbackCount,
                    FallbackRate = total == 0 ? 0 : Math.Round(fallbackCount * 100d / total, 2)
                };
            })
            .ToList();
    }

    // 站点分布图展示各站点的请求量、成功失败和平均耗时。
    private static List<AnalyticsDistributionPointDto> BuildSiteDistribution(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        IReadOnlyDictionary<Guid, string> siteNames)
    {
        return finalLogs
            .GroupBy(x => x.TargetSiteId)
            .Select(g => new AnalyticsDistributionPointDto
            {
                Label = siteNames.TryGetValue(g.Key, out var siteName) ? siteName : g.Key.ToString(),
                RequestCount = g.Count(),
                SuccessCount = g.Count(x => IsSuccess(x.Status)),
                FailedCount = g.Count(x => !IsSuccess(x.Status)),
                TotalTokens = g.Sum(x => x.TotalTokens),
                AverageTotalDurationMs = Math.Round(g.Average(x => x.TotalDurationMs), 2)
            })
            .OrderByDescending(x => x.RequestCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 模型分布图展示各真实模型的调用热度和用量。
    private static List<AnalyticsDistributionPointDto> BuildModelDistribution(List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs)
    {
        return finalLogs
            .GroupBy(x => x.AttemptedModel)
            .Select(g => new AnalyticsDistributionPointDto
            {
                Label = g.Key,
                RequestCount = g.Count(),
                SuccessCount = g.Count(x => IsSuccess(x.Status)),
                FailedCount = g.Count(x => !IsSuccess(x.Status)),
                TotalTokens = g.Sum(x => x.TotalTokens),
                AverageTotalDurationMs = Math.Round(g.Average(x => x.TotalDurationMs), 2)
            })
            .OrderByDescending(x => x.RequestCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    // 统一生成时间桶，避免前后端对时间边界理解不一致。
    private static List<AnalyticsBucket> BuildBuckets(DateTimeOffset startTime, DateTimeOffset endTime, string bucketType)
    {
        var buckets = new List<AnalyticsBucket>();
        var cursor = AlignBucketStart(startTime, bucketType);

        while (cursor < endTime)
        {
            var next = AddBucket(cursor, bucketType);
            buckets.Add(new AnalyticsBucket
            {
                Start = cursor,
                End = next,
                Label = FormatBucketLabel(cursor, bucketType)
            });
            cursor = next;
        }

        if (buckets.Count == 0)
        {
            var next = AddBucket(AlignBucketStart(startTime, bucketType), bucketType);
            buckets.Add(new AnalyticsBucket
            {
                Start = startTime,
                End = next,
                Label = FormatBucketLabel(startTime, bucketType)
            });
        }

        return buckets;
    }

    private static (DateTimeOffset StartTime, DateTimeOffset EndTime) ResolveTimeRange(string? rangeType, DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        var now = DateTimeOffset.Now;
        var normalized = string.IsNullOrWhiteSpace(rangeType) ? "week" : rangeType.Trim().ToLowerInvariant();

        if (normalized == "custom")
        {
            var customStart = startTime ?? StartOfDay(now);
            var customEnd = endTime.HasValue
                ? StartOfDay(endTime.Value).AddDays(1)
                : now;
            if (customEnd < customStart)
            {
                customEnd = customStart.AddDays(1);
            }

            return (customStart, customEnd);
        }

        return normalized switch
        {
            "day" => (StartOfDay(now), now),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), now),
            "all" => (DateTimeOffset.MinValue, now),
            _ => (StartOfDay(now).AddDays(-6), now)
        };
    }

    private static string ResolveBucketType(string? bucketType, string? rangeType, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var normalized = string.IsNullOrWhiteSpace(bucketType) ? "auto" : bucketType.Trim().ToLowerInvariant();
        if (normalized is "hour" or "day" or "week" or "month")
        {
            return normalized;
        }

        var range = string.IsNullOrWhiteSpace(rangeType) ? "week" : rangeType.Trim().ToLowerInvariant();
        if (range == "day")
        {
            return "hour";
        }

        if (range == "month")
        {
            return "week";
        }

        if (range == "all")
        {
            var totalDays = (endTime - startTime).TotalDays;
            return totalDays > 120 ? "month" : "week";
        }

        return "day";
    }

    private static bool IsSuccess(string status)
    {
        return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset AlignBucketStart(DateTimeOffset value, string bucketType)
    {
        return bucketType switch
        {
            "hour" => StartOfHour(value),
            "week" => StartOfDay(value).AddDays(-((7 + (int)value.DayOfWeek - (int)DayOfWeek.Monday) % 7)),
            "month" => new DateTimeOffset(new DateTime(value.Year, value.Month, 1), value.Offset),
            _ => StartOfDay(value)
        };
    }

    private static DateTimeOffset AddBucket(DateTimeOffset value, string bucketType)
    {
        return bucketType switch
        {
            "hour" => value.AddHours(1),
            "week" => value.AddDays(7),
            "month" => value.AddMonths(1),
            _ => value.AddDays(1)
        };
    }

    private static string FormatBucketLabel(DateTimeOffset value, string bucketType)
    {
        return bucketType switch
        {
            "hour" => $"{value:MM-dd HH}:00",
            "week" => $"{value:yyyy-MM-dd} 周",
            "month" => $"{value:yyyy-MM}",
            _ => $"{value:yyyy-MM-dd}"
        };
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }

    private static DateTimeOffset StartOfHour(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Offset);
    }

    // 缓存键按筛选参数收敛，降低重复统计开销。
    private static string BuildCacheKey(AnalyticsQueryDto query)
    {
        return string.Join('|',
            query.RangeType ?? "week",
            query.BucketType ?? "auto",
            query.StartTime?.ToString("O") ?? "-",
            query.EndTime?.ToString("O") ?? "-",
            query.ProtocolType ?? "all",
            query.ModelName ?? "all",
            query.SiteId?.ToString() ?? "-");
    }

    // 全量范围默认不在请求线程等待，普通范围也只给极短预算。
    private static TimeSpan ResolveWaitBudget(string? rangeType, IHostEnvironment hostEnvironment)
    {
        if (hostEnvironment.IsEnvironment("Testing"))
        {
            return TimeSpan.FromSeconds(5);
        }

        var normalized = string.IsNullOrWhiteSpace(rangeType) ? "week" : rangeType.Trim().ToLowerInvariant();
        return normalized == "all"
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(120);
    }

    private sealed class AnalyticsBucket
    {
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
        public string Label { get; set; } = string.Empty;
    }
}
