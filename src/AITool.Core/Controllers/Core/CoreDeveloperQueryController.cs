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
    /// <summary>
    /// 调用追踪只读查询服务。
    /// </summary>
    private readonly DeveloperInvocationTraceQueryService _traceQuery;

    /// <summary>
    /// 模型并发只读查询服务。
    /// </summary>
    private readonly ModelConcurrencyQueryService _concurrencyQuery;

    /// <summary>
    /// 后台查询元数据服务，提供默认密钥、模型列表等信息。
    /// </summary>
    private readonly AdminQueryMetadataService _metadataService;

    /// <summary>
    /// 初始化开发者查询控制器。
    /// </summary>
    public CoreDeveloperQueryController(
        DeveloperInvocationTraceQueryService traceQuery,
        ModelConcurrencyQueryService concurrencyQuery,
        AdminQueryMetadataService metadataService)
    {
        _traceQuery = traceQuery;
        _concurrencyQuery = concurrencyQuery;
        _metadataService = metadataService;
    }

    /// <summary>
    /// 分页查询开发者调用追踪列表。
    /// 返回最近 6 小时内的代理调用记录摘要，按创建时间倒序排列。
    /// </summary>
    /// <param name="pageNumber">页码（从 1 开始）。</param>
    /// <param name="pageSize">每页记录数。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("invocations/list")]
    public async Task<IActionResult> ListInvocations(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // 校验开发者功能开关，功能未启用时返回 404。
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound(new { message = "开发者功能未启用" });
        }

        // 限制每页最大记录数，防止过大分页请求拖慢响应。
        pageSize = Math.Clamp(pageSize, 1, 100);

        var allEntries = _traceQuery.List();
        var totalCount = allEntries.Count;
        var failedCount = allEntries.Count(e => e.Status is "failed" or "error");
        var pendingCount = allEntries.Count(e => e.Status is "pending" or "running");
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        // 分页切片，当前数据已按时间倒序排列。
        var paged = allEntries
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(ToSummary)
            .ToList();

        return Ok(new CoreDeveloperInvocationListResponse
        {
            TotalCount = totalCount,
            FailedCount = failedCount,
            PendingCount = pendingCount,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalPages = totalPages,
            Entries = paged
        });
    }

    /// <summary>
    /// 查询单条开发者调用追踪详情。
    /// </summary>
    /// <param name="traceId">跟踪标识。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("invocations/detail")]
    public async Task<IActionResult> DetailInvocation(
        [FromQuery] Guid traceId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound(new { message = "开发者功能未启用" });
        }

        var entry = _traceQuery.Get(traceId);
        if (entry is null)
        {
            return NotFound(new { message = $"跟踪记录 {traceId} 不存在或已过期" });
        }

        return Ok(ToDetail(entry));
    }

    /// <summary>
    /// 查询当前模型并发状态快照。
    /// 返回最近 6 小时内出现过的模型并发记录，包括活跃数、排队数和配置上限。
    /// </summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
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

    /// <summary>
    /// 将运行时调用追踪记录转换为摘要传输对象。
    /// </summary>
    private static CoreDeveloperInvocationSummary ToSummary(DeveloperInvocationTraceEntry entry)
    {
        var attempts = entry.Attempts;
        return new CoreDeveloperInvocationSummary
        {
            TraceId = entry.TraceId,
            CreatedAt = entry.CreatedAt,
            CreatedAtText = FormatTime(entry.CreatedAt),
            Source = entry.Source,
            ProtocolType = entry.ProtocolType,
            RequestPath = entry.RequestPath,
            RequestModel = entry.RequestModel,
            SummarySite = ComputeSummarySite(entry),
            SummaryAttemptedModel = ComputeSummaryAttemptedModel(entry),
            Status = entry.Status,
            StatusText = GetStatusText(entry.Status),
            StatusClass = GetStatusClass(entry.Status),
            StatusCode = entry.StatusCode,
            TotalDurationMs = entry.TotalDurationMs,
            FailedAttemptCount = attempts.Count(a => a.Status is "failed" or "error"),
            PendingAttemptCount = attempts.Count(a => a.Status is "pending" or "running"),
            SuccessAttemptCount = attempts.Count(a => a.Status == "success")
        };
    }

    /// <summary>
    /// 将运行时调用追踪记录转换为详情传输对象。
    /// </summary>
    private static CoreDeveloperInvocationDetail ToDetail(DeveloperInvocationTraceEntry entry)
    {
        var attempts = entry.Attempts;
        return new CoreDeveloperInvocationDetail
        {
            TraceId = entry.TraceId,
            RequestId = entry.RequestId,
            CreatedAt = entry.CreatedAt,
            CreatedAtText = FormatTime(entry.CreatedAt),
            UpdatedAt = entry.UpdatedAt,
            UpdatedAtText = FormatTime(entry.UpdatedAt),
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
            SummarySite = ComputeSummarySite(entry),
            SummaryAttemptedModel = ComputeSummaryAttemptedModel(entry),
            RequestBody = DeveloperInvocationTraceStore.FormatBody(entry.RequestBody),
            RequestHeaders = entry.RequestHeaders,
            Status = entry.Status,
            StatusText = GetStatusText(entry.Status),
            StatusClass = GetStatusClass(entry.Status),
            StatusCode = entry.StatusCode,
            ErrorMessage = entry.ErrorMessage,
            ResponseBody = DeveloperInvocationTraceStore.FormatBody(entry.ResponseBody),
            ResponseContentType = entry.ResponseContentType,
            IsStreaming = entry.IsStreaming,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            TotalDurationMs = entry.TotalDurationMs,
            FailedAttemptCount = attempts.Count(a => a.Status is "failed" or "error"),
            PendingAttemptCount = attempts.Count(a => a.Status is "pending" or "running"),
            SuccessAttemptCount = attempts.Count(a => a.Status == "success"),
            Attempts = attempts.Select(ToAttempt).ToList()
        };
    }

    /// <summary>
    /// 将运行时调用尝试记录转换为传输对象。
    /// </summary>
    private static CoreDeveloperInvocationAttempt ToAttempt(DeveloperInvocationTraceAttempt attempt)
    {
        return new CoreDeveloperInvocationAttempt
        {
            AttemptId = attempt.AttemptId,
            CreatedAt = attempt.CreatedAt,
            CreatedAtText = FormatTime(attempt.CreatedAt),
            UpdatedAt = attempt.UpdatedAt,
            UpdatedAtText = FormatTime(attempt.UpdatedAt),
            AttemptedModel = attempt.AttemptedModel,
            UpstreamProtocolType = attempt.UpstreamProtocolType,
            ForwardingMode = attempt.ForwardingMode,
            TargetSiteId = attempt.TargetSiteId,
            TargetSiteName = attempt.TargetSiteName,
            SummarySite = string.IsNullOrEmpty(attempt.TargetSiteName) ? "—" : attempt.TargetSiteName,
            SummaryAttemptedModel = string.IsNullOrEmpty(attempt.AttemptedModel) ? "—" : attempt.AttemptedModel,
            Status = attempt.Status,
            StatusText = GetStatusText(attempt.Status),
            StatusClass = GetStatusClass(attempt.Status),
            StatusCode = attempt.StatusCode,
            ErrorMessage = attempt.ErrorMessage,
            ResponseBody = DeveloperInvocationTraceStore.FormatBody(attempt.ResponseBody),
            ResponseContentType = attempt.ResponseContentType,
            IsStreaming = attempt.IsStreaming,
            InputTokens = attempt.InputTokens,
            CachedTokens = attempt.CachedTokens,
            OutputTokens = attempt.OutputTokens,
            TotalDurationMs = attempt.TotalDurationMs
        };
    }

    /// <summary>
    /// 计算摘要中显示的站点名称。
    /// 优先使用尝试记录中的站点名称，其次使用主记录的目标站点名称。
    /// </summary>
    private static string ComputeSummarySite(DeveloperInvocationTraceEntry entry)
    {
        // 从最后一个尝试中获取最新的站点信息
        var lastAttempt = entry.Attempts.LastOrDefault();
        var siteName = lastAttempt?.TargetSiteName ?? entry.TargetSiteName;
        return string.IsNullOrEmpty(siteName) ? "—" : siteName;
    }

    /// <summary>
    /// 计算摘要中显示的模型名称。
    /// 优先使用尝试记录中的模型名称，其次使用主记录的尝试调用模型。
    /// </summary>
    private static string ComputeSummaryAttemptedModel(DeveloperInvocationTraceEntry entry)
    {
        var lastAttempt = entry.Attempts.LastOrDefault();
        var modelName = lastAttempt?.AttemptedModel ?? entry.AttemptedModel;
        return string.IsNullOrEmpty(modelName) ? "—" : modelName;
    }

    /// <summary>
    /// 将时间偏移量格式化为本地时间字符串。
    /// </summary>
    private static string FormatTime(DateTimeOffset dateTime)
    {
        return dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// 获取状态对应的显示文本。
    /// </summary>
    private static string GetStatusText(string status)
    {
        return status switch
        {
            "pending" => "等待中",
            "running" => "处理中",
            "success" => "成功",
            "failed" => "失败",
            "error" => "异常",
            _ => status
        };
    }

    /// <summary>
    /// 获取状态对应的 CSS 样式类名。
    /// </summary>
    private static string GetStatusClass(string status)
    {
        return status switch
        {
            "pending" => "warning",
            "running" => "info",
            "success" => "success",
            "failed" => "danger",
            "error" => "danger",
            _ => "secondary"
        };
    }
}
