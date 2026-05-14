using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AITool.Web.Services;

namespace AITool.Web.Pages.Admin.Developer.Invocations;

// 调用调试页模型，按运行时开关展示内存中的最近调用信息。
public sealed class IndexModel : PageModel
{
    public const int PageSize = 20;

    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    private readonly DeveloperInvocationTraceStore _traceStore;

    public IndexModel(ISystemRuntimeSettingsService runtimeSettingsService, DeveloperInvocationTraceStore traceStore)
    {
        _runtimeSettingsService = runtimeSettingsService;
        _traceStore = traceStore;
    }

    public int InitialTotalCount { get; private set; }
    public int InitialFailedCount { get; private set; }
    public int InitialPendingCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        InitialTotalCount = entries.Count;
        InitialFailedCount = entries.Count(x => x.Attempts.Any(a => !string.Equals(a.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase)));
        InitialPendingCount = entries.Count(x => x.Attempts.Any(a => string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase)));
        return Page();
    }

    public async Task<IActionResult> OnGetListAsync(int page = 1, CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        var totalCount = entries.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
        var currentPage = Math.Min(Math.Max(page, 1), totalPages);
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

    // 列表阶段只返回摘要字段，避免一次性传输大块请求体、返回体和完整尝试链路。
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

    // 展开卡片后再单独返回完整详情，减少列表刷新和自动刷新时的无效负载。
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

    private static string GetStatusClass(string status)
    {
        return status?.ToLowerInvariant() switch
        {
            "success" => "success",
            "pending" => "pending",
            _ => "danger"
        };
    }

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

public sealed class DeveloperInvocationListResponse
{
    public int TotalCount { get; set; }
    public int FailedCount { get; set; }
    public int PendingCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public List<DeveloperInvocationTraceSummaryDto> Entries { get; set; } = [];
}

public sealed class DeveloperInvocationTraceSummaryDto
{
    public Guid TraceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedAtText { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string SummarySite { get; set; } = string.Empty;
    public string SummaryAttemptedModel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string StatusClass { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int TotalDurationMs { get; set; }
    public int FailedAttemptCount { get; set; }
    public int PendingAttemptCount { get; set; }
    public int SuccessAttemptCount { get; set; }
}

public sealed class DeveloperInvocationTraceDto
{
    public Guid TraceId { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedAtText { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedAtText { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string UpstreamProtocolType { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public Guid? TargetSiteId { get; set; }
    public string TargetSiteName { get; set; } = string.Empty;
    public string SummarySite { get; set; } = string.Empty;
    public string SummaryAttemptedModel { get; set; } = string.Empty;
    public string RequestBody { get; set; } = string.Empty;
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string StatusClass { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public string ResponseContentType { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalDurationMs { get; set; }
    public int FailedAttemptCount { get; set; }
    public int PendingAttemptCount { get; set; }
    public int SuccessAttemptCount { get; set; }
    public List<DeveloperInvocationTraceAttemptDto> Attempts { get; set; } = [];
}

public sealed class DeveloperInvocationTraceAttemptDto
{
    public Guid AttemptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedAtText { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedAtText { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string UpstreamProtocolType { get; set; } = string.Empty;
    public string ForwardingMode { get; set; } = string.Empty;
    public Guid? TargetSiteId { get; set; }
    public string TargetSiteName { get; set; } = string.Empty;
    public string SummarySite { get; set; } = string.Empty;
    public string SummaryAttemptedModel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string StatusClass { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public string ResponseContentType { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalDurationMs { get; set; }
}
