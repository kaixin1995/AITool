using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

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
    /// 访问密钥标识。
    /// </summary>
    public Guid? AccessKeyId { get; set; }
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
    /// 访问密钥名称。
    /// </summary>
    public string AccessKeyName { get; set; } = string.Empty;
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
    /// 本次尝试的消耗成本（USD，按本地价格表查询时动态计算）；null 表示该模型未定价。
    /// </summary>
    public decimal? CostUsd { get; set; }
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
    /// 访问密钥名称。
    /// </summary>
    public string AccessKeyName { get; set; } = string.Empty;
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
    public long TotalTokens { get; set; }
    /// <summary>
    /// 消耗总成本（USD，当前筛选范围全部尝试行求和；未定价模型的行按 0 计）。
    /// </summary>
    public decimal TotalCostUsd { get; set; }
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
    private const int CostCalculationBatchSize = 1000;

    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 模型价格服务（列表行与摘要的动态计价）。
    /// </summary>
    private readonly Application.Pricing.IModelPricingService _pricingService;

    /// <summary>
    /// 创建用量日志控制器。
    /// </summary>
    public UsageLogsApiController(AppDbContext dbContext, Application.Pricing.IModelPricingService pricingService)
    {
        _dbContext = dbContext;
        _pricingService = pricingService;
    }

    /// <summary>
    /// 获取筛选项（全部站点 + 全部访问密钥，供筛选下拉框）。
    /// 迁移自 UsageLogs/Index.cshtml.cs 的 OnGetAsync。
    /// </summary>
    [HttpGet("filters")]
    public async Task<IActionResult> GetFilters(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .OrderBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name })
            .ToListAsync(cancellationToken);
        var accessKeys = await _dbContext.ProxyAccessKeys
            .OrderBy(k => k.KeyName)
            .Select(k => new { id = k.Id, name = k.KeyName })
            .ToListAsync(cancellationToken);

        return Ok(new { sites, accessKeys });
    }

    /// <summary>
    /// 获取用量日志列表。
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<UsageLogListResponseDto>> GetList([FromQuery] UsageLogListQueryDto query, CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var siteModelNameMap = await BuildSiteModelNameMapAsync(cancellationToken);
        var accessKeyNames = await _dbContext.ProxyAccessKeys
            .ToDictionaryAsync(x => x.Id, x => x.KeyName, cancellationToken);
        var (startTime, endTime) = ResolveTimeRange(query.RangeType, query.StartTime, query.EndTime);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // 数据库层过滤 + 分页：SqlSugar 支持 DateTimeOffset 下推，不再全表加载到内存。
        var baseQuery = _dbContext.ProxyUsageLogs
            .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
            .WhereIF(query.SiteId.HasValue, x => x.TargetSiteId == query.SiteId!.Value)
            .WhereIF(query.AccessKeyId.HasValue, x => x.AccessKeyId == query.AccessKeyId!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Source), x => x.Source == query.Source!)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Status), x => x.Status == query.Status!);

        // ModelKeyword 是模糊匹配（Contains），SqlSugar 对此也能下推。
        if (!string.IsNullOrWhiteSpace(query.ModelKeyword))
        {
            baseQuery = baseQuery.Where(x => x.AttemptedModel.Contains(query.ModelKeyword) || x.RequestModel.Contains(query.ModelKeyword));
        }

        var totalCount = await baseQuery.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = totalPages == 0 ? 1 : Math.Min(Math.Max(1, query.Page), totalPages);

        // 确保价格表已加载（首次访问时从本地 JSON 读取），行级计价才能命中。
        await _pricingService.GetCatalogAsync(cancellationToken);

        // 只加载当前页的数据，不加载全表
        var pagedLogs = await baseQuery
            .OrderBy(x => x.RequestedAt, SqlSugar.OrderByType.Desc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = pagedLogs
            .Select(x => new UsageLogListItemDto
            {
                Id = x.Id,
                RequestId = x.RequestId,
                ProtocolType = x.ProtocolType,
                RequestModel = x.RequestModel,
                AttemptedModel = x.AttemptedModel,
                SiteModelName = ResolveSiteModelName(siteModelNameMap, x.TargetSiteId, x.AttemptedModel),
                Status = x.Status,
                Source = x.Source,
                SiteName = sites.TryGetValue(x.TargetSiteId, out var siteName) ? siteName : "-",
                AccessKeyName = accessKeyNames.TryGetValue(x.AccessKeyId, out var keyName) ? keyName : "-",
                RetryCount = x.RetryCount,
                AttemptIndex = x.AttemptIndex,
                IsFinalResult = x.IsFinalResult,
                FallbackTriggered = x.FallbackTriggered,
                InputTokens = x.InputTokens,
                CachedTokens = x.CachedTokens,
                OutputTokens = x.OutputTokens,
                TotalTokens = x.TotalTokens,
                // 单页最多 100 行，逐行动态计价；峰谷条目按各自请求时间取档。
                CostUsd = _pricingService.CalculateCostUsd(x.AttemptedModel, x.RequestedAt, x.InputTokens, x.CachedTokens, x.OutputTokens).CostUsd,
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
        var siteModelNameMap = await BuildSiteModelNameMapAsync(cancellationToken);
        var accessKeyNames = await _dbContext.ProxyAccessKeys
            .ToDictionaryAsync(x => x.Id, x => x.KeyName, cancellationToken);
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
                    SiteModelName = ResolveSiteModelName(siteModelNameMap, x.TargetSiteId, x.AttemptedModel),
                    SiteName = sites.TryGetValue(x.TargetSiteId, out var siteName) ? siteName : "-",
                    AccessKeyName = accessKeyNames.TryGetValue(x.AccessKeyId, out var keyName) ? keyName : "-",
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

            // 数据库层过滤。
            var baseQuery = _dbContext.ProxyUsageLogs
                .Where(x => x.RequestedAt >= startTime && x.RequestedAt < endTime)
                .WhereIF(query.SiteId.HasValue, x => x.TargetSiteId == query.SiteId!.Value)
                .WhereIF(query.AccessKeyId.HasValue, x => x.AccessKeyId == query.AccessKeyId!.Value)
                .WhereIF(!string.IsNullOrWhiteSpace(query.Source), x => x.Source == query.Source!)
                .WhereIF(!string.IsNullOrWhiteSpace(query.Status), x => x.Status == query.Status!);

            if (!string.IsNullOrWhiteSpace(query.ModelKeyword))
            {
                baseQuery = baseQuery.Where(x => x.AttemptedModel.Contains(query.ModelKeyword) || x.RequestModel.Contains(query.ModelKeyword));
            }

            // 数据库层过滤 + 聚合：不下全表加载到内存，避免大数据量（如"全部"范围）OOM。
            var totalCount = await baseQuery.CountAsync(cancellationToken);
            var successRequests = await baseQuery.CountAsync(x => x.Status == "success", cancellationToken);
            var failedRequests = await baseQuery.CountAsync(x => x.Status == "fail", cancellationToken);
            var successRate = totalCount == 0
                ? 0d
                : Math.Round(successRequests * 100d / totalCount, 2, MidpointRounding.AwayFromZero);
            // (long) 强转避免 int 求和累计超 21 亿溢出（见 9391615）。
            var totalTokens = totalCount == 0 ? 0L : await baseQuery.SumAsync(x => (long)x.TotalTokens);
            var maxDurationMs = totalCount == 0
                ? 0
                : await baseQuery.MaxAsync(x => x.TotalDurationMs, cancellationToken);

            // 成本无法在 SQL 层聚合（价格表在本地 JSON）：只投影计价所需的最小列集合，
            // 并分批读取，避免“全部”范围把所有计价行一次性载入内存。
            var totalCostUsd = 0m;
            if (totalCount > 0)
            {
                await _pricingService.GetCatalogAsync(cancellationToken);
                var offset = 0;
                while (true)
                {
                    var costRows = await baseQuery
                        .OrderBy(x => x.RequestedAt, SqlSugar.OrderByType.Asc)
                        .OrderBy(x => x.Id, SqlSugar.OrderByType.Asc)
                        .Skip(offset)
                        .Take(CostCalculationBatchSize)
                        .Select(x => new { x.AttemptedModel, x.RequestedAt, x.InputTokens, x.CachedTokens, x.OutputTokens })
                        .ToListAsync(cancellationToken);
                    if (costRows.Count == 0)
                    {
                        break;
                    }

                    foreach (var row in costRows)
                    {
                        totalCostUsd += _pricingService.CalculateCostUsd(
                            row.AttemptedModel,
                            row.RequestedAt,
                            row.InputTokens,
                            row.CachedTokens,
                            row.OutputTokens).CostUsd ?? 0m;
                    }

                    offset += costRows.Count;
                    if (costRows.Count < CostCalculationBatchSize)
                    {
                        break;
                    }
                }

                totalCostUsd = Math.Round(totalCostUsd, 6);
            }

            return Ok(new UsageLogSummaryDto
            {
                TotalRequests = totalCount,
                FailedRequests = failedRequests,
                SuccessRate = successRate,
                TotalTokens = totalTokens,
                TotalCostUsd = totalCostUsd,
                MaxDurationMs = maxDurationMs
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

        // 结束时间统一用"今天结束"（明天0点），与 Analytics 口径一致，
        // 避免不同页面对"当天"的结束时刻定义不同导致统计范围偏差。
        var endOfToday = now.Date.AddDays(1);
        // 周起点也统一为周一（与 AnalyticsApiController 相同公式）；.DayOfWeek 周日=0，
        // 直接 -(int)DayOfWeek 会把周日起算成"本周"，导致两页"本周"范围相差 1-2 天。
        var daysFromMonday = (7 + (int)now.DayOfWeek - 1) % 7;
        return normalized switch
        {
            "week" => (now.Date.AddDays(-daysFromMonday), endOfToday),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), endOfToday),
            "all" => (DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            _ => (now.Date, endOfToday)
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
    /// 构建 (SiteId, UpstreamModelName) → 站点模型名称映射。
    /// 同一组合存在多条规则时取 Priority 最小（优先级最高）的那条，与原逐条线性扫描语义一致。
    /// </summary>
    private async Task<Dictionary<(Guid SiteId, string UpstreamModelName), string>> BuildSiteModelNameMapAsync(CancellationToken cancellationToken)
    {
        var rules = await _dbContext.ProxyRouteRules
            .OrderBy(x => x.Priority)
            .Select(x => new { x.SiteId, x.UpstreamModelName, x.SiteModelName })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<(Guid, string), string>();
        foreach (var rule in rules)
        {
            // 升序遍历下首次遇到的即最小 Priority，后续同组合规则直接忽略。
            map.TryAdd((rule.SiteId, rule.UpstreamModelName), rule.SiteModelName);
        }

        return map;
    }

    /// <summary>
    /// 解析站点模型名称（O(1) 字典查找）。
    /// </summary>
    private static string ResolveSiteModelName(
        IReadOnlyDictionary<(Guid SiteId, string UpstreamModelName), string> siteModelNameMap,
        Guid siteId,
        string attemptedModel)
    {
        return siteModelNameMap.TryGetValue((siteId, attemptedModel), out var siteModelName)
            ? siteModelName
            : string.Empty;
    }
}
