using AITool.Application.CoreRuntime;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

using AITool.Core.Services;
namespace AITool.Core.Controllers.Core;

/// <summary>
/// 开发者工具查询接口。
/// 提供 Admin 宿主所需的开发者调用追踪、模型并发检测、客户端模拟器元数据等运行时数据查询能力。
/// Core 宿主持有所有代理运行时的内存数据，Admin 通过此控制器间接读取。
/// </summary>
[ApiController]
[Route("api/core/developer")]
public sealed class CoreDeveloperQueryController : ControllerBase
{
    private readonly ModelConcurrencyQueryService _concurrencyQuery;
    private readonly AdminQueryMetadataService _metadataService;
    private readonly DeveloperInvocationTraceStore _traceStore;

    public CoreDeveloperQueryController(
        ModelConcurrencyQueryService concurrencyQuery,
        AdminQueryMetadataService metadataService,
        DeveloperInvocationTraceStore traceStore)
    {
        _concurrencyQuery = concurrencyQuery;
        _metadataService = metadataService;
        _traceStore = traceStore;
    }

    [HttpGet("invocations/list")]
    public async Task<IActionResult> ListInvocations(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
            return NotFound(new { message = "开发者功能未启用" });

        // 调试页取消分页：DeveloperInvocationTraceStore 已限制最多 40 条记录（MaxEntryCount），
        // 这里一次性返回全部，totalPages 始终为 1，前端单页全展示，不再出现分页栏。
        // 保留 pageNumber/pageSize 参数仅为接口兼容，pageSize 默认调高到 100 确保覆盖全部记录。
        pageSize = Math.Clamp(pageSize, 1, 100);
        var allEntries = _traceStore.List();
        var totalCount = allEntries.Count;
        var failedCount = allEntries.Count(e => e.Status is "failed" or "error");
        var pendingCount = allEntries.Count(e => e.Status is "pending" or "running");
        var totalPages = 1;
        var paged = allEntries.Select(ToSummary).ToList();

        return Ok(new CoreDeveloperInvocationListResponse
        {
            TotalCount = totalCount, FailedCount = failedCount, PendingCount = pendingCount,
            PageNumber = 1, PageSize = totalCount, TotalPages = totalPages, Entries = paged
        });
    }

    [HttpGet("invocations/detail")]
    public async Task<IActionResult> DetailInvocation(
        [FromQuery] Guid traceId, CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
            return NotFound(new { message = "开发者功能未启用" });

        var entry = _traceStore.Get(traceId);
        if (entry is null) return NotFound(new { message = $"跟踪记录 {traceId} 不存在或已过期" });
        return Ok(ToDetail(entry));
    }

    [HttpGet("concurrency")]
    public async Task<IActionResult> Concurrency(CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound(new { message = "开发者功能未启用" });
        }

        // 获取配置中的并发上限映射，用于补充 MaxConcurrency 字段。
        var concurrencyLimits = await _metadataService.GetModelConcurrencyLimitsAsync(cancellationToken);

        var entries = _concurrencyQuery.ListRecent(ModelConcurrencyQueryService.RecentRetention);
        var items = entries.Select(e =>
        {
            // MaxConcurrency 在 ActiveModelConcurrencyEntry 中为 0 表示不限制，
            // 在传输 DTO 中使用 null 表示不限制，保持语义一致。
            int? maxConcurrency = e.MaxConcurrency > 0 ? e.MaxConcurrency : null;
            return new CoreDeveloperConcurrencyItem
            {
                ModelName = e.SiteModelName,
                SiteName = string.Empty,
                ActiveCount = e.ActiveCount,
                MaxConcurrency = maxConcurrency,
                QueueCount = e.QueueCount
            };
        }).ToList();

        return Ok(new CoreDeveloperConcurrencyResponse
        {
            RefreshedAt = DateTimeOffset.UtcNow,
            Items = items
        });
    }

    /// <summary>
    /// 查询客户端模拟器所需的元数据。
    /// 返回默认访问密钥、默认模型名称和可用的调试模型列表。
    /// </summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("metadata")]
    public async Task<IActionResult> Metadata(CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound(new { message = "开发者功能未启用" });
        }

        var accessKey = await _metadataService.GetDeveloperDefaultAccessKeyAsync(cancellationToken);
        var models = await _metadataService.GetDeveloperDebugModelsAsync(cancellationToken);

        // 从模型列表中推断默认 OpenAI 和 Anthropic 模型名称。
        string defaultOpenAiModel = string.Empty;
        string defaultAnthropicModel = string.Empty;
        foreach (var m in models)
        {
            // 优先选择支持原生协议的模型作为默认值
            if (m.SupportsOpenAi && string.IsNullOrEmpty(defaultOpenAiModel))
            {
                defaultOpenAiModel = m.ModelName;
            }
            if (m.SupportsAnthropic && string.IsNullOrEmpty(defaultAnthropicModel))
            {
                defaultAnthropicModel = m.ModelName;
            }
            // 如果都找到了就提前退出
            if (!string.IsNullOrEmpty(defaultOpenAiModel) && !string.IsNullOrEmpty(defaultAnthropicModel))
            {
                break;
            }
        }

        return Ok(new CoreDeveloperMetadataResponse
        {
            DefaultAccessKey = accessKey,
            DefaultOpenAiModel = defaultOpenAiModel,
            DefaultAnthropicModel = defaultAnthropicModel,
            Models = models.Select(m => new CoreDeveloperModelItem
            {
                ModelName = m.ModelName,
                RouteCount = m.RouteCount,
                SupportsOpenAi = m.SupportsOpenAi,
                SupportsAnthropic = m.SupportsAnthropic,
                CanUseOpenAi = m.CanUseOpenAi,
                CanUseAnthropic = m.CanUseAnthropic
            }).ToList()
        });
    }

    /// <summary>
    /// 检查开发者功能是否启用。
    /// 读取运行时设置中的开发者功能开关，未启用时所有开发者端点返回 404。
    /// </summary>
    private async Task<bool> IsDeveloperEnabledAsync(CancellationToken cancellationToken)
    {
        var runtimeSettings = await _metadataService.GetRuntimeSettingsAsync(cancellationToken);
        return runtimeSettings.DeveloperFeaturesEnabled;
    }

    private static CoreDeveloperInvocationSummary ToSummary(DeveloperInvocationTraceEntry entry)
    {
        var attempts = entry.Attempts;
        return new CoreDeveloperInvocationSummary
        {
            TraceId = entry.TraceId, CreatedAt = entry.CreatedAt,
            CreatedAtText = entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            Source = entry.Source, ProtocolType = entry.ProtocolType, RequestPath = entry.RequestPath,
            RequestModel = entry.RequestModel, SummarySite = entry.TargetSiteName ?? "",
            SummaryAttemptedModel = entry.AttemptedModel ?? "",
            Status = entry.Status, StatusText = GetStatusText(entry.Status),
            StatusClass = GetStatusClass(entry.Status), StatusCode = entry.StatusCode,
            TotalDurationMs = entry.TotalDurationMs,
            FailedAttemptCount = attempts.Count(a => a.Status is "failed" or "error"),
            PendingAttemptCount = attempts.Count(a => a.Status is "pending" or "running"),
            SuccessAttemptCount = attempts.Count(a => a.Status == "success")
        };
    }

    private static CoreDeveloperInvocationDetail ToDetail(DeveloperInvocationTraceEntry entry)
    {
        return new CoreDeveloperInvocationDetail
        {
            TraceId = entry.TraceId, RequestId = entry.RequestId,
            CreatedAt = entry.CreatedAt, CreatedAtText = entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = entry.UpdatedAt, UpdatedAtText = entry.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            Source = entry.Source, UserAgent = entry.UserAgent, ClientIp = entry.ClientIp,
            ProtocolType = entry.ProtocolType, UpstreamProtocolType = entry.UpstreamProtocolType,
            RequestPath = entry.RequestPath, RequestModel = entry.RequestModel,
            AttemptedModel = entry.AttemptedModel, TargetSiteId = entry.TargetSiteId,
            TargetSiteName = entry.TargetSiteName, SummarySite = entry.TargetSiteName,
            SummaryAttemptedModel = entry.AttemptedModel,
            RequestBody = entry.RequestBody, RequestHeaders = entry.RequestHeaders,
            Status = entry.Status, StatusText = GetStatusText(entry.Status),
            StatusClass = GetStatusClass(entry.Status), StatusCode = entry.StatusCode,
            TotalDurationMs = entry.TotalDurationMs,
            InputTokens = entry.InputTokens, CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens, IsStreaming = entry.IsStreaming,
            ErrorMessage = entry.ErrorMessage,
            FailedAttemptCount = entry.Attempts.Count(a => a.Status is "failed" or "error"),
            PendingAttemptCount = entry.Attempts.Count(a => a.Status is "pending" or "running"),
            SuccessAttemptCount = entry.Attempts.Count(a => a.Status == "success"),
            Attempts = entry.Attempts.Select(a => new CoreDeveloperInvocationAttempt
            {
                AttemptId = a.AttemptId, CreatedAt = a.CreatedAt,
                CreatedAtText = a.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                UpstreamProtocolType = a.UpstreamProtocolType, ForwardingMode = a.ForwardingMode,
                AttemptedModel = a.AttemptedModel, TargetSiteName = a.TargetSiteName,
                SummarySite = a.TargetSiteName, SummaryAttemptedModel = a.AttemptedModel,
                Status = a.Status, StatusText = GetStatusText(a.Status),
                StatusClass = GetStatusClass(a.Status), StatusCode = a.StatusCode,
                TotalDurationMs = a.TotalDurationMs, InputTokens = a.InputTokens,
                CachedTokens = a.CachedTokens, OutputTokens = a.OutputTokens,
                ErrorMessage = a.ErrorMessage, ResponseBody = a.ResponseBody
            }).ToList()
        };
    }

    private static string GetStatusText(string status) => status switch
    {
        "success" => "成功", "fail" or "error" => "异常",
        "pending" or "running" => "等待中", _ => status
    };

    private static string GetStatusClass(string status) => status switch
    {
        "success" => "success", "fail" or "error" => "danger",
        "pending" or "running" => "warning", _ => "secondary"
    };
}
