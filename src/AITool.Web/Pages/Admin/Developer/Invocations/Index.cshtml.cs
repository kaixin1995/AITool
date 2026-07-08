using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Web.Pages.Admin.ClientSimulator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
        // 不分页：一次性返回全部记录（最多 40 条，由 DeveloperInvocationTraceStore 上限控制）。
        var allEntries = entries.Select(ToSummaryDto).ToList();

        var payload = new DeveloperInvocationListResponse
        {
            TotalCount = totalCount,
            FailedCount = entries.Count(x => x.Attempts.Any(a => !string.Equals(a.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase))),
            PendingCount = entries.Count(x => x.Attempts.Any(a => string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase))),
            PageNumber = 1,
            PageSize = totalCount,
            TotalPages = 1,
            Entries = allEntries
        };
        return new JsonResult(payload);
    }

    /// <summary>
    /// 返回调用记录详情。
    /// </summary>
    public async Task<IActionResult> OnGetDetailAsync(Guid traceId, bool summarize = false, CancellationToken cancellationToken = default)
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

        return new JsonResult(ToDetailDto(entry, summarize));
    }

    /// <summary>
    /// 返回最近 6 小时内出现过的站点模型及其实时并发、排队状态。
    /// </summary>
    public async Task<IActionResult> OnGetConcurrencyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        var snapshots = _concurrencyLimiter.ListRecent(ModelConcurrencyLimiter.RecentRetention);
        if (snapshots.Count == 0)
        {
            return new JsonResult(new DeveloperModelConcurrencyResponse
            {
                RefreshedAt = DateTimeOffset.Now,
                Items = []
            });
        }

        // 站点名和最大并发都走元数据缓存，避免并发面板每次刷新都直接查数据库。
        var siteNames = await _metadataCache.GetEnabledSiteNamesAsync(cancellationToken);
        var mappingLimits = await _metadataCache.GetModelConcurrencyLimitsAsync(cancellationToken);

        var items = snapshots
            .Select(x =>
            {
                var key = $"{x.SiteId:N}:{x.SiteModelName}";
                return new DeveloperModelConcurrencyDto
                {
                    ModelName = x.SiteModelName,
                    SiteName = siteNames.TryGetValue(x.SiteId, out var siteName) ? siteName : "-",
                    ActiveCount = x.ActiveCount,
                    MaxConcurrency = mappingLimits.TryGetValue(key, out var maxConcurrency) && maxConcurrency > 0 ? maxConcurrency : null,
                    QueueCount = x.QueueCount
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
    private static DeveloperInvocationTraceDto ToDetailDto(DeveloperInvocationTraceEntry entry, bool summarize = false)
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
            RequestBody = summarize ? SummarizeJsonBody(entry.RequestBody) : entry.RequestBody,
            RequestHeaders = entry.RequestHeaders,
            Status = entry.Status,
            StatusText = GetStatusText(entry.Status),
            StatusClass = GetStatusClass(entry.Status),
            StatusCode = entry.StatusCode,
            ErrorMessage = entry.ErrorMessage,
            ResponseBody = summarize ? SummarizeJsonBody(entry.ResponseBody) : entry.ResponseBody,
            ResponseContentType = entry.ResponseContentType,
            IsStreaming = entry.IsStreaming,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            TotalDurationMs = entry.TotalDurationMs,
            SummarySite = string.IsNullOrWhiteSpace(entry.TargetSiteName) ? "未命中站点" : entry.TargetSiteName,
            SummaryAttemptedModel = string.IsNullOrWhiteSpace(entry.AttemptedModel) ? "未解析调用模型" : entry.AttemptedModel,
            Attempts = entry.Attempts.Select(a => ToAttemptDto(a, summarize)).ToList(),
            FailedAttemptCount = entry.Attempts.Count(x => !string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            PendingAttemptCount = entry.Attempts.Count(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)),
            SuccessAttemptCount = entry.Attempts.Count(x => string.Equals(x.Status, "success", StringComparison.OrdinalIgnoreCase))
        };
    }

    /// <summary>
    /// 转换为尝试详情数据。
    /// </summary>
    private static DeveloperInvocationTraceAttemptDto ToAttemptDto(DeveloperInvocationTraceAttempt attempt, bool summarize = false)
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
            PreparedRequestBody = summarize ? SummarizeJsonBody(attempt.PreparedRequestBody) : attempt.PreparedRequestBody,
            Status = attempt.Status,
            StatusText = GetStatusText(attempt.Status),
            StatusClass = GetStatusClass(attempt.Status),
            StatusCode = attempt.StatusCode,
            ErrorMessage = attempt.ErrorMessage,
            ResponseBody = summarize ? SummarizeJsonBody(attempt.ResponseBody) : attempt.ResponseBody,
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
    /// 对 JSON 请求/响应体做"结构保留、内容精简"摘要：仅截断超长字符串值（&gt;200 字符），
    /// 保留头 100 + 尾 20，中间标注省略字符数。字段名、嵌套层级、数组结构、数值、布尔、短字符串原样保留。
    /// 非 JSON（如 SSE 流）或解析失败时原样返回，不影响展示。
    /// </summary>
    private static string SummarizeJsonBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body ?? string.Empty;

        try
        {
            var node = JsonNode.Parse(body);
            if (node is null) return body;

            SummarizeNode(node);

            // 用带缩进的方式重新序列化，便于在页面上阅读（与原有 FormatBody 行为一致）。
            return node.ToJsonString(_summarizeOptions);
        }
        catch
        {
            return body;
        }
    }

    /// <summary>
    /// 摘要化用的 JSON 序列化选项：带缩进、不转义非 ASCII（中文直接显示，便于阅读）。
    /// </summary>
    private static readonly JsonSerializerOptions _summarizeOptions = new()
    {
        WriteIndented = true,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 触发摘要的字符串长度阈值（超过才截断）。
    /// </summary>
    private const int SummarizeThreshold = 200;
    /// <summary>
    /// 截断时保留的头部字符数。
    /// </summary>
    private const int SummarizeHeadKeep = 100;
    /// <summary>
    /// 截断时保留的尾部字符数。
    /// </summary>
    private const int SummarizeTailKeep = 20;

    /// <summary>
    /// 递归摘要化 JSON 节点：对象遍历每个属性值、数组遍历每个元素，遇到超长字符串值就截断。
    /// </summary>
    private static void SummarizeNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                // 复制成数组再改值，避免遍历时修改集合。
                foreach (var kvp in obj.ToArray())
                {
                    if (kvp.Value is JsonValue v && v.TryGetValue<string>(out var s) && s != null)
                    {
                        obj[kvp.Key] = SummarizeStringValue(s);
                    }
                    else if (kvp.Value is not null)
                    {
                        SummarizeNode(kvp.Value);
                    }
                }
                break;

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    if (item is JsonValue v && v.TryGetValue<string>(out var s) && s != null)
                    {
                        arr[i] = SummarizeStringValue(s);
                    }
                    else if (item is not null)
                    {
                        SummarizeNode(item);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// 截断单个字符串值：超阈值则保留头尾并标注省略字符数，否则原样返回。
    /// </summary>
    private static string SummarizeStringValue(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= SummarizeThreshold) return s;

        var head = s.Length > SummarizeHeadKeep ? s[..SummarizeHeadKeep] : s;
        var tail = s.Length > SummarizeTailKeep ? s[^SummarizeTailKeep..] : string.Empty;
        var omitted = s.Length - head.Length - tail.Length;
        return $"{head}…(省略{omitted}字符){tail}";
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
    /// 转换后实际发给上游的请求体（兼容中转场景的最终 payload），排查上游参数错误（如 GLM 1210）的关键。
    /// </summary>
    public string PreparedRequestBody { get; set; } = string.Empty;
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
