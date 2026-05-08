using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AITool.Web.Services;

namespace AITool.Web.Pages.Admin.Developer.Invocations;

// 调用调试页模型，按运行时开关展示内存中的最近调用信息。
public sealed class IndexModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    private readonly DeveloperInvocationTraceStore _traceStore;

    public IndexModel(ISystemRuntimeSettingsService runtimeSettingsService, DeveloperInvocationTraceStore traceStore)
    {
        _runtimeSettingsService = runtimeSettingsService;
        _traceStore = traceStore;
    }

    public IReadOnlyList<DeveloperInvocationTraceEntry> Entries { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        Entries = _traceStore.List();
        return Page();
    }

    public async Task<IActionResult> OnGetListAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        var payload = new DeveloperInvocationListResponse
        {
            TotalCount = entries.Count,
            FailedCount = entries.Count(x => !string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            PendingCount = entries.Count(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            Entries = entries.Select(ToDto).ToList()
        };
        return new JsonResult(payload);
    }

    private static DeveloperInvocationTraceDto ToDto(DeveloperInvocationTraceEntry entry)
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
            SummaryAttemptedModel = string.IsNullOrWhiteSpace(entry.AttemptedModel) ? "未解析调用模型" : entry.AttemptedModel
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
    public List<DeveloperInvocationTraceDto> Entries { get; set; } = [];
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
}
