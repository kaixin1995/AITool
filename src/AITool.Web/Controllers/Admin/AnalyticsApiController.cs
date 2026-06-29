using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 前端查询统计数据时的请求参数，包含时间范围、分桶粒度和筛选条件。
/// </summary>
public sealed class AnalyticsQueryDto
{
    /// <summary>
    /// 时间范围类型。
    /// </summary>
    public string RangeType { get; set; } = "week";
    /// <summary>
    /// 统计分桶类型。
    /// </summary>
    public string BucketType { get; set; } = "auto";
    /// <summary>
    /// 开始时间。
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }
    /// <summary>
    /// 结束时间。
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = "all";
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = "all";
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid? SiteId { get; set; }
    /// <summary>
    /// 访问密钥标识。
    /// </summary>
    public Guid? AccessKeyId { get; set; }
}

/// <summary>
/// 统计筛选下拉选项，包含可用的站点和模型列表。
/// </summary>
public sealed class AnalyticsFilterOptionsDto
{
    /// <summary>
    /// 站点筛选项。
    /// </summary>
    public List<AnalyticsSiteOptionDto> Sites { get; set; } = [];
    /// <summary>
    /// 模型筛选项。
    /// </summary>
    public List<AnalyticsModelOptionDto> Models { get; set; } = [];
    /// <summary>
    /// 访问密钥筛选项。
    /// </summary>
    public List<AnalyticsAccessKeyOptionDto> AccessKeys { get; set; } = [];
}

/// <summary>
/// 站点筛选下拉项，用于统计页的站点选择器。
/// </summary>
public sealed class AnalyticsSiteOptionDto
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
}

/// <summary>
/// 模型筛选下拉项，用于统计页的模型选择器。
/// </summary>
public sealed class AnalyticsModelOptionDto
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
}

/// <summary>
/// 访问密钥筛选下拉项，用于统计页的密钥选择器。
/// </summary>
public sealed class AnalyticsAccessKeyOptionDto
{
    /// <summary>
    /// 访问密钥标识。
    /// </summary>
    public Guid AccessKeyId { get; set; }
    /// <summary>
    /// 访问密钥名称。
    /// </summary>
    public string AccessKeyLabel { get; set; } = string.Empty;
}

/// <summary>
/// 统计看板完整响应，包含筛选条件、汇总指标和各维度趋势图表数据。
/// </summary>
public sealed class AnalyticsDashboardResponseDto
{
    /// <summary>
    /// 默认筛选条件。
    /// </summary>
    public AnalyticsAppliedFilterDto AppliedFilter { get; set; } = new();
    /// <summary>
    /// 汇总统计数据。
    /// </summary>
    public AnalyticsSummaryDto Summary { get; set; } = new();
    /// <summary>
    /// 请求趋势数据。
    /// </summary>
    public List<AnalyticsTrendPointDto> RequestTrend { get; set; } = [];
    /// <summary>
    /// 结果趋势数据。
    /// </summary>
    public List<AnalyticsResultTrendPointDto> ResultTrend { get; set; } = [];
    /// <summary>
    /// Token 趋势数据。
    /// </summary>
    public List<AnalyticsTokenTrendPointDto> TokenTrend { get; set; } = [];
    /// <summary>
    /// 耗时趋势数据。
    /// </summary>
    public List<AnalyticsDurationTrendPointDto> DurationTrend { get; set; } = [];
    /// <summary>
    /// 回退趋势数据。
    /// </summary>
    public List<AnalyticsFallbackTrendPointDto> FallbackTrend { get; set; } = [];
    /// <summary>
    /// 站点分布数据。
    /// </summary>
    public List<AnalyticsDistributionPointDto> SiteDistribution { get; set; } = [];
    /// <summary>
    /// 模型分布数据。
    /// </summary>
    public List<AnalyticsDistributionPointDto> ModelDistribution { get; set; } = [];
    /// <summary>
    /// 模型缓存命中分布数据。
    /// </summary>
    public List<AnalyticsCacheRatioPointDto> ModelCacheRatioDistribution { get; set; } = [];
}

/// <summary>
/// 本次统计实际生效的筛选条件快照，随看板数据一并返回给前端。
/// </summary>
public sealed class AnalyticsAppliedFilterDto
{
    /// <summary>
    /// 开始时间。
    /// </summary>
    public DateTimeOffset StartTime { get; set; }
    /// <summary>
    /// 结束时间。
    /// </summary>
    public DateTimeOffset EndTime { get; set; }
    /// <summary>
    /// 时间范围类型。
    /// </summary>
    public string RangeType { get; set; } = string.Empty;
    /// <summary>
    /// 统计分桶类型。
    /// </summary>
    public string BucketType { get; set; } = string.Empty;
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid? SiteId { get; set; }
    /// <summary>
    /// 访问密钥标识。
    /// </summary>
    public Guid? AccessKeyId { get; set; }
}

/// <summary>
/// 统计汇总指标，包含请求总数、成功率、Token 用量和耗时均值。
/// </summary>
public sealed class AnalyticsSummaryDto
{
    /// <summary>
    /// 请求总数。
    /// </summary>
    public int TotalRequests { get; set; }
    /// <summary>
    /// 成功请求数。
    /// </summary>
    public int SuccessRequests { get; set; }
    /// <summary>
    /// 失败请求数。
    /// </summary>
    public int FailedRequests { get; set; }
    /// <summary>
    /// 成功率。
    /// </summary>
    public double SuccessRate { get; set; }
    /// <summary>
    /// 失败率。
    /// </summary>
    public double FailureRate { get; set; }
    /// <summary>
    /// 输入 Token 总数。
    /// </summary>
    public int TotalInputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 总数。
    /// </summary>
    public int TotalCachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 总数。
    /// </summary>
    public int TotalOutputTokens { get; set; }
    /// <summary>
    /// Token 总数。
    /// </summary>
    public int TotalTokens { get; set; }
    /// <summary>
    /// 平均总耗时（毫秒）。
    /// </summary>
    public double AverageTotalDurationMs { get; set; }
    /// <summary>
    /// 平均首 Token 延迟（毫秒）。
    /// </summary>
    public double AverageFirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 触发回退的请求数。
    /// </summary>
    public int FallbackRequestCount { get; set; }
}

/// <summary>
/// 请求趋势图中的一个时间桶，包含该时段的请求数。
/// </summary>
public sealed class AnalyticsTrendPointDto
{
    /// <summary>
    /// 时间桶的显示标签，如 "2024-01-01" 或 "01-01 08:00"。
    /// </summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>
    /// 请求数。
    /// </summary>
    public int RequestCount { get; set; }
}

/// <summary>
/// 成功/失败结果趋势图中的一个时间桶，包含该时段的成功与失败数量及比率。
/// </summary>
public sealed class AnalyticsResultTrendPointDto
{
    /// <summary>
    /// 时间桶的显示标签。
    /// </summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>
    /// 成功数。
    /// </summary>
    public int SuccessCount { get; set; }
    /// <summary>
    /// 失败数。
    /// </summary>
    public int FailCount { get; set; }
    /// <summary>
    /// 成功率。
    /// </summary>
    public double SuccessRate { get; set; }
    /// <summary>
    /// 失败率。
    /// </summary>
    public double FailureRate { get; set; }
}

/// <summary>
/// Token 用量趋势图中的一个时间桶，包含输入、缓存、输出和总 Token 数。
/// </summary>
public sealed class AnalyticsTokenTrendPointDto
{
    /// <summary>
    /// 时间桶的显示标签。
    /// </summary>
    public string Label { get; set; } = string.Empty;
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
}

/// <summary>
/// 耗时趋势图中的一个时间桶，包含该时段的平均总耗时和首 Token 延迟。
/// </summary>
public sealed class AnalyticsDurationTrendPointDto
{
    /// <summary>
    /// 时间桶的显示标签。
    /// </summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>
    /// 平均总耗时（毫秒）。
    /// </summary>
    public double AverageTotalDurationMs { get; set; }
    /// <summary>
    /// 平均首 Token 延迟（毫秒）。
    /// </summary>
    public double AverageFirstTokenLatencyMs { get; set; }
}

/// <summary>
/// 回退趋势图中的一个时间桶，包含该时段的回退次数和回退率。
/// </summary>
public sealed class AnalyticsFallbackTrendPointDto
{
    /// <summary>
    /// 时间桶的显示标签。
    /// </summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>
    /// 回退次数。
    /// </summary>
    public int FallbackCount { get; set; }
    /// <summary>
    /// 回退率。
    /// </summary>
    public double FallbackRate { get; set; }
}

/// <summary>
/// 分布统计中的一个维度点，用于站点或模型的请求量/成功率/Token/耗时分布。
/// </summary>
public sealed class AnalyticsDistributionPointDto
{
    /// <summary>
    /// 维度标签，站点分布时为站点名称，模型分布时为模型名称。
    /// </summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>
    /// 请求数。
    /// </summary>
    public int RequestCount { get; set; }
    /// <summary>
    /// 成功数。
    /// </summary>
    public int SuccessCount { get; set; }
    /// <summary>
    /// 失败数。
    /// </summary>
    public int FailedCount { get; set; }
    /// <summary>
    /// Token 总数。
    /// </summary>
    public int TotalTokens { get; set; }
    /// <summary>
    /// 未命中的输入 Token 总数。
    /// </summary>
    public int InputTokens { get; set; }
    /// <summary>
    /// 缓存命中的 Token 总数。
    /// </summary>
    public int CachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 总数。
    /// </summary>
    public int OutputTokens { get; set; }
    /// <summary>
    /// 平均总耗时（毫秒）。
    /// </summary>
    public double AverageTotalDurationMs { get; set; }
}

/// <summary>
/// 模型缓存命中分布中的一个维度点，展示各模型的缓存命中率和相关 Token 统计。
/// </summary>
public sealed class AnalyticsCacheRatioPointDto
{
    /// <summary>
    /// 模型名称，作为分布维度标签。
    /// </summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }
    /// <summary>
    /// 输入统计范围总量。
    /// </summary>
    public int TotalInputScope { get; set; }
    /// <summary>
    /// 缓存命中率。
    /// </summary>
    public double CacheHitRate { get; set; }
}

/// <summary>
/// 统计分析控制器，提供用量统计看板和趋势图表数据。
/// </summary>
[ApiController]
[Route("api/admin/analytics")]
public sealed class AnalyticsApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 服务作用域工厂。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>
    /// 统计后台查询执行器。
    /// </summary>
    private readonly AnalyticsBackgroundQueryExecutor _queryExecutor;
    /// <summary>
    /// 当前宿主环境。
    /// </summary>
    private readonly IHostEnvironment _hostEnvironment;

    /// <summary>
    /// 创建统计分析控制器。
    /// </summary>
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

    /// <summary>
    /// 获取统计筛选项。
    /// </summary>
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
            
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.ModelName)
            .Select(x => new AnalyticsModelOptionDto
            {
                ModelName = x.ModelName
            })
            .ToListAsync(cancellationToken);

        var accessKeys = await _dbContext.ProxyAccessKeys
            
            .OrderBy(x => x.KeyName)
            .Select(x => new AnalyticsAccessKeyOptionDto
            {
                AccessKeyId = x.Id,
                AccessKeyLabel = x.KeyName
            })
            .ToListAsync(cancellationToken);

        return Ok(new AnalyticsFilterOptionsDto
        {
            Sites = sites,
            Models = models,
            AccessKeys = accessKeys
        });
    }

    /// <summary>
    /// 获取统计看板数据。
    /// </summary>
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

    /// <summary>
    /// 构建统计看板返回结果。
    /// </summary>
    private static async Task<AnalyticsDashboardResponseDto> BuildDashboardResponseAsync(
        AppDbContext dbContext,
        AnalyticsQueryDto query,
        CancellationToken cancellationToken)
    {
        var allLogs = await dbContext.ProxyUsageLogs
            
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
            
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);
        var bucketType = ResolveBucketType(query.BucketType, query.RangeType, startTime, endTime);

        // 先按时间和基础筛选收窄范围，再在内存里做聚合：SQLite + EF Core 无法稳定翻译
        // DateTimeOffset 的区间比较（项目内 SystemRuntimeSettingsService、UsageLogsApiController 均因此采用内存过滤）。
        var baseLogs = allLogs
            .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
            .Where(x => string.Equals(query.ProtocolType, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(x.ProtocolType, query.ProtocolType, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(query.ModelName, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(x.AttemptedModel, query.ModelName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 站点筛选按”命中过该站点的尝试”统计，避免回退成功后把失败站点整条请求吞掉。
        var scopedLogs = query.SiteId.HasValue
            ? baseLogs.Where(x => x.TargetSiteId == query.SiteId.Value).ToList()
            : baseLogs;

        // 访问密钥筛选：按该密钥发起的尝试统计。
        if (query.AccessKeyId.HasValue)
        {
            scopedLogs = scopedLogs.Where(x => x.AccessKeyId == query.AccessKeyId.Value).ToList();
        }

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
                SiteId = query.SiteId,
                AccessKeyId = query.AccessKeyId
            },
            Summary = BuildSummary(finalLogs, fallbackRequestIds),
            RequestTrend = BuildRequestTrend(finalLogs, startTime, endTime, bucketType),
            ResultTrend = BuildResultTrend(finalLogs, startTime, endTime, bucketType),
            TokenTrend = BuildTokenTrend(finalLogs, startTime, endTime, bucketType),
            DurationTrend = BuildDurationTrend(finalLogs, startTime, endTime, bucketType),
            FallbackTrend = BuildFallbackTrend(finalLogs, fallbackRequestIds, startTime, endTime, bucketType),
            SiteDistribution = BuildSiteDistribution(finalLogs, siteNames),
            ModelDistribution = BuildModelDistribution(finalLogs),
            ModelCacheRatioDistribution = BuildModelCacheRatioDistribution(finalLogs)
        };
    }

    /// <summary>
    /// 构建汇总统计。
    /// </summary>
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
            // Analytics 页面上的“输入 / 输出 Tokens”需要与“总 Tokens”口径一致，因此输入侧合并缓存命中量。
            TotalInputTokens = finalLogs.Sum(x => x.InputTokens + x.CachedTokens),
            TotalCachedTokens = finalLogs.Sum(x => x.CachedTokens),
            TotalOutputTokens = finalLogs.Sum(x => x.OutputTokens),
            TotalTokens = finalLogs.Sum(x => x.TotalTokens),
            AverageTotalDurationMs = totalRequests == 0 ? 0 : Math.Round(finalLogs.Average(x => x.TotalDurationMs), 2),
            AverageFirstTokenLatencyMs = totalRequests == 0 ? 0 : Math.Round(finalLogs.Average(x => x.FirstTokenLatencyMs), 2),
            FallbackRequestCount = fallbackRequestIds.Count
        };
    }

    /// <summary>
    /// 构建请求趋势。
    /// </summary>
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

    /// <summary>
    /// 构建结果趋势。
    /// </summary>
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

    /// <summary>
    /// 构建 Token 趋势。
    /// </summary>
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

    /// <summary>
    /// 构建耗时趋势。
    /// </summary>
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

    /// <summary>
    /// 构建回退趋势。
    /// </summary>
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

    /// <summary>
    /// 构建站点分布。
    /// </summary>
    private static List<AnalyticsDistributionPointDto> BuildSiteDistribution(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        IReadOnlyDictionary<Guid, string> siteNames)
    {
        return finalLogs
            .GroupBy(x => x.TargetSiteId)
            .Select(g => new AnalyticsDistributionPointDto
            {
                Label = siteNames.TryGetValue(g.Key, out var siteName) ? NormalizeAnalyticsLabel(siteName) : "-",
                RequestCount = g.Count(),
                SuccessCount = g.Count(x => IsSuccess(x.Status)),
                FailedCount = g.Count(x => !IsSuccess(x.Status)),
                TotalTokens = g.Sum(x => x.TotalTokens),
                InputTokens = g.Sum(x => x.InputTokens),
                CachedTokens = g.Sum(x => x.CachedTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                AverageTotalDurationMs = Math.Round(g.Average(x => x.TotalDurationMs), 2)
            })
            .OrderByDescending(x => x.RequestCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 构建模型分布。
    /// </summary>
    private static List<AnalyticsDistributionPointDto> BuildModelDistribution(List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs)
    {
        return finalLogs
            .GroupBy(x => x.AttemptedModel)
            .Select(g => new AnalyticsDistributionPointDto
            {
                Label = NormalizeAnalyticsLabel(g.Key),
                RequestCount = g.Count(),
                SuccessCount = g.Count(x => IsSuccess(x.Status)),
                FailedCount = g.Count(x => !IsSuccess(x.Status)),
                TotalTokens = g.Sum(x => x.TotalTokens),
                InputTokens = g.Sum(x => x.InputTokens),
                CachedTokens = g.Sum(x => x.CachedTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                AverageTotalDurationMs = Math.Round(g.Average(x => x.TotalDurationMs), 2)
            })
            .OrderByDescending(x => x.RequestCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }


    /// <summary>
    /// 构建模型缓存命中分布。
    /// </summary>
    private static List<AnalyticsCacheRatioPointDto> BuildModelCacheRatioDistribution(List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs)
    {
        return finalLogs
            .Where(x => !string.IsNullOrWhiteSpace(x.AttemptedModel))
            .GroupBy(x => x.AttemptedModel)
            .Select(g =>
            {
                var inputTokens = g.Sum(x => x.InputTokens);
                var cachedTokens = g.Sum(x => x.CachedTokens);
                var totalInputScope = inputTokens + cachedTokens;
                return new AnalyticsCacheRatioPointDto
                {
                    Label = NormalizeAnalyticsLabel(g.Key),
                    InputTokens = inputTokens,
                    CachedTokens = cachedTokens,
                    TotalInputScope = totalInputScope,
                    CacheHitRate = totalInputScope <= 0 ? 0 : Math.Round(cachedTokens * 100d / totalInputScope, 2)
                };
            })
            .OrderByDescending(x => x.CacheHitRate)
            .ThenByDescending(x => x.CachedTokens)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// 按分桶类型生成时间区间。
    /// </summary>
    private static string NormalizeAnalyticsLabel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static List<AnalyticsBucket> BuildBuckets(DateTimeOffset startTime, DateTimeOffset endTime, string bucketType)
    {
        var buckets = new List<AnalyticsBucket>();
        var alignedStart = AlignBucketStart(startTime, bucketType);
        // 范围筛选以用户选中的起点为准，避免按月视图因为按周分桶回退到上个月。
        var cursor = alignedStart < startTime ? startTime : alignedStart;

        while (cursor < endTime)
        {
            var next = AddBucket(cursor, bucketType);
            buckets.Add(new AnalyticsBucket
            {
                Start = cursor,
                End = next,
                Label = FormatBucketLabel(cursor, next, bucketType)
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
                Label = FormatBucketLabel(startTime, next, bucketType)
            });
        }

        return buckets;
    }

    /// <summary>
    /// 解析时间范围。
    /// </summary>
    private static (DateTimeOffset StartTime, DateTimeOffset EndTime) ResolveTimeRange(string? rangeType, DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        var now = DateTimeOffset.Now;
        var normalized = string.IsNullOrWhiteSpace(rangeType) ? "week" : rangeType.Trim().ToLowerInvariant();

        if (normalized == "custom")
        {
            var customStart = startTime ?? StartOfDay(now);
            // 前端 datetime-local 目前按分钟输入，筛选时把结束时间扩到下一分钟，避免右开区间把当前分钟内的数据排除掉。
            var customEnd = endTime.HasValue ? endTime.Value.AddMinutes(1) : now;
            if (customEnd <= customStart)
            {
                customEnd = customStart.AddMinutes(1);
            }

            return (customStart, customEnd);
        }

        var endOfToday = StartOfDay(now).AddDays(1);
        var startOfWeek = StartOfDay(now).AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7));

        return normalized switch
        {
            "day" => (StartOfDay(now), endOfToday),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), endOfToday),
            "all" => (DateTimeOffset.MinValue, now),
            _ => (startOfWeek, endOfToday)
        };
    }

    /// <summary>
    /// 解析统计分桶类型。
    /// </summary>
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

        if (range == "custom")
        {
            // 指定时间范围覆盖不超过一天时，自动粒度与“按天”保持一致，避免只生成一个按天桶导致折线图几乎不可见。
            return (endTime - startTime).TotalDays <= 1 ? "hour" : "day";
        }

        return "day";
    }

    /// <summary>
    /// 判断请求是否成功。
    /// </summary>
    private static bool IsSuccess(string status)
    {
        return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 对齐分桶起始时间。
    /// </summary>
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

    /// <summary>
    /// 计算下一个时间桶边界。
    /// </summary>
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

    /// <summary>
    /// 生成时间桶标签。
    /// </summary>
    private static string FormatBucketLabel(DateTimeOffset start, DateTimeOffset end, string bucketType)
    {
        return bucketType switch
        {
            "hour" => $"{start:MM-dd HH}:00",
            // 周桶可能来自按月范围内的截断区间，因此展示实际日期范围更直观。
            "week" => $"{start:MM-dd} ~ {end.AddDays(-1):MM-dd}",
            "month" => $"{start:yyyy-MM}",
            _ => $"{start:yyyy-MM-dd}"
        };
    }

    /// <summary>
    /// 获取当天起始时间。
    /// </summary>
    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }

    /// <summary>
    /// 获取当前小时起始时间。
    /// </summary>
    private static DateTimeOffset StartOfHour(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Offset);
    }

    /// <summary>
    /// 构建统计缓存键。
    /// </summary>
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

    /// <summary>
    /// 计算统计查询等待时长。
    /// </summary>
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

    /// <summary>
    /// 内部时间桶，表示聚合统计用的一个时间区间。
    /// </summary>
    private sealed class AnalyticsBucket
    {
        /// <summary>
        /// 区间开始时间。
        /// </summary>
        public DateTimeOffset Start { get; set; }
        /// <summary>
        /// 区间结束时间。
        /// </summary>
        public DateTimeOffset End { get; set; }
        /// <summary>
        /// 标签。
        /// </summary>
        public string Label { get; set; } = string.Empty;
    }
}
