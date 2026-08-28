using AITool.Application.UsageLogs;
using AITool.Infrastructure.Persistence;
using AITool.Admin.Services;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

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
    /// 来源标识。
    /// </summary>
    public string? Source { get; set; }
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
    /// <summary>
    /// 来源细分数据。
    /// </summary>
    public List<AnalyticsBreakdownPointDto> SourceBreakdown { get; set; } = [];
    /// <summary>
    /// 访问密钥细分数据。
    /// </summary>
    public List<AnalyticsBreakdownPointDto> AccessKeyBreakdown { get; set; } = [];
    /// <summary>
    /// 协议细分数据。
    /// </summary>
    public List<AnalyticsBreakdownPointDto> ProtocolBreakdown { get; set; } = [];
    /// <summary>
    /// 最终失败请求的错误分类细分数据。
    /// </summary>
    public List<AnalyticsBreakdownPointDto> FailureReasonBreakdown { get; set; } = [];
    /// <summary>
    /// 最终失败请求的 HTTP 状态码细分数据。
    /// </summary>
    public List<AnalyticsBreakdownPointDto> StatusCodeBreakdown { get; set; } = [];
    /// <summary>
    /// 发生回退的请求链路分布数据。
    /// </summary>
    public List<AnalyticsFallbackChainPointDto> FallbackChainDistribution { get; set; } = [];
    /// <summary>
    /// 当前筛选最终请求的延迟百分位数。
    /// </summary>
    public AnalyticsLatencyPercentilesDto LatencyPercentiles { get; set; } = new();
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
    /// 来源标识。
    /// </summary>
    public string? Source { get; set; }
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
    /// 消耗总成本（USD，按本地价格表查询时动态计算；未匹配价格的请求按 0 计）。
    /// </summary>
    public decimal TotalCostUsd { get; set; }
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
    public long TotalInputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 总数。
    /// </summary>
    public long TotalCachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 总数。
    /// </summary>
    public long TotalOutputTokens { get; set; }
    /// <summary>
    /// Token 总数。
    /// </summary>
    public long TotalTokens { get; set; }
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
    public long InputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public long CachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public long OutputTokens { get; set; }
    /// <summary>
    /// Token 总数。
    /// </summary>
    public long TotalTokens { get; set; }
    /// <summary>
    /// 该桶的消耗成本（USD）。未匹配价格的请求按 0 计。
    /// </summary>
    public decimal CostUsd { get; set; }
    /// <summary>
    /// 该桶的输入段成本（USD）。未定价请求按 0 计。
    /// </summary>
    public decimal InputCostUsd { get; set; }
    /// <summary>
    /// 该桶的缓存段成本（USD）。未定价请求按 0 计。
    /// </summary>
    public decimal CachedCostUsd { get; set; }
    /// <summary>
    /// 该桶的输出段成本（USD）。未定价请求按 0 计。
    /// </summary>
    public decimal OutputCostUsd { get; set; }
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
    /// 稳定维度键，站点为 SiteId，模型为 AttemptedModel。
    /// </summary>
    public string Key { get; set; } = string.Empty;
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
    public long TotalTokens { get; set; }
    /// <summary>
    /// 未命中的输入 Token 总数。
    /// </summary>
    public long InputTokens { get; set; }
    /// <summary>
    /// 缓存命中的 Token 总数。
    /// </summary>
    public long CachedTokens { get; set; }
    /// <summary>
    /// 输出 Token 总数。
    /// </summary>
    public long OutputTokens { get; set; }
    /// <summary>
    /// 平均总耗时（毫秒）。
    /// </summary>
    public double AverageTotalDurationMs { get; set; }
    /// <summary>
    /// 该维度的消耗成本（USD，按本地价格表查询时动态计算；未匹配价格的请求按 0 计）。
    /// </summary>
    public decimal TotalCostUsd { get; set; }
}

/// <summary>
/// 来源、访问密钥或协议细分中的一个维度点。
/// </summary>
public sealed class AnalyticsBreakdownPointDto
{
    /// <summary>
    /// 稳定维度键。
    /// </summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>
    /// 面向展示的维度标签。
    /// </summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>
    /// 请求数。
    /// </summary>
    public int RequestCount { get; set; }
    /// <summary>
    /// 成功请求数。
    /// </summary>
    public int SuccessCount { get; set; }
    /// <summary>
    /// 失败请求数。
    /// </summary>
    public int FailedCount { get; set; }
    /// <summary>
    /// 成功率。
    /// </summary>
    public double SuccessRate { get; set; }
    /// <summary>
    /// Token 总数。
    /// </summary>
    public long TotalTokens { get; set; }
    /// <summary>
    /// 该维度的消耗成本（USD）。未定价模型的请求按 0 计。
    /// </summary>
    public decimal TotalCostUsd { get; set; }
    /// <summary>
    /// 平均总耗时（毫秒）。
    /// </summary>
    public double AverageTotalDurationMs { get; set; }
    /// <summary>
    /// 触发回退的请求数。
    /// </summary>
    public int FallbackRequestCount { get; set; }
}

/// <summary>
/// 回退请求链路的首末站点及结果统计。
/// </summary>
public sealed class AnalyticsFallbackChainPointDto
{
    /// <summary>
    /// 首次尝试站点的稳定标识。
    /// </summary>
    public string FirstSiteKey { get; set; } = string.Empty;
    /// <summary>
    /// 首次尝试站点名称。
    /// </summary>
    public string FirstSiteLabel { get; set; } = string.Empty;
    /// <summary>
    /// 最终尝试站点的稳定标识。
    /// </summary>
    public string FinalSiteKey { get; set; } = string.Empty;
    /// <summary>
    /// 最终尝试站点名称。
    /// </summary>
    public string FinalSiteLabel { get; set; } = string.Empty;
    /// <summary>
    /// 请求数。
    /// </summary>
    public int RequestCount { get; set; }
    /// <summary>
    /// 最终成功请求数。
    /// </summary>
    public int SuccessCount { get; set; }
    /// <summary>
    /// 成功率。
    /// </summary>
    public double SuccessRate { get; set; }
    /// <summary>
    /// 平均尝试次数。
    /// </summary>
    public double AverageAttemptCount { get; set; }
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
    public long InputTokens { get; set; }
    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public long CachedTokens { get; set; }
    /// <summary>
    /// 输入统计范围总量。
    /// </summary>
    public long TotalInputScope { get; set; }
    /// <summary>
    /// 缓存命中率。
    /// </summary>
    public double CacheHitRate { get; set; }
}

/// <summary>
/// 延迟百分位数结果，包含 P50、P95、P99 和有效样本数。
/// </summary>
public sealed class AnalyticsLatencyPercentileValuesDto
{
    /// <summary>
    /// P50 延迟（毫秒）。
    /// </summary>
    public double P50 { get; set; }
    /// <summary>
    /// P95 延迟（毫秒）。
    /// </summary>
    public double P95 { get; set; }
    /// <summary>
    /// P99 延迟（毫秒）。
    /// </summary>
    public double P99 { get; set; }
    /// <summary>
    /// 有效样本数。
    /// </summary>
    public int SampleCount { get; set; }
}

/// <summary>
/// 当前筛选最终请求的总耗时和首 Token 延迟百分位数。
/// </summary>
public sealed class AnalyticsLatencyPercentilesDto
{
    /// <summary>
    /// 总耗时百分位数。
    /// </summary>
    public AnalyticsLatencyPercentileValuesDto TotalDuration { get; set; } = new();
    /// <summary>
    /// 首 Token 延迟百分位数。
    /// </summary>
    public AnalyticsLatencyPercentileValuesDto FirstTokenLatency { get; set; } = new();
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
    /// 模型价格服务（查询时动态计价）。
    /// </summary>
    private readonly Application.Pricing.IModelPricingService _pricingService;
    // 回退链路数量上限，避免高基数链路维度撑大看板响应。
    private const int MaxFallbackChainDistributionCount = 20;
    // 统计查询按批读取，内存只保留每个 RequestId 的最终记录和链路摘要。
    private const int AnalyticsLogBatchSize = 2000;

    /// <summary>
    /// 创建统计分析控制器。
    /// </summary>
    public AnalyticsApiController(
        AppDbContext dbContext,
        IServiceScopeFactory scopeFactory,
        AnalyticsBackgroundQueryExecutor queryExecutor,
        IHostEnvironment hostEnvironment,
        Application.Pricing.IModelPricingService pricingService)
    {
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
        _queryExecutor = queryExecutor;
        _hostEnvironment = hostEnvironment;
        _pricingService = pricingService;
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
                return await BuildDashboardResponseAsync(dbContext, _pricingService, query, innerCancellationToken);
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
        Application.Pricing.IModelPricingService pricingService,
        AnalyticsQueryDto query,
        CancellationToken cancellationToken)
    {
        var siteNames = await dbContext.Sites

            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        // 细分只读取访问密钥标识和名称，避免把明文密钥或哈希载入统计查询。
        var accessKeyNames = (await dbContext.ProxyAccessKeys
                .Select(x => new { x.Id, x.KeyName })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => x.KeyName);

        var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);

        // rangeType=all 时把起点收敛到最早一条日志（按 RequestedAt 索引取第一行），
        // 避免从 0001 年起建桶与无意义的时间范围扫描；表为空时保持原值。
        // 注：不能用 MinAsync / Select(单列) / First(标量)——SqlSugar 对 DateTimeOffset 列的
        // 标量读取会抛 InvalidCastException，整行实体映射则正常。
        if (startTime == DateTimeOffset.MinValue)
        {
            var earliestRow = await dbContext.ProxyUsageLogs
                .OrderBy(x => x.RequestedAt)
                .FirstAsync(cancellationToken);
            if (earliestRow is not null)
            {
                var earliest = earliestRow.RequestedAt;
                if (earliest.Offset != TimeSpan.Zero)
                {
                    earliest = new DateTimeOffset(earliest.DateTime, TimeSpan.Zero);
                }

                startTime = earliest;
            }
        }

        var bucketType = ResolveBucketType(query.BucketType, query.RangeType, startTime, endTime);
        var source = NormalizeAnalyticsSource(query.Source);

        // 分批读取并按 RequestId 聚合，确保请求级归并不会丢失中间尝试，
        // 同时避免把整个时间范围内的所有重试日志一次性保留在内存中。
        var requestAggregates = new Dictionary<Guid, AnalyticsRequestAggregate>();
        var offset = 0;
        while (true)
        {
            var batch = await dbContext.ProxyUsageLogs
                .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
                .OrderBy(x => x.RequestedAt, SqlSugar.OrderByType.Asc)
                .OrderBy(x => x.Id, SqlSugar.OrderByType.Asc)
                .Skip(offset)
                .Take(AnalyticsLogBatchSize)
                .Select(x => new AITool.Domain.Proxy.ProxyUsageLog
                {
                    Id = x.Id,
                    RequestedAt = x.RequestedAt,
                    RequestId = x.RequestId,
                    AttemptIndex = x.AttemptIndex,
                    AttemptedModel = x.AttemptedModel,
                    TargetSiteId = x.TargetSiteId,
                    AccessKeyId = x.AccessKeyId,
                    ProtocolType = x.ProtocolType,
                    Source = x.Source,
                    Status = x.Status,
                    HttpStatusCode = x.HttpStatusCode,
                    IsStreamInterrupted = x.IsStreamInterrupted,
                    IsFinalResult = x.IsFinalResult,
                    FallbackTriggered = x.FallbackTriggered,
                    ErrorMessage = x.ErrorMessage,
                    ErrorCategory = x.ErrorCategory,
                    InputTokens = x.InputTokens,
                    CachedTokens = x.CachedTokens,
                    OutputTokens = x.OutputTokens,
                    TotalTokens = x.TotalTokens,
                    TotalDurationMs = x.TotalDurationMs,
                    FirstTokenLatencyMs = x.FirstTokenLatencyMs
                })
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            // SqlSugar 读回 DateTimeOffset 时 offset 被配成本地时区（+08:00），
            // 但存储的是 UTC 值，这里统一恢复为 UTC offset。
            foreach (var log in batch)
            {
                if (log.RequestedAt.Offset != TimeSpan.Zero)
                {
                    log.RequestedAt = new DateTimeOffset(log.RequestedAt.DateTime, TimeSpan.Zero);
                }

                if (!requestAggregates.TryGetValue(log.RequestId, out var aggregate))
                {
                    aggregate = new AnalyticsRequestAggregate(log.RequestId);
                    requestAggregates.Add(log.RequestId, aggregate);
                }

                aggregate.Add(log);
            }

            offset += batch.Count;
            if (batch.Count < AnalyticsLogBatchSize)
            {
                break;
            }
        }

        // 先确定每个请求的最终记录，再用最终记录应用所有维度筛选。
        var finalLogs = requestAggregates.Values
            .Select(x => x.FinalLog)
            .Where(x => MatchesAnalyticsFilters(x, query, source))
            .ToList();
        var matchedRequestIds = finalLogs
            .Select(x => x.RequestId)
            .ToHashSet();

        // 命中请求后只保留每条链路摘要，避免为了回退判断重新恢复整条尝试列表。
        var matchedAggregates = requestAggregates.Values
            .Where(x => matchedRequestIds.Contains(x.RequestId))
            .ToList();
        var fallbackRequestIds = matchedAggregates
            .Where(x => x.FallbackTriggered || x.MaxAttemptIndex > 1)
            .Select(x => x.RequestId)
            .ToHashSet();

        // 一次预分桶：把 finalLogs 按 bucket 归类，后续 5 个趋势方法直接用预分桶结果，
        // 避免每个趋势方法各自遍历 finalLogs 做 Where().ToList()（5 轮全扫 → 1 轮）。
        var buckets = BuildBuckets(startTime, endTime, bucketType);
        var bucketedLogs = buckets.ToDictionary(b => b.Label, _ => new List<AITool.Domain.Proxy.ProxyUsageLog>());
        foreach (var log in finalLogs)
        {
            var bucketIndex = FindBucketIndex(buckets, log.RequestedAt);
            if (bucketIndex >= 0)
            {
                bucketedLogs[buckets[bucketIndex].Label].Add(log);
            }
        }

        // 成本按本地价格表查询时动态计算（历史日志自动兼容，价格修改后看板即时反映）。
        // 先确保价格表已加载，再对每条最终记录只计价一次（summary / 趋势 / 分布 / 细分复用）。
        await pricingService.GetCatalogAsync(cancellationToken);
        var costByLog = new Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost>(finalLogs.Count);
        foreach (var log in finalLogs)
        {
            costByLog[log] = pricingService.CalculateCostUsd(log.AttemptedModel, log.RequestedAt, log.InputTokens, log.CachedTokens, log.OutputTokens);
        }

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
                Source = source,
                SiteId = query.SiteId,
                AccessKeyId = query.AccessKeyId
            },
            Summary = BuildSummary(finalLogs, fallbackRequestIds, costByLog),
            RequestTrend = BuildRequestTrend(buckets, bucketedLogs),
            ResultTrend = BuildResultTrend(buckets, bucketedLogs),
            TokenTrend = BuildTokenTrend(buckets, bucketedLogs, costByLog),
            DurationTrend = BuildDurationTrend(buckets, bucketedLogs),
            FallbackTrend = BuildFallbackTrend(buckets, bucketedLogs, fallbackRequestIds),
            SiteDistribution = BuildSiteDistribution(finalLogs, siteNames, costByLog),
            ModelDistribution = BuildModelDistribution(finalLogs, costByLog),
            ModelCacheRatioDistribution = BuildModelCacheRatioDistribution(finalLogs),
            SourceBreakdown = BuildSourceBreakdown(finalLogs, fallbackRequestIds, costByLog),
            AccessKeyBreakdown = BuildAccessKeyBreakdown(finalLogs, fallbackRequestIds, accessKeyNames, costByLog),
            ProtocolBreakdown = BuildProtocolBreakdown(finalLogs, fallbackRequestIds, costByLog),
            FailureReasonBreakdown = BuildFailureReasonBreakdown(finalLogs, fallbackRequestIds, costByLog),
            StatusCodeBreakdown = BuildStatusCodeBreakdown(finalLogs, fallbackRequestIds, costByLog),
            FallbackChainDistribution = BuildFallbackChainDistribution(finalLogs, matchedAggregates, fallbackRequestIds, siteNames),
            LatencyPercentiles = BuildLatencyPercentiles(finalLogs)
        };
    }

    /// <summary>
    /// 构建汇总统计。
    /// </summary>
    private static AnalyticsSummaryDto BuildSummary(List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs, HashSet<Guid> fallbackRequestIds, Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
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
            TotalInputTokens = finalLogs.Sum(x => (long)x.InputTokens + (long)x.CachedTokens),
            TotalCachedTokens = finalLogs.Sum(x => (long)x.CachedTokens),
            TotalOutputTokens = finalLogs.Sum(x => (long)x.OutputTokens),
            TotalTokens = finalLogs.Sum(x => (long)x.TotalTokens),
            // 未匹配价格的请求按 0 计入总成本（明细仍可在模型分布中看到未定价项）。
            TotalCostUsd = Math.Round(costByLog.Values.Sum(x => x.CostUsd ?? 0m), 6),
            AverageTotalDurationMs = totalRequests == 0 ? 0 : Math.Round(finalLogs.Average(x => x.TotalDurationMs), 2),
            AverageFirstTokenLatencyMs = totalRequests == 0 ? 0 : Math.Round(finalLogs.Average(x => x.FirstTokenLatencyMs), 2),
            FallbackRequestCount = fallbackRequestIds.Count
        };
    }

    /// <summary>
    /// 构建请求趋势。
    /// </summary>
    private static List<AnalyticsTrendPointDto> BuildRequestTrend(
        List<AnalyticsBucket> buckets,
        Dictionary<string, List<AITool.Domain.Proxy.ProxyUsageLog>> bucketedLogs)
    {
        return buckets
            .Select(bucket => new AnalyticsTrendPointDto
            {
                Label = bucket.Label,
                RequestCount = bucketedLogs[bucket.Label].Count
            })
            .ToList();
    }

    /// <summary>
    /// 构建结果趋势。
    /// </summary>
    private static List<AnalyticsResultTrendPointDto> BuildResultTrend(
        List<AnalyticsBucket> buckets,
        Dictionary<string, List<AITool.Domain.Proxy.ProxyUsageLog>> bucketedLogs)
    {
        return buckets
            .Select(bucket =>
            {
                var bucketLogs = bucketedLogs[bucket.Label];
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
        List<AnalyticsBucket> buckets,
        Dictionary<string, List<AITool.Domain.Proxy.ProxyUsageLog>> bucketedLogs,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return buckets
            .Select(bucket =>
            {
                var bucketLogs = bucketedLogs[bucket.Label];

                return new AnalyticsTokenTrendPointDto
                {
                    Label = bucket.Label,
                    InputTokens = bucketLogs.Sum(x => (long)x.InputTokens),
                    CachedTokens = bucketLogs.Sum(x => (long)x.CachedTokens),
                    OutputTokens = bucketLogs.Sum(x => (long)x.OutputTokens),
                    TotalTokens = bucketLogs.Sum(x => (long)x.TotalTokens),
                    CostUsd = Math.Round(bucketLogs.Sum(x => costByLog.TryGetValue(x, out var cost) ? cost.CostUsd ?? 0m : 0m), 6),
                    InputCostUsd = Math.Round(bucketLogs.Sum(x => costByLog.TryGetValue(x, out var cost) ? cost.InputCostUsd : 0m), 6),
                    CachedCostUsd = Math.Round(bucketLogs.Sum(x => costByLog.TryGetValue(x, out var cost) ? cost.CachedCostUsd : 0m), 6),
                    OutputCostUsd = Math.Round(bucketLogs.Sum(x => costByLog.TryGetValue(x, out var cost) ? cost.OutputCostUsd : 0m), 6)
                };
            })
            .ToList();
    }

    /// <summary>
    /// 构建耗时趋势。
    /// </summary>
    private static List<AnalyticsDurationTrendPointDto> BuildDurationTrend(
        List<AnalyticsBucket> buckets,
        Dictionary<string, List<AITool.Domain.Proxy.ProxyUsageLog>> bucketedLogs)
    {
        return buckets
            .Select(bucket =>
            {
                var bucketLogs = bucketedLogs[bucket.Label];

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
        List<AnalyticsBucket> buckets,
        Dictionary<string, List<AITool.Domain.Proxy.ProxyUsageLog>> bucketedLogs,
        HashSet<Guid> fallbackRequestIds)
    {
        return buckets
            .Select(bucket =>
            {
                var bucketLogs = bucketedLogs[bucket.Label];
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
    /// 构建来源细分，来源键统一为稳定的小写标识。
    /// </summary>
    private static List<AnalyticsBreakdownPointDto> BuildSourceBreakdown(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        HashSet<Guid> fallbackRequestIds,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return BuildBreakdown(
            finalLogs,
            fallbackRequestIds,
            costByLog,
            x => NormalizeAnalyticsSource(x.Source) ?? "-",
            ResolveAnalyticsSourceLabel);
    }

    /// <summary>
    /// 构建访问密钥细分，只使用访问密钥名称作为展示标签。
    /// </summary>
    private static List<AnalyticsBreakdownPointDto> BuildAccessKeyBreakdown(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        HashSet<Guid> fallbackRequestIds,
        IReadOnlyDictionary<Guid, string> accessKeyNames,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return BuildBreakdown(
            finalLogs,
            fallbackRequestIds,
            costByLog,
            x => x.AccessKeyId.ToString("D"),
            key => Guid.TryParse(key, out var accessKeyId)
                && accessKeyNames.TryGetValue(accessKeyId, out var name)
                ? NormalizeAnalyticsLabel(name)
                : "-");
    }

    /// <summary>
    /// 构建协议细分，协议键和标签均来自最终记录的 ProtocolType。
    /// </summary>
    private static List<AnalyticsBreakdownPointDto> BuildProtocolBreakdown(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        HashSet<Guid> fallbackRequestIds,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return BuildBreakdown(
            finalLogs,
            fallbackRequestIds,
            costByLog,
            x => NormalizeAnalyticsLabel(x.ProtocolType),
            key => key);
    }

    /// <summary>
    /// 按最终请求统一构建细分统计，fallback 请求按 RequestId 去重。
    /// </summary>
    private static List<AnalyticsBreakdownPointDto> BuildBreakdown(
        IEnumerable<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        HashSet<Guid> fallbackRequestIds,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog,
        Func<AITool.Domain.Proxy.ProxyUsageLog, string> keySelector,
        Func<string, string> labelSelector)
    {
        return finalLogs
            .GroupBy(keySelector)
            .Select(group =>
            {
                var requestCount = group.Count();
                var successCount = group.Count(x => IsSuccess(x.Status));
                var fallbackCount = group
                    .Select(x => x.RequestId)
                    .Distinct()
                    .Count(fallbackRequestIds.Contains);

                return new AnalyticsBreakdownPointDto
                {
                    Key = group.Key,
                    Label = labelSelector(group.Key),
                    RequestCount = requestCount,
                    SuccessCount = successCount,
                    FailedCount = requestCount - successCount,
                    SuccessRate = requestCount == 0 ? 0 : Math.Round(successCount * 100d / requestCount, 2),
                    TotalTokens = group.Sum(x => (long)x.TotalTokens),
                    TotalCostUsd = Math.Round(group.Sum(x => costByLog.TryGetValue(x, out var cost) ? cost.CostUsd ?? 0m : 0m), 6),
                    AverageTotalDurationMs = requestCount == 0 ? 0 : Math.Round(group.Average(x => x.TotalDurationMs), 2),
                    FallbackRequestCount = fallbackCount
                };
            })
            .OrderByDescending(x => x.RequestCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 对最终请求集合一次性计算总耗时和首 Token 延迟百分位数。
    /// </summary>
    private static AnalyticsLatencyPercentilesDto BuildLatencyPercentiles(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs)
    {
        var totalDuration = PercentileCalculator.Calculate(
            finalLogs.Select(x => (double)x.TotalDurationMs));
        var firstTokenLatency = PercentileCalculator.Calculate(
            finalLogs.Select(x => (double)x.FirstTokenLatencyMs));

        return new AnalyticsLatencyPercentilesDto
        {
            TotalDuration = ToLatencyPercentileValues(totalDuration),
            FirstTokenLatency = ToLatencyPercentileValues(firstTokenLatency)
        };
    }

    /// <summary>
    /// 转换公共百分位计算结果 DTO。
    /// </summary>
    private static AnalyticsLatencyPercentileValuesDto ToLatencyPercentileValues(PercentileResult result)
    {
        return new AnalyticsLatencyPercentileValuesDto
        {
            P50 = result.P50,
            P95 = result.P95,
            P99 = result.P99,
            SampleCount = result.SampleCount
        };
    }

    /// <summary>
    /// 构建最终失败请求的错误分类细分，不读取中间失败尝试作为统计请求。
    /// </summary>
    private static List<AnalyticsBreakdownPointDto> BuildFailureReasonBreakdown(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        HashSet<Guid> fallbackRequestIds,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return BuildBreakdown(
            finalLogs.Where(x => !IsSuccess(x.Status)),
            fallbackRequestIds,
            costByLog,
            x => x.ErrorCategory
                 ?? UsageLogErrorClassifier.Classify(
                     x.Status,
                     x.HttpStatusCode,
                     x.ErrorMessage,
                     x.IsStreamInterrupted)
                 ?? "other",
            ResolveFailureCategoryLabel);
    }

    /// <summary>
    /// 构建最终失败请求的 HTTP 状态码细分，空值或零统一归入 no-response。
    /// </summary>
    private static List<AnalyticsBreakdownPointDto> BuildStatusCodeBreakdown(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        HashSet<Guid> fallbackRequestIds,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return BuildBreakdown(
            finalLogs.Where(x => !IsSuccess(x.Status)),
            fallbackRequestIds,
            costByLog,
            x => x.HttpStatusCode is null or 0 ? "no-response" : x.HttpStatusCode.Value.ToString(),
            ResolveHttpStatusLabel);
    }

    /// <summary>
    /// 构建发生 fallback 的请求链路分布，并按首末站点组合去重。
    /// </summary>
    private static List<AnalyticsFallbackChainPointDto> BuildFallbackChainDistribution(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        IReadOnlyList<AnalyticsRequestAggregate> matchedAggregates,
        HashSet<Guid> fallbackRequestIds,
        IReadOnlyDictionary<Guid, string> siteNames)
    {
        var finalByRequestId = finalLogs.ToDictionary(x => x.RequestId);
        var requestChains = matchedAggregates
            .Where(aggregate => fallbackRequestIds.Contains(aggregate.RequestId))
            .Select(aggregate =>
            {
                var finalRecord = finalByRequestId[aggregate.RequestId];

                return new
                {
                    FirstSiteId = aggregate.FirstAttempt.TargetSiteId,
                    FinalSiteId = aggregate.FinalLog.TargetSiteId,
                    IsSuccess = IsSuccess(finalRecord.Status),
                    AttemptCount = Math.Max(1, aggregate.MaxAttemptIndex)
                };
            });

        return requestChains
            .GroupBy(x => new { x.FirstSiteId, x.FinalSiteId })
            .Select(group =>
            {
                var requestCount = group.Count();
                var successCount = group.Count(x => x.IsSuccess);
                return new AnalyticsFallbackChainPointDto
                {
                    FirstSiteKey = group.Key.FirstSiteId.ToString("D"),
                    FirstSiteLabel = ResolveSiteAnalyticsLabel(group.Key.FirstSiteId, siteNames),
                    FinalSiteKey = group.Key.FinalSiteId.ToString("D"),
                    FinalSiteLabel = ResolveSiteAnalyticsLabel(group.Key.FinalSiteId, siteNames),
                    RequestCount = requestCount,
                    SuccessCount = successCount,
                    SuccessRate = requestCount == 0 ? 0 : Math.Round(successCount * 100d / requestCount, 2),
                    AverageAttemptCount = Math.Round(group.Average(x => x.AttemptCount), 2)
                };
            })
            .OrderByDescending(x => x.RequestCount)
            .ThenBy(x => x.FirstSiteLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FinalSiteLabel, StringComparer.OrdinalIgnoreCase)
            // 仅返回 Top 20，控制高基数首末站点组合的响应体大小。
            .Take(MaxFallbackChainDistributionCount)
            .ToList();
    }

    /// <summary>
    /// 将错误分类键转换为稳定的展示标签，不返回错误正文。
    /// </summary>
    private static string ResolveFailureCategoryLabel(string key)
    {
        return key switch
        {
            "authentication" => "认证失败",
            "rate-limit" => "频率限制",
            "timeout" => "超时",
            "stream-interrupted" => "流中断",
            "model-not-found" => "模型不存在",
            "upstream-error" => "上游错误",
            "network-error" => "网络错误",
            _ => "其他"
        };
    }

    /// <summary>
    /// 将 HTTP 状态码键转换为稳定展示标签。
    /// </summary>
    private static string ResolveHttpStatusLabel(string key)
    {
        return key == "no-response" ? "无响应" : key;
    }

    /// <summary>
    /// 使用站点名称映射生成链路标签，缺失站点统一显示占位符。
    /// </summary>
    private static string ResolveSiteAnalyticsLabel(Guid siteId, IReadOnlyDictionary<Guid, string> siteNames)
    {
        return siteNames.TryGetValue(siteId, out var siteName)
            ? NormalizeAnalyticsLabel(siteName)
            : "-";
    }

    /// <summary>
    /// 将稳定来源键转换为与 Usage Logs 一致的展示标签。
    /// </summary>
    private static string ResolveAnalyticsSourceLabel(string key)
    {
        return key switch
        {
            "proxy" => "代理",
            "chat" => "对话测试",
            "claude-code" => "Claude Code",
            "codex" => "Codex",
            "open-code" => "Open Code",
            "zcode" => "ZCode",
            "deepseek-harness" => "DeepSeek Harness",
            "detection-manual" => "手动检测",
            "detection-task" => "定时检测",
            _ => NormalizeAnalyticsLabel(key)
        };
    }

    /// <summary>
    /// 构建站点分布。
    /// </summary>
    private static List<AnalyticsDistributionPointDto> BuildSiteDistribution(
        List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs,
        IReadOnlyDictionary<Guid, string> siteNames,
        Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return finalLogs
            .GroupBy(x => x.TargetSiteId)
            .Select(g => new AnalyticsDistributionPointDto
            {
                Key = g.Key.ToString("D"),
                Label = siteNames.TryGetValue(g.Key, out var siteName) ? NormalizeAnalyticsLabel(siteName) : "-",
                RequestCount = g.Count(),
                SuccessCount = g.Count(x => IsSuccess(x.Status)),
                FailedCount = g.Count(x => !IsSuccess(x.Status)),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                InputTokens = g.Sum(x => (long)x.InputTokens),
                CachedTokens = g.Sum(x => (long)x.CachedTokens),
                OutputTokens = g.Sum(x => (long)x.OutputTokens),
                AverageTotalDurationMs = Math.Round(g.Average(x => x.TotalDurationMs), 2),
                TotalCostUsd = Math.Round(g.Sum(x => costByLog.TryGetValue(x, out var cost) ? cost.CostUsd ?? 0m : 0m), 6)
            })
            .OrderByDescending(x => x.RequestCount)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 构建模型分布。
    /// </summary>
    private static List<AnalyticsDistributionPointDto> BuildModelDistribution(List<AITool.Domain.Proxy.ProxyUsageLog> finalLogs, Dictionary<AITool.Domain.Proxy.ProxyUsageLog, Application.Pricing.ModelUsageCost> costByLog)
    {
        return finalLogs
            .GroupBy(x => x.AttemptedModel)
            .Select(g => new AnalyticsDistributionPointDto
            {
                Key = g.Key,
                Label = NormalizeAnalyticsLabel(g.Key),
                RequestCount = g.Count(),
                SuccessCount = g.Count(x => IsSuccess(x.Status)),
                FailedCount = g.Count(x => !IsSuccess(x.Status)),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                InputTokens = g.Sum(x => (long)x.InputTokens),
                CachedTokens = g.Sum(x => (long)x.CachedTokens),
                OutputTokens = g.Sum(x => (long)x.OutputTokens),
                AverageTotalDurationMs = Math.Round(g.Average(x => x.TotalDurationMs), 2),
                TotalCostUsd = Math.Round(g.Sum(x => costByLog.TryGetValue(x, out var cost) ? cost.CostUsd ?? 0m : 0m), 6)
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
                var inputTokens = g.Sum(x => (long)x.InputTokens);
                var cachedTokens = g.Sum(x => (long)x.CachedTokens);
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
    /// 判断请求最终记录是否满足看板筛选条件。
    /// </summary>
    private static bool MatchesAnalyticsFilters(
        AITool.Domain.Proxy.ProxyUsageLog log,
        AnalyticsQueryDto query,
        string? source)
    {
        return (string.Equals(query.ProtocolType, "all", StringComparison.OrdinalIgnoreCase)
                || log.ProtocolType == query.ProtocolType)
            && (string.Equals(query.ModelName, "all", StringComparison.OrdinalIgnoreCase)
                || log.AttemptedModel == query.ModelName)
            && (source is null
                || string.Equals(log.Source, source, StringComparison.OrdinalIgnoreCase))
            && (!query.SiteId.HasValue || log.TargetSiteId == query.SiteId.Value)
            && (!query.AccessKeyId.HasValue || log.AccessKeyId == query.AccessKeyId.Value);
    }

    /// <summary>
    /// 规范化来源筛选值，空值表示不筛选来源。
    /// </summary>
    private static string? NormalizeAnalyticsSource(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
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
        // 桶数上限：自定义范围可任意长（rangeType=all 时从 MinValue 起算），不设限会导致
        // 标签字典膨胀与 O(日志数×桶数) 的分桶循环爆炸。超限时保留最近的一段（日志总在近期），
        // 汇总与分布仍覆盖全部日志，仅趋势图按上限截断。
        const int maxBuckets = 2000;

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

        if (buckets.Count > maxBuckets)
        {
            buckets.RemoveRange(0, buckets.Count - maxBuckets);
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
    /// 在按时间升序排列的桶集合中二分定位日志所属桶。
    /// 找不到时表示该日志落在趋势图被截断的旧区间，保持原有忽略语义。
    /// </summary>
    private static int FindBucketIndex(IReadOnlyList<AnalyticsBucket> buckets, DateTimeOffset requestedAt)
    {
        var low = 0;
        var high = buckets.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var bucket = buckets[middle];
            if (requestedAt < bucket.Start)
            {
                high = middle - 1;
            }
            else if (requestedAt >= bucket.End)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
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
    /// 生成时间桶标签。标签会作为字典键使用，必须包含年份以保证超长范围（跨年）下唯一。
    /// </summary>
    private static string FormatBucketLabel(DateTimeOffset start, DateTimeOffset end, string bucketType)
    {
        return bucketType switch
        {
            "hour" => $"{start:yyyy-MM-dd HH}:00",
            // 周桶可能来自按月范围内的截断区间，因此展示实际日期范围更直观。
            "week" => $"{start:yyyy-MM-dd} ~ {end.AddDays(-1):MM-dd}",
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
            NormalizeAnalyticsSource(query.Source) ?? "-",
            query.SiteId?.ToString() ?? "-",
            query.AccessKeyId?.ToString() ?? "-");
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
    /// 按请求聚合 Analytics 所需的最终记录和回退链路摘要。
    /// 只保留每个 RequestId 的少量状态，避免重试记录数量直接决定看板内存占用。
    /// </summary>
    private sealed class AnalyticsRequestAggregate
    {
        public AnalyticsRequestAggregate(Guid requestId)
        {
            RequestId = requestId;
        }

        public Guid RequestId { get; }
        public AITool.Domain.Proxy.ProxyUsageLog FinalLog { get; private set; } = null!;
        public AITool.Domain.Proxy.ProxyUsageLog FirstAttempt { get; private set; } = null!;
        public int MaxAttemptIndex { get; private set; }
        public bool FallbackTriggered { get; private set; }

        public void Add(AITool.Domain.Proxy.ProxyUsageLog log)
        {
            if (FinalLog is null || IsPreferredFinalLog(log, FinalLog))
            {
                FinalLog = log;
            }

            if (FirstAttempt is null || IsEarlierAttempt(log, FirstAttempt))
            {
                FirstAttempt = log;
            }

            MaxAttemptIndex = Math.Max(MaxAttemptIndex, log.AttemptIndex);
            FallbackTriggered |= log.FallbackTriggered;
        }

        private static bool IsPreferredFinalLog(
            AITool.Domain.Proxy.ProxyUsageLog candidate,
            AITool.Domain.Proxy.ProxyUsageLog current)
        {
            if (candidate.IsFinalResult != current.IsFinalResult)
            {
                return candidate.IsFinalResult;
            }

            if (candidate.AttemptIndex != current.AttemptIndex)
            {
                return candidate.AttemptIndex > current.AttemptIndex;
            }

            return candidate.RequestedAt > current.RequestedAt;
        }

        private static bool IsEarlierAttempt(
            AITool.Domain.Proxy.ProxyUsageLog candidate,
            AITool.Domain.Proxy.ProxyUsageLog current)
        {
            if (candidate.AttemptIndex != current.AttemptIndex)
            {
                return candidate.AttemptIndex < current.AttemptIndex;
            }

            return candidate.RequestedAt < current.RequestedAt;
        }
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
