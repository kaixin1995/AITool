using AITool.Application.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Web.Pages.Admin.ClientSimulator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AITool.Web.Services;

namespace AITool.Web.Pages.Admin.Developer.Invocations;

/// <summary>
/// 当前模型并发检测响应。
/// </summary>
public sealed class DeveloperModelConcurrencyResponse
{
    /// <summary>
    /// 最近刷新时间。
    /// </summary>
    public DateTimeOffset RefreshedAt { get; set; }
    /// <summary>
    /// 当前活跃项。
    /// </summary>
    public List<DeveloperModelConcurrencyDto> Items { get; set; } = [];
}

/// <summary>
/// 当前模型并发检测项。
/// </summary>
public sealed class DeveloperModelConcurrencyDto
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 当前并发数。
    /// </summary>
    public int ActiveCount { get; set; }
    /// <summary>
    /// 配置的最大并发数，null 表示未设置限制。
    /// </summary>
    public int? MaxConcurrency { get; set; }
    /// <summary>
    /// 当前排队等待的请求数。
    /// </summary>
    public int QueueCount { get; set; }
}

/// <summary>
/// 开发者调用记录页面模型。
/// </summary>
public sealed class IndexModel : PageModel
{
    /// <summary>
    /// 每页记录数。
    /// </summary>
    public const int PageSize = 20;

    /// <summary>
    /// 系统运行时设置服务。
    /// </summary>
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    /// <summary>
    /// 调用跟踪存储。
    /// </summary>
    private readonly DeveloperInvocationTraceStore _traceStore;
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理请求元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 开发者调用记录页面模型。
    /// </summary>
    public IndexModel(
        ISystemRuntimeSettingsService runtimeSettingsService,
        DeveloperInvocationTraceStore traceStore,
        AppDbContext dbContext,
        ModelConcurrencyLimiter concurrencyLimiter,
        ProxyRequestMetadataCache metadataCache)
    {
        _runtimeSettingsService = runtimeSettingsService;
        _traceStore = traceStore;
        _dbContext = dbContext;
        _concurrencyLimiter = concurrencyLimiter;
        // 调试页默认参数走内存缓存，避免每次打开都触发独立的数据库查询。
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 初始总记录数。
    /// </summary>
    public int InitialTotalCount { get; private set; }
    /// <summary>
    /// 初始失败记录数。
    /// </summary>
    public int InitialFailedCount { get; private set; }
    /// <summary>
    /// 初始等待记录数。
    /// </summary>
    public int InitialPendingCount { get; private set; }
    /// <summary>
    /// 当前激活页签。
    /// </summary>
    public string ActiveTab { get; private set; } = "invocations";
    /// <summary>
    /// 模型并发限制器，用于读取当前真实活跃并发快照。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    /// <summary>
    /// 默认请求地址。
    /// </summary>
    public string DefaultBaseUrl { get; private set; } = string.Empty;
    /// <summary>
    /// 默认访问密钥。
    /// </summary>
    public string DefaultAccessKey { get; private set; } = string.Empty;
    /// <summary>
    /// 默认 OpenAI 模型。
    /// </summary>
    public string DefaultOpenAiModel { get; private set; } = string.Empty;
    /// <summary>
    /// 默认 Anthropic 模型。
    /// </summary>
    public string DefaultAnthropicModel { get; private set; } = string.Empty;
    /// <summary>
    /// 模型列表。
    /// </summary>
    public List<ClientSimulatorModelItemViewModel> Models { get; private set; } = [];

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        ActiveTab = "invocations";

        var entries = _traceStore.List();
        InitialTotalCount = entries.Count;
        InitialFailedCount = entries.Count(x => x.Attempts.Any(a => !string.Equals(a.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase)));
        InitialPendingCount = entries.Count(x => x.Attempts.Any(a => string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase)));
        await LoadClientSimulatorAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// 返回调用记录列表。
    /// </summary>
    public async Task<IActionResult> OnGetListAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        var totalCount = entries.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
        // Razor Pages 会把 page 当作保留路由字段参与绑定，这里改用 pageNumber 避免翻页始终回到第一页。
        var currentPage = Math.Min(Math.Max(pageNumber, 1), totalPages);
        var pagedEntries = entries
            .Skip((currentPage - 1) * PageSize)
            .Take(PageSize)
            .Select(ToSummaryDto)
            .ToList();

        var payload = new DeveloperInvocationListResponse
        {
            TotalCount = totalCount,
            FailedCount = entries.Count(x => x.Attempts.Any(a => !string.Equals(a.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase))),
            PendingCount = entries.Count(x => x.Attempts.Any(a => string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase))),
            PageNumber = currentPage,
            PageSize = PageSize,
            TotalPages = totalPages,
            Entries = pagedEntries
        };
        return new JsonResult(payload);
    }

    /// <summary>
    /// 返回调用记录详情。
    /// </summary>
    public async Task<IActionResult> OnGetDetailAsync(Guid traceId, CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        var entry = _traceStore.Get(traceId);
        if (entry is null)
        {
            return NotFound();
        }

        return new JsonResult(ToDetailDto(entry));
    }

    /// <summary>
    /// 返回所有启用的站点模型映射及其实时并发、排队状态。
    /// </summary>
    public async Task<IActionResult> OnGetConcurrencyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        // 查询所有启用的站点模型映射，关联站点名
        var mappings = await (
            from m in _dbContext.SiteModelMappings.AsNoTracking()
            where m.IsEnabled
            join s in _dbContext.Sites.AsNoTracking() on m.SiteId equals s.Id into sites
            from s in sites.DefaultIfEmpty()
            select new
            {
                m.SiteId,
                SiteName = s != null ? s.Name : "-",
                ModelName = m.RemoteModelName,
                m.MaxConcurrency
            }
        ).ToListAsync(cancellationToken);

        // 获取内存中的实时并发快照
        var snapshots = _concurrencyLimiter.ListRecent(ModelConcurrencyLimiter.RecentRetention);
        var snapshotMap = snapshots
            .GroupBy(x => $"{x.SiteId:N}:{x.SiteModelName}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var items = mappings.Select(m =>
        {
            var key = $"{m.SiteId:N}:{m.ModelName}";
            var snap = snapshotMap.TryGetValue(key, out var s) ? s : null;
            return new DeveloperModelConcurrencyDto
            {
                ModelName = m.ModelName,
                SiteName = m.SiteName,
                ActiveCount = snap?.ActiveCount ?? 0,
                MaxConcurrency = m.MaxConcurrency > 0 ? m.MaxConcurrency : null,
                QueueCount = snap?.QueueCount ?? 0
            };
        })
        .OrderByDescending(x => x.QueueCount > 0 ? 1 : 0)
        .ThenByDescending(x => x.QueueCount)
        .ThenBy(x => x.SiteName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.ModelName, StringComparer.OrdinalIgnoreCase)
        .ToList();

        return new JsonResult(new DeveloperModelConcurrencyResponse
        {
            RefreshedAt = DateTimeOffset.Now,
            Items = items
        });
    }

    /// <summary>
    /// 转换为摘要数据。
    /// </summary>
    private static DeveloperInvocationTraceSummaryDto ToSummaryDto(DeveloperInvocationTraceEntry entry)
    {
        return new DeveloperInvocationTraceSummaryDto
        {
            TraceId = entry.TraceId,
            CreatedAt = entry.CreatedAt,
            CreatedAtText = entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            Source = entry.Source,
            ProtocolType = entry.ProtocolType,
            RequestPath = entry.RequestPath,
            RequestModel = entry.RequestModel,
            SummarySite = string.IsNullOrWhiteSpace(entry.TargetSiteName) ? "未命中站点" : entry.TargetSiteName,
            SummaryAttemptedModel = string.IsNullOrWhiteSpace(entry.AttemptedModel) ? "未解析调用模型" : entry.AttemptedModel,
            Status = entry.Status,
            StatusText = GetStatusText(entry.Status),
            StatusClass = GetStatusClass(entry.Status),
            StatusCode = entry.StatusCode,
            TotalDurationMs = entry.TotalDurationMs,
            FailedAttemptCount = entry.Attempts.Count(x => !string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            PendingAttemptCount = entry.Attempts.Count(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            SuccessAttemptCount = entry.Attempts.Count(x => string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase))
        };
    }

    /// <summary>
    /// 转换为详情数据。
    /// </summary>
    private static DeveloperInvocationTraceDto ToDetailDto(DeveloperInvocationTraceEntry entry)
    {
        return new DeveloperInvocationTraceDto
        {
            TraceId = entry.TraceId,
            RequestId = entry.RequestId,
            CreatedAt = entry.CreatedAt,
            CreatedAtText = entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = entry.UpdatedAt,
            UpdatedAtText = entry.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            Source = entry.Source,
            UserAgent = entry.UserAgent,
            ClientIp = entry.ClientIp,
            ProtocolType = entry.ProtocolType,
            UpstreamProtocolType = entry.UpstreamProtocolType,
            RequestPath = entry.RequestPath,
            RequestModel = entry.RequestModel,
            AttemptedModel = entry.AttemptedModel,
            TargetSiteId = entry.TargetSiteId,
            TargetSiteName = entry.TargetSiteName,
            RequestBody = entry.RequestBody,
            RequestHeaders = entry.RequestHeaders,
            Status = entry.Status,
            StatusText = GetStatusText(entry.Status),
            StatusClass = GetStatusClass(entry.Status),
            StatusCode = entry.StatusCode,
            ErrorMessage = entry.ErrorMessage,
            ResponseBody = entry.ResponseBody,
            ResponseContentType = entry.ResponseContentType,
            IsStreaming = entry.IsStreaming,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            TotalDurationMs = entry.TotalDurationMs,
            SummarySite = string.IsNullOrWhiteSpace(entry.TargetSiteName) ? "未命中站点" : entry.TargetSiteName,
            SummaryAttemptedModel = string.IsNullOrWhiteSpace(entry.AttemptedModel) ? "未解析调用模型" : entry.AttemptedModel,
            Attempts = entry.Attempts.Select(ToAttemptDto).ToList(),
            FailedAttemptCount = entry.Attempts.Count(x => !string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            PendingAttemptCount = entry.Attempts.Count(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            SuccessAttemptCount = entry.Attempts.Count(x => string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase))
        };
    }

    /// <summary>
    /// 转换为尝试详情数据。
    /// </summary>
    private static DeveloperInvocationTraceAttemptDto ToAttemptDto(DeveloperInvocationTraceAttempt attempt)
    {
        return new DeveloperInvocationTraceAttemptDto
        {
            AttemptId = attempt.AttemptId,
            CreatedAt = attempt.CreatedAt,
            CreatedAtText = attempt.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = attempt.UpdatedAt,
            UpdatedAtText = attempt.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            AttemptedModel = attempt.AttemptedModel,
            UpstreamProtocolType = attempt.UpstreamProtocolType,
            ForwardingMode = attempt.ForwardingMode,
            TargetSiteId = attempt.TargetSiteId,
            TargetSiteName = attempt.TargetSiteName,
            Status = attempt.Status,
            StatusText = GetStatusText(attempt.Status),
            StatusClass = GetStatusClass(attempt.Status),
            StatusCode = attempt.StatusCode,
            ErrorMessage = attempt.ErrorMessage,
            ResponseBody = attempt.ResponseBody,
            ResponseContentType = attempt.ResponseContentType,
            IsStreaming = attempt.IsStreaming,
            InputTokens = attempt.InputTokens,
            CachedTokens = attempt.CachedTokens,
            OutputTokens = attempt.OutputTokens,
            TotalDurationMs = attempt.TotalDurationMs,
            SummarySite = string.IsNullOrWhiteSpace(attempt.TargetSiteName) ? "未命中站点" : attempt.TargetSiteName,
            SummaryAttemptedModel = string.IsNullOrWhiteSpace(attempt.AttemptedModel) ? "未解析调用模型" : attempt.AttemptedModel
        };
    }

    /// <summary>
    /// 加载调试调用的默认参数。
    /// </summary>
    private async Task LoadClientSimulatorAsync(CancellationToken cancellationToken)
    {
        DefaultBaseUrl = $"{Request.Scheme}://{Request.Host}";

        // 默认密钥与调试模型清单走元数据缓存，5 秒内重复打开页面只查询一次数据库。
        DefaultAccessKey = await _metadataCache.GetDeveloperDefaultAccessKeyAsync(cancellationToken);

        var routeModels = await _metadataCache.GetDeveloperDebugModelsAsync(cancellationToken);

        Models = routeModels.ToList();
        DefaultOpenAiModel = routeModels.FirstOrDefault(x => x.CanUseOpenAi)?.ModelName ?? string.Empty;
        DefaultAnthropicModel = routeModels.FirstOrDefault(x => x.CanUseAnthropic)?.ModelName ?? string.Empty;
    }

    /// <summary>
    /// 返回状态样式。
    /// </summary>
    private static string GetStatusClass(string status)
    {
        return status?.ToLowerInvariant() switch
        {
            "success" => "success",
            "pending" => "pending",
            _ => "danger"
        };
    }

    /// <summary>
    /// 返回状态文本。
    /// </summary>
    private static string GetStatusText(string status)
    {
        return status?.ToLowerInvariant() switch
        {
            "success" => "成功",
            "pending" => "等待返回",
            "not-found" => "无可用路由",
            "all-failed" => "全部失败",
            "fail" => "失败",
            _ => string.IsNullOrWhiteSpace(status) ? "未知" : status
        };
    }
}

/// <summary>
/// 开发者调用列表响应。
/// </summary>
public sealed class DeveloperInvocationListResponse
{
    /// <summary>
    /// 调用记录总数。
    /// </summary>
    public int TotalCount { get; set; }
    /// <summary>
    /// 失败记录数。
    /// </summary>
    public int FailedCount { get; set; }
    /// <summary>
    /// 等待记录数。
    /// </summary>
    public int PendingCount { get; set; }
    /// <summary>
    /// 页码。
    /// </summary>
    public int PageNumber { get; set; }
    /// <summary>
    /// 每页记录数。
    /// </summary>
    public int PageSize { get; set; }
    /// <summary>
    /// 总页数。
    /// </summary>
    public int TotalPages { get; set; }
    /// <summary>
    /// 记录列表。
    /// </summary>
    public List<DeveloperInvocationTraceSummaryDto> Entries { get; set; } = [];
}

/// <summary>
/// 开发者调用摘要。
/// </summary>
public sealed class DeveloperInvocationTraceSummaryDto
{
    /// <summary>
    /// 跟踪标识。
    /// </summary>
    public Guid TraceId { get; set; }
    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>
    /// 格式化后的创建时间。
    /// </summary>
    public string CreatedAtText { get; set; } = string.Empty;
    /// <summary>
    /// 来源。
    /// </summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;
    /// <summary>
    /// 请求模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;
    /// <summary>
    /// 摘要中的站点名称。
    /// </summary>
    public string SummarySite { get; set; } = string.Empty;
    /// <summary>
    /// 摘要中的模型名称。
    /// </summary>
    public string SummaryAttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 状态显示文本。
    /// </summary>
    public string StatusText { get; set; } = string.Empty;
    /// <summary>
    /// 状态样式类名。
    /// </summary>
    public string StatusClass { get; set; } = string.Empty;
    /// <summary>
    /// 状态码。
    /// </summary>
    public int StatusCode { get; set; }
    /// <summary>
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
    /// <summary>
    /// 失败尝试次数。
    /// </summary>
    public int FailedAttemptCount { get; set; }
    /// <summary>
    /// 等待中的尝试次数。
    /// </summary>
    public int PendingAttemptCount { get; set; }
    /// <summary>
    /// 成功尝试次数。
    /// </summary>
    public int SuccessAttemptCount { get; set; }
}

/// <summary>
/// 开发者调用详情。
/// </summary>
public sealed class DeveloperInvocationTraceDto
{
    /// <summary>
    /// 跟踪标识。
    /// </summary>
    public Guid TraceId { get; set; }
    /// <summary>
    /// 请求标识。
    /// </summary>
    public Guid RequestId { get; set; }
    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>
    /// 格式化后的创建时间。
    /// </summary>
    public string CreatedAtText { get; set; } = string.Empty;
    /// <summary>
    /// 更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>
    /// 格式化后的更新时间。
    /// </summary>
    public string UpdatedAtText { get; set; } = string.Empty;
    /// <summary>
    /// 来源。
    /// </summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>
    /// 用户代理。
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;
    /// <summary>
    /// 客户端 IP。
    /// </summary>
    public string ClientIp { get; set; } = string.Empty;
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 上游协议类型。
    /// </summary>
    public string UpstreamProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;
    /// <summary>
    /// 请求模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;
    /// <summary>
    /// 尝试调用的模型。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 目标站点标识。
    /// </summary>
    public Guid? TargetSiteId { get; set; }
    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string TargetSiteName { get; set; } = string.Empty;
    /// <summary>
    /// 摘要中的站点名称。
    /// </summary>
    public string SummarySite { get; set; } = string.Empty;
    /// <summary>
    /// 摘要中的模型名称。
    /// </summary>
    public string SummaryAttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 请求体。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;
    /// <summary>
    /// 请求头。
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 状态显示文本。
    /// </summary>
    public string StatusText { get; set; } = string.Empty;
    /// <summary>
    /// 状态样式类名。
    /// </summary>
    public string StatusClass { get; set; } = string.Empty;
    /// <summary>
    /// 状态码。
    /// </summary>
    public int StatusCode { get; set; }
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
    /// <summary>
    /// 响应体。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;
    /// <summary>
    /// 响应内容类型。
    /// </summary>
    public string ResponseContentType { get; set; } = string.Empty;
    /// <summary>
    /// 是否为流式响应。
    /// </summary>
    public bool IsStreaming { get; set; }
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
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
    /// <summary>
    /// 失败尝试次数。
    /// </summary>
    public int FailedAttemptCount { get; set; }
    /// <summary>
    /// 等待中的尝试次数。
    /// </summary>
    public int PendingAttemptCount { get; set; }
    /// <summary>
    /// 成功尝试次数。
    /// </summary>
    public int SuccessAttemptCount { get; set; }
    /// <summary>
    /// 尝试记录列表。
    /// </summary>
    public List<DeveloperInvocationTraceAttemptDto> Attempts { get; set; } = [];
}

/// <summary>
/// 开发者调用尝试详情。
/// </summary>
public sealed class DeveloperInvocationTraceAttemptDto
{
    /// <summary>
    /// 尝试记录标识。
    /// </summary>
    public Guid AttemptId { get; set; }
    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>
    /// 格式化后的创建时间。
    /// </summary>
    public string CreatedAtText { get; set; } = string.Empty;
    /// <summary>
    /// 更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>
    /// 格式化后的更新时间。
    /// </summary>
    public string UpdatedAtText { get; set; } = string.Empty;
    /// <summary>
    /// 尝试调用的模型。
    /// </summary>
    public string AttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 上游协议类型。
    /// </summary>
    public string UpstreamProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 转发模式。
    /// </summary>
    public string ForwardingMode { get; set; } = string.Empty;
    /// <summary>
    /// 目标站点标识。
    /// </summary>
    public Guid? TargetSiteId { get; set; }
    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string TargetSiteName { get; set; } = string.Empty;
    /// <summary>
    /// 摘要中的站点名称。
    /// </summary>
    public string SummarySite { get; set; } = string.Empty;
    /// <summary>
    /// 摘要中的模型名称。
    /// </summary>
    public string SummaryAttemptedModel { get; set; } = string.Empty;
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 状态显示文本。
    /// </summary>
    public string StatusText { get; set; } = string.Empty;
    /// <summary>
    /// 状态样式类名。
    /// </summary>
    public string StatusClass { get; set; } = string.Empty;
    /// <summary>
    /// 状态码。
    /// </summary>
    public int StatusCode { get; set; }
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
    /// <summary>
    /// 响应体。
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;
    /// <summary>
    /// 响应内容类型。
    /// </summary>
    public string ResponseContentType { get; set; } = string.Empty;
    /// <summary>
    /// 是否为流式响应。
    /// </summary>
    public bool IsStreaming { get; set; }
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
    /// 总耗时（毫秒）。
    /// </summary>
    public int TotalDurationMs { get; set; }
}
