using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Proxy;
using AITool.Web.Contracts;
using AITool.Protocol;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 开发者调用追踪 API：查看近期代理请求的全链路详情 + 并发面板 + 熔断状态。
/// </summary>
[ApiController]
[Route("api/admin/developer/invocations")]
public sealed class DeveloperInvocationsApiController : ControllerBase
{
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    private readonly DeveloperInvocationTraceStore _traceStore;
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly AppDbContext _dbContext;
    private readonly IProxyForwardService _forwardService;

    public DeveloperInvocationsApiController(
        ISystemRuntimeSettingsService runtimeSettingsService,
        DeveloperInvocationTraceStore traceStore,
        ModelConcurrencyLimiter concurrencyLimiter,
        ProxyRequestMetadataCache metadataCache,
        RouteCircuitStateStore circuitStore,
        AppDbContext dbContext,
        IProxyForwardService forwardService)
    {
        _runtimeSettingsService = runtimeSettingsService;
        _traceStore = traceStore;
        _concurrencyLimiter = concurrencyLimiter;
        _metadataCache = metadataCache;
        _circuitStore = circuitStore;
        _dbContext = dbContext;
        _forwardService = forwardService;
    }

    /// <summary>
    /// 获取开发者调试初始信息（计数 + 默认调用参数 + 可调试模型清单）。
    /// </summary>
    [HttpGet("init")]
    public async Task<IActionResult> GetInit(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        var defaultAccessKey = await _metadataCache.GetDeveloperDefaultAccessKeyAsync(cancellationToken);
        var routeModels = await _metadataCache.GetDeveloperDebugModelsAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new
        {
            totalCount = entries.Count,
            failedCount = entries.Count(x => x.Attempts.Any(a => !IsSuccessOrPending(a.Status))),
            pendingCount = entries.Count(x => x.Attempts.Any(a => IsPending(a.Status))),
            defaultBaseUrl = $"{Request.Scheme}://{Request.Host}",
            defaultAccessKey,
            models = routeModels,
            defaultOpenAiModel = routeModels.FirstOrDefault(x => x.CanUseOpenAi)?.ModelName ?? string.Empty,
            defaultAnthropicModel = routeModels.FirstOrDefault(x => x.CanUseAnthropic)?.ModelName ?? string.Empty
        }));
    }

    /// <summary>
    /// 离线执行协议转换诊断，不调用真实上游或代理转发链路。
    /// </summary>
    [HttpPost("protocol-diagnostics")]
    public async Task<IActionResult> RunProtocolDiagnostics(
        [FromBody] ProtocolDiagnosticsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings is null || !settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        if (!TryValidateProtocolDiagnosticsRequest(request, out var validationError, out var errorCode))
        {
            return BadRequest(ApiResponse.Fail(validationError, errorCode));
        }

        try
        {
            var result = ConvertProtocolDiagnostics(request);

            // 转换失败也返回 200 + conversionFailed + failureReason，让前端展示具体原因而非笼统报错。
            return Ok(ApiResponse.Ok(new
            {
                direction = request.Direction,
                sourceProtocol = request.SourceProtocol,
                targetProtocol = request.TargetProtocol,
                streaming = request.Streaming,
                convertedPayload = result.Payload,
                eventCount = result.EventCount,
                completionDetected = result.CompletionDetected,
                conversionFailed = result.ConversionFailed,
                conversionPath = result.ConversionPath,
                failureReason = result.FailureReason,
                inputSummary = result.InputSummary,
                fieldMappings = result.FieldMappings.Select(m => new { source = m.Source, target = m.Target, note = m.Note }),
                missingFields = result.MissingFields,
                rulesApplied = result.RulesApplied,
                chain = new
                {
                    mode = result.Chain.Mode,
                    stages = result.Chain.Stages.Select(s => new
                    {
                        kind = s.Kind,
                        label = s.Label,
                        protocol = s.Protocol,
                        function = s.Function,
                        note = s.Note,
                        isBridge = s.IsBridge
                    }),
                    eventMappings = result.Chain.EventMappings.Select(e => new
                    {
                        sourceEvent = e.SourceEvent,
                        targetEvent = e.TargetEvent,
                        note = e.Note
                    })
                }
            }));
        }
        catch (JsonException)
        {
            return BadRequest(ApiResponse.Fail("payload 不是合法 JSON", "invalid_json"));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse.Fail($"协议转换失败：{ex.Message}", "conversion_failed"));
        }
    }

    /// <summary>
    /// 使用指定的站点模型对调用失败现场进行 AI 智能诊断。
    /// </summary>
    [HttpPost("ai-diagnose")]
    public async Task<IActionResult> RunAiDiagnosis(
        [FromBody] DeveloperAiDiagnoseRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings is null || !settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        if (request.ModelId == Guid.Empty)
        {
            return BadRequest(ApiResponse.Fail("请选择用于诊断的 AI 模型", "invalid_model"));
        }

        var model = await _metadataCache.GetEnabledModelAsync(request.ModelId, cancellationToken);
        if (model is null)
        {
            return Ok(ApiResponse.Ok(new DeveloperAiDiagnoseResponse
            {
                Success = false,
                Error = "所选诊断模型不存在或已禁用"
            }));
        }

        // 构造诊断提示词
        var prompt = BuildAiDiagnosisPrompt(request);

        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        var concurrencyMode = (ConcurrencyAcquireMode)runtimeSettings.ConcurrencyMode;
        var concurrencyQueueTimeout = TimeSpan.FromSeconds(runtimeSettings.ConcurrencyQueueTimeoutSeconds);

        CachedFallbackTarget? target = null;
        if (request.MappingId != Guid.Empty)
        {
            var targets = await _metadataCache.GetChatTargetsAsync(request.ModelId, cancellationToken);
            var selectedTarget = targets.FirstOrDefault(x => x.MappingId == request.MappingId);
            if (selectedTarget != null)
            {
                target = new CachedFallbackTarget
                {
                    ModelId = request.ModelId,
                    SiteId = selectedTarget.SiteId,
                    SiteKeyId = selectedTarget.SiteKeyId,
                    CircuitKey = selectedTarget.CircuitKey,
                    SiteName = selectedTarget.SiteName,
                    ProtocolType = selectedTarget.ProtocolType,
                    BaseUrl = selectedTarget.BaseUrl,
                    EndpointPathMode = selectedTarget.EndpointPathMode,
                    ApiKey = selectedTarget.ApiKey,
                    SiteModelName = selectedTarget.SiteModelName,
                    ExtraHeaders = selectedTarget.ExtraHeaders
                };
            }
        }

        if (target == null)
        {
            var allRoutes = await _metadataCache.GetRouteTargetsForModelAsync(model.ModelName, cancellationToken);
            var availableRoute = allRoutes.FirstOrDefault(r => !_circuitStore.IsBlocked(r.CircuitKey));
            if (availableRoute != null)
            {
                target = new CachedFallbackTarget
                {
                    ModelId = request.ModelId,
                    SiteId = availableRoute.SiteId,
                    SiteKeyId = availableRoute.SiteKeyId,
                    CircuitKey = availableRoute.CircuitKey,
                    SiteName = availableRoute.SiteName,
                    ProtocolType = availableRoute.ProtocolType,
                    BaseUrl = availableRoute.BaseUrl,
                    EndpointPathMode = availableRoute.EndpointPathMode,
                    ApiKey = availableRoute.ApiKey,
                    SiteModelName = availableRoute.SiteModelName,
                    ExtraHeaders = availableRoute.ExtraHeaders
                };
            }
        }

        if (target == null)
        {
            return Ok(ApiResponse.Ok(new DeveloperAiDiagnoseResponse
            {
                Success = false,
                Error = "没有可用的模型路由目标"
            }));
        }

        using var concurrencyHandle = await _concurrencyLimiter.AcquireAsync(
            HttpContext.RequestServices,
            target.SiteKeyId ?? target.SiteId,
            target.SiteModelName,
            concurrencyMode,
            concurrencyQueueTimeout,
            cancellationToken,
            displaySiteId: target.SiteId);

        if (!concurrencyHandle.Acquired)
        {
            return Ok(ApiResponse.Ok(new DeveloperAiDiagnoseResponse
            {
                Success = false,
                Error = "当前诊断模型无可用并发槽位"
            }));
        }

        var chatRequestBody = BuildAiDiagnosisChatRequestBody(
            target.ProtocolType,
            target.SiteModelName,
            prompt,
            request.EnableReasoning,
            request.ReasoningEffort);

        string preparedRequestBody = chatRequestBody;
        var isGeminiRoute = string.Equals(target.ProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(target.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            preparedRequestBody = ProxyProtocolBridge.NormalizeResponsesBody(chatRequestBody, ProxyProtocolBridge.IsCodexTarget(target.BaseUrl));
        }
        else if (isGeminiRoute)
        {
            preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "OpenAI", "Gemini", chatRequestBody, target.SiteModelName, false,
                null, target.BaseUrl, null, isPassthrough: false, isCompact: false,
                geminiProjectId: target.GoogleProjectId);
        }

        var forwardHeaders = Controllers.Proxy.OpenAiProxyController.MergeExtraHeaders(target.ExtraHeaders);
        if (isGeminiRoute)
        {
            Controllers.Proxy.OpenAiProxyController.ApplyGeminiForwardHeaders(
                forwardHeaders, target.SiteModelName, ProxyProtocolBridge.IsAntigravityTarget(target.BaseUrl));
        }

        var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = target.BaseUrl,
            TargetEndpointPathMode = target.EndpointPathMode,
            TargetApiKey = target.ApiKey,
            ProtocolType = target.ProtocolType,
            TargetModelName = target.SiteModelName,
            RequestBody = chatRequestBody,
            PreparedRequestBody = preparedRequestBody,
            EnableStreaming = false,
            RequestTimeoutSeconds = Math.Max(60, runtimeSettings.ProxyRequestTimeoutSeconds),
            RetryCount = 0,
            ForwardHeaders = forwardHeaders,
            TargetPath = isGeminiRoute
                ? "/v1internal:generateContent"
                : string.Equals(target.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                    ? SiteEndpointPathResolver.ResolvePath(target.EndpointPathMode, "responses")
                    : null
        }, cancellationToken);

        if (!forwardResult.Success)
        {
            return Ok(ApiResponse.Ok(new DeveloperAiDiagnoseResponse
            {
                Success = false,
                Error = $"诊断模型调用失败 (HTTP {forwardResult.StatusCode}): {forwardResult.ErrorMessage}"
            }));
        }

        var (rawContent, reasoning) = ExtractChatCompletionContent(forwardResult.ResponseBody, target.ProtocolType);
        var parsed = ParseAiDiagnosisOutput(rawContent);

        return Ok(ApiResponse.Ok(new DeveloperAiDiagnoseResponse
        {
            Success = true,
            Content = rawContent,
            Reasoning = reasoning,
            Summary = parsed.Summary,
            RootCause = parsed.RootCause,
            SuggestedAction = parsed.SuggestedAction,
            Rules = parsed.Rules
        }));
    }

    private static string BuildAiDiagnosisPrompt(DeveloperAiDiagnoseRequest req)
    {
        return $@"你是一个顶级 AI API 网关与跨协议转换专家。现在有一个发往上游 AI 站点的请求失败了（HTTP {req.StatusCode}），请深度诊断失败根因并给出具体的排查结论和修复方案。

### 【调用现场上下文】
- 客户端请求协议: {req.ClientProtocol}
- 客户端请求路径: {req.RequestPath}
- 目标对外模型: {req.RequestModel}
- 上游实际模型: {req.AttemptedModel}
- 上游目标站点: {req.TargetSiteName}
- 上游协议类型: {req.UpstreamProtocolType}
- 转发模式: {req.ForwardingMode}
- 上游响应 HTTP 状态码: {req.StatusCode}

### 【上游返回的真实错误原文】
```
{req.ErrorMessage}
```

### 【客户端原始请求体 (Client Body)】
```json
{req.OriginalRequestBody}
```

### 【网关转换后发往上游的实际请求体 (Upstream Prepared Body)】
```json
{req.PreparedRequestBody}
```

---

### 【诊断重点提示】
1. **上游参数或模型名校验 (INVALID_ARGUMENT / 400)**: 
   - 检查上游真实模型名与映射是否匹配。
   - 检查 tools 中的 parameters JSON Schema 是否缺少必要字段或 required/properties 不匹配。
   - 检查是否有不支持的参数（如 `temperature`、`reasoning_effort`、`max_tokens` 等）。
2. **思维链 / Reasoning 签名**: 检查 DeepSeek 等模型是否缺少 `reasoning_content`，或 Claude thinking block 签名是否断裂。
3. **网关兼容规则引擎能力**:
   - `strip`: 剔除不支持的字段（target: 字段路径，如 `reasoning_effort`）。
   - `rename`: 重命名顶层字段（from: 原名, to: 新名）。
   - `default`: 补充缺失默认值（key: 字段名, value: 默认值）。
   - `keep_reasoning`: 在 Anthropic ↔ OpenAI 转换时保留思维链。

---

### 【你的输出格式要求】
请先用中文给出详尽的分析（包含 **故障现象**、**根本原因** 和 **排查建议**），并在回答的最后，必须包含以下严格的 JSON 块：
```json
{{
  ""summary"": ""一句话总结核心问题（例如：上游 Antigravity 无法识别带 -high 后缀的模型名）"",
  ""rootCause"": ""详细的根本原因分析"",
  ""suggestedAction"": ""具体的修复或操作建议"",
  ""rules"": [
    {{
      ""op"": ""strip"",
      ""target"": ""reasoning_effort"",
      ""scope"": ""bridge""
    }}
  ]
}}
```
如果该问题不是通过网关兼容规则修复的（例如模型名不正确、API Key 无效等），`rules` 数组应设为 `[]`。";
    }

    private static string BuildAiDiagnosisChatRequestBody(string protocolType, string modelName, string message, bool enableReasoning, string reasoningEffort)
    {
        var effort = ChatSendRequest.ValidReasoningEfforts.Contains(reasoningEffort) ? reasoningEffort : "high";

        if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var anthropicPayload = new Dictionary<string, object?>
            {
                ["model"] = modelName,
                ["messages"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = message
                    }
                },
                ["stream"] = false,
                ["max_tokens"] = 4096
            };

            if (enableReasoning)
            {
                var budgetTokens = effort switch
                {
                    "low" => 1024,
                    "medium" => 4096,
                    _ => 8192
                };
                anthropicPayload["thinking"] = new Dictionary<string, object?>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = budgetTokens
                };
            }

            return JsonSerializer.Serialize(anthropicPayload);
        }

        var openAiPayload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = message
                }
            },
            ["stream"] = false,
            ["max_tokens"] = 4096
        };

        if (enableReasoning)
        {
            openAiPayload["reasoning_effort"] = effort;
        }

        var openAiBody = JsonSerializer.Serialize(openAiPayload);
        if (string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyProtocolBridge.ConvertChatRequestToResponses(openAiBody, modelName, false);
        }

        return openAiBody;
    }

    private static (string Content, string? Reasoning) ExtractChatCompletionContent(string responseBody, string protocolType)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return (string.Empty, null);

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                string? reasoning = null;
                if (root.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in contentArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp))
                        {
                            var type = typeProp.GetString();
                            if (type == "text" && item.TryGetProperty("text", out var textProp))
                            {
                                sb.Append(textProp.GetString());
                            }
                            else if (type == "thinking" && item.TryGetProperty("thinking", out var thinkingProp))
                            {
                                reasoning = thinkingProp.GetString();
                            }
                        }
                    }
                }
                return (sb.ToString(), reasoning);
            }
            else if (string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("output_text", out var outText))
                {
                    return (outText.GetString() ?? string.Empty, null);
                }
                if (root.TryGetProperty("output", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    string? reasoning = null;
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "message" &&
                            item.TryGetProperty("content", out var contentList) && contentList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in contentList.EnumerateArray())
                            {
                                if (c.TryGetProperty("type", out var ct) && ct.GetString() == "output_text" &&
                                    c.TryGetProperty("text", out var tp))
                                {
                                    sb.Append(tp.GetString());
                                }
                            }
                        }
                    }
                    return (sb.ToString(), reasoning);
                }
            }
            else if (string.Equals(protocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                string? reasoning = null;
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                {
                    var first = candidates.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in parts.EnumerateArray())
                        {
                            if (p.TryGetProperty("text", out var tp))
                            {
                                sb.Append(tp.GetString());
                            }
                            else if (p.TryGetProperty("thought", out var thProp))
                            {
                                reasoning = thProp.GetString();
                            }
                        }
                    }
                }
                return (sb.ToString(), reasoning);
            }

            // Standard OpenAI format
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var first = choices.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var msg))
                {
                    var text = msg.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    string? reasoning = null;
                    if (msg.TryGetProperty("reasoning_content", out var rc))
                    {
                        reasoning = rc.GetString();
                    }
                    return (text, reasoning);
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return (responseBody, null);
    }

    private static (string Summary, string RootCause, string SuggestedAction, List<CompatibilityRule> Rules) ParseAiDiagnosisOutput(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return (string.Empty, string.Empty, string.Empty, []);
        }

        try
        {
            // 尝试在 Markdown 中定位 JSON 块
            var startIndex = rawContent.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (startIndex >= 0)
            {
                startIndex += 7;
                var endIndex = rawContent.IndexOf("```", startIndex, StringComparison.Ordinal);
                var jsonStr = (endIndex > startIndex ? rawContent[startIndex..endIndex] : rawContent[startIndex..]).Trim();
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                var rootCause = root.TryGetProperty("rootCause", out var rc) ? rc.GetString() ?? "" : "";
                var suggestedAction = root.TryGetProperty("suggestedAction", out var sa) ? sa.GetString() ?? "" : "";
                var rules = new List<CompatibilityRule>();
                if (root.TryGetProperty("rules", out var rArray) && rArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rArray.EnumerateArray())
                    {
                        var rule = JsonSerializer.Deserialize<CompatibilityRule>(item.GetRawText());
                        if (rule != null)
                        {
                            rules.Add(rule);
                        }
                    }
                }
                return (summary, rootCause, suggestedAction, rules);
            }
        }
        catch
        {
            // fallback
        }

        return (string.Empty, string.Empty, string.Empty, []);
    }

    /// <summary>
    /// 获取调用记录列表（不分页，最多 40 条由 TraceStore 上限控制）。
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> GetList([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var entries = _traceStore.List();
        pageSize = Math.Clamp(pageSize, 1, 100);
        var totalPages = entries.Count == 0 ? 0 : (int)Math.Ceiling(entries.Count / (double)pageSize);
        var currentPage = totalPages == 0 ? 1 : Math.Min(Math.Max(1, page), totalPages);
        var summaries = entries.Skip((currentPage - 1) * pageSize).Take(pageSize).Select(e => new
        {
            traceId = e.TraceId,
            createdAt = e.CreatedAt,
            source = e.Source,
            protocolType = e.UpstreamProtocolType,
            requestPath = e.RequestPath,
            requestModel = e.RequestModel,
            targetSiteId = e.TargetSiteId,
            targetSiteName = e.TargetSiteName,
            attemptedModel = e.AttemptedModel,
            status = e.Status,
            statusCode = e.StatusCode,
            totalDurationMs = e.TotalDurationMs,
            attemptCount = e.Attempts.Count,
            successAttemptCount = e.Attempts.Count(a => IsSuccess(a.Status)),
            failedAttemptCount = e.Attempts.Count(a => !IsSuccess(a.Status) && !IsPending(a.Status)),
            pendingAttemptCount = e.Attempts.Count(a => IsPending(a.Status))
        }).ToList();

        return Ok(ApiResponse.Ok(new
        {
            page = currentPage,
            pageSize,
            totalPages,
            totalCount = entries.Count,
            failedCount = entries.Count(x => x.Attempts.Any(a => !IsSuccessOrPending(a.Status))),
            pendingCount = entries.Count(x => x.Attempts.Any(a => IsPending(a.Status))),
            entries = summaries
        }));
    }

    /// <summary>
    /// 获取单条调用记录详情（含请求/响应体、每次尝试详情）。
    /// </summary>
    /// <param name="traceId">追踪 Id。</param>
    /// <param name="summarize">true 时对超长 JSON 字符串做摘要（截断），减少传输量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("{traceId:guid}")]
    public async Task<IActionResult> GetDetail(Guid traceId, [FromQuery] bool summarize = false, CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var entry = _traceStore.Get(traceId);
        if (entry is null)
        {
            return NotFound(ApiResponse.Fail("调用记录不存在或已过期", "trace_not_found"));
        }

        return Ok(ApiResponse.Ok(new
        {
            traceId = entry.TraceId,
            requestId = entry.RequestId,
            createdAt = entry.CreatedAt,
            updatedAt = entry.UpdatedAt,
            source = entry.Source,
            userAgent = entry.UserAgent,
            clientIp = entry.ClientIp,
            protocolType = entry.UpstreamProtocolType,
            requestPath = entry.RequestPath,
            requestModel = entry.RequestModel,
            requestHeaders = entry.RequestHeaders,
            requestBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(entry.RequestBody) : entry.RequestBody,
            targetSiteId = entry.TargetSiteId,
            targetSiteName = entry.TargetSiteName,
            attemptedModel = entry.AttemptedModel,
            status = entry.Status,
            statusCode = entry.StatusCode,
            errorMessage = entry.ErrorMessage,
            responseBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(entry.ResponseBody) : entry.ResponseBody,
            responseContentType = entry.ResponseContentType,
            isStreaming = entry.IsStreaming,
            totalDurationMs = entry.TotalDurationMs,
            inputTokens = entry.InputTokens,
            cachedTokens = entry.CachedTokens,
            outputTokens = entry.OutputTokens,
            attempts = entry.Attempts.Select(a => new
            {
                attemptId = a.AttemptId,
                targetSiteId = a.TargetSiteId,
                targetSiteName = a.TargetSiteName,
                attemptedModel = a.AttemptedModel,
                forwardingMode = a.ForwardingMode,
                upstreamProtocolType = a.UpstreamProtocolType,
                status = a.Status,
                statusCode = a.StatusCode,
                errorMessage = a.ErrorMessage,
                preparedRequestBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(a.PreparedRequestBody) : a.PreparedRequestBody,
                responseBody = summarize ? DeveloperInvocationTraceStore.SummarizeBody(a.ResponseBody) : a.ResponseBody,
                responseContentType = a.ResponseContentType,
                isStreaming = a.IsStreaming,
                inputTokens = a.InputTokens,
                cachedTokens = a.CachedTokens,
                outputTokens = a.OutputTokens,
                totalDurationMs = a.TotalDurationMs
            }).ToList()
        }));
    }

    /// <summary>
    /// 获取并发面板快照（按站点+模型的活跃/排队数）。
    /// </summary>
    [HttpGet("concurrency")]
    public async Task<IActionResult> GetConcurrency(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var snapshots = _concurrencyLimiter.ListRecent(ModelConcurrencyLimiter.RecentRetention);
        if (snapshots.Count == 0)
        {
            return Ok(ApiResponse.Ok(new { refreshedAt = DateTimeOffset.Now, items = Array.Empty<object>() }));
        }

        var siteNames = await _metadataCache.GetEnabledSiteNamesAsync(cancellationToken);

        // x.SiteId 是真实站点 Id（displaySiteId），用于反查站点名；
        // x.MaxConcurrency 已由限制器的 EnrichWithStateInfo 从运行时 state 填充，无需再查缓存字典。
        var items = snapshots.Select(x =>
        {
            return new
            {
                siteId = x.SiteId,
                concurrencyKey = x.ConcurrencyKey,
                modelName = x.SiteModelName,
                siteName = siteNames.TryGetValue(x.SiteId, out var n) ? n : "-",
                activeCount = x.ActiveCount,
                maxConcurrency = x.MaxConcurrency > 0 ? (int?)x.MaxConcurrency : null,
                queueCount = x.QueueCount,
                lastSeenAt = x.LastSeenAt
            };
        })
        .OrderByDescending(x => x.queueCount > 0 ? 1 : 0)
        .ThenByDescending(x => x.queueCount)
        .ThenBy(x => x.siteName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.modelName, StringComparer.OrdinalIgnoreCase)
        .ToList<object>();

        return Ok(ApiResponse.Ok(new { refreshedAt = DateTimeOffset.Now, items }));
    }

    /// <summary>
    /// 获取当前所有熔断/失败计数中的路由状态。
    /// </summary>
    [HttpGet("circuit-breaker")]
    public async Task<IActionResult> GetCircuitBreakerStates(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var states = _circuitStore.GetAllCircuitStates();
        if (states.Count == 0)
        {
            return Ok(ApiResponse.Ok(new { routes = Array.Empty<object>() }));
        }

        // 熔断存储的 key 现在是 CircuitKey（多 Key 候选为合成 Guid，单 Key/兼容候选为 RouteId 本身）。
        // 从缓存层展开后的路由候选构建 CircuitKey → 候选信息 字典，正确匹配每条熔断状态。
        // 同一站点同一站点模型可能被多个模型入口引用（CircuitKey 按站点+模型派生会重复），
        // 取首条即可——同键候选的站点/模型信息相同，重复键若直接 ToDictionary 会抛异常打挂整个面板。
        var allTargets = await _metadataCache.GetAllRouteTargetsAsync(cancellationToken);
        var targetByCircuitKey = allTargets
            .GroupBy(x => x.CircuitKey)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<object>(states.Count);
        foreach (var pair in states)
        {
            var circuitKey = pair.Key;
            var state = pair.Value;
            // 匹配缓存候选；匹配不到（候选已被删除或缓存未刷新）时仅展示熔断状态本身。
            if (targetByCircuitKey.TryGetValue(circuitKey, out var target))
            {
                result.Add(new
                {
                    routeId = target.RouteId,
                    circuitKey,
                    entryName = target.ExternalModelName,
                    upstreamModelName = target.UpstreamModelName,
                    siteName = target.SiteName,
                    siteKeyId = target.SiteKeyId,
                    isBlocked = state.IsBlocked,
                    failureCount = state.FailureCount,
                    blockedUntil = state.BlockedUntil,
                    remainingSeconds = state.RemainingTime != null
                        ? Math.Max(0, (int)Math.Ceiling(state.RemainingTime.Value.TotalSeconds))
                        : (int?)null
                });
            }
            else
            {
                // 候选已不存在（站点/模型被移除或未进任何路由），熔断状态按"站点+模型"全局共享，
                // 仍展示状态以便手动解除；用熔断时记录的归属元数据兜底显示站点/模型名，而非"(候选已移除)"。
                result.Add(new
                {
                    routeId = Guid.Empty,
                    circuitKey,
                    entryName = state.Meta?.SiteModelName ?? "(候选已移除)",
                    upstreamModelName = string.Empty,
                    siteName = state.Meta?.SiteName ?? string.Empty,
                    siteKeyId = (Guid?)null,
                    isBlocked = state.IsBlocked,
                    failureCount = state.FailureCount,
                    blockedUntil = state.BlockedUntil,
                    remainingSeconds = state.RemainingTime != null
                        ? Math.Max(0, (int)Math.Ceiling(state.RemainingTime.Value.TotalSeconds))
                        : (int?)null
                });
            }
        }

        return Ok(ApiResponse.Ok(new { routes = result }));
    }

    /// <summary>
    /// 手动解除指定路由的熔断状态。
    /// 路径参数 circuitKey 为熔断身份键（多 Key 候选为合成 Guid，兼容候选为 RouteId）。
    /// </summary>
    [HttpPost("circuit-breaker/{circuitKey}/reset")]
    public async Task<IActionResult> ResetCircuitBreaker(Guid circuitKey, CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var removed = _circuitStore.Reset(circuitKey);
        return Ok(ApiResponse.Ok(new { circuitKey, reset = removed }, removed ? "已解除熔断" : "该路由未被熔断"));
    }

    /// <summary>
    /// 解除所有路由的熔断状态。
    /// </summary>
    [HttpPost("circuit-breaker/reset-all")]
    public async Task<IActionResult> ResetAllCircuitBreakers(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var count = _circuitStore.ResetAll();
        return Ok(ApiResponse.Ok(new { resetCount = count }, $"已解除 {count} 条路由的熔断"));
    }

    private static bool TryValidateProtocolDiagnosticsRequest(
        ProtocolDiagnosticsRequest request,
        out string error,
        out string errorCode)
    {
        error = string.Empty;
        errorCode = string.Empty;

        var direction = request.Direction.Trim();
        var sourceProtocol = request.SourceProtocol.Trim();
        var targetProtocol = request.TargetProtocol.Trim();
        if (!string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(direction, "response", StringComparison.OrdinalIgnoreCase))
        {
            error = "direction 只支持 request 或 response";
            errorCode = "invalid_direction";
            return false;
        }

        if (!IsSupportedProtocol(sourceProtocol) || !IsSupportedProtocol(targetProtocol))
        {
            error = "协议只支持 OpenAI、Anthropic、Responses 和 Gemini";
            errorCode = "invalid_protocol";
            return false;
        }

        // 模型名仅用于写入转换后的目标协议字段，与鉴权无关，允许任意值以自由测试协议转换。
        if (string.IsNullOrWhiteSpace(request.Payload))
        {
            error = "payload 不能为空";
            errorCode = "empty_payload";
            return false;
        }

        if (request.Payload.Length > 512 * 1024)
        {
            error = "payload 超过 512 KB 限制";
            errorCode = "payload_too_large";
            return false;
        }

        if (!request.Streaming)
        {
            try
            {
                using var document = JsonDocument.Parse(request.Payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "非流式 payload 必须是 JSON 对象";
                    errorCode = "invalid_json";
                    return false;
                }
            }
            catch (JsonException)
            {
                error = "payload 不是合法 JSON";
                errorCode = "invalid_json";
                return false;
            }

            return true;
        }

        if (string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "Responses", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.EventName))
        {
            error = "Anthropic 流式诊断需要 eventName";
            errorCode = "missing_event_name";
            return false;
        }

        if (!IsSupportedStreamingDirection(direction, sourceProtocol, targetProtocol))
        {
            error = "当前流式协议方向暂未提供离线状态转换";
            errorCode = "unsupported_stream_direction";
            return false;
        }

        // 同协议流式：事件原样透传，不解析内容，任何格式都接受。
        if (sourceProtocol.Equals(targetProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(sourceProtocol, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            if (!HasValidResponsesSsePayload(request.Payload))
            {
                error = "Responses 流式 payload 必须是完整 SSE 事件块，且 data 必须是 JSON 对象";
                errorCode = "invalid_stream_payload";
                return false;
            }

            return true;
        }

        // 上游 Anthropic 流式 → 客户端 OpenAI：整体转换（BuildOpenAiStreamingResponseFromAnthropic）
        // 解析完整 SSE 帧，因此要求 event: + data: 块结构。
        if (string.Equals(sourceProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase)
            && string.Equals(direction, "response", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            if (!HasValidResponsesSsePayload(request.Payload))
            {
                error = "Anthropic 流式 payload 必须是完整 SSE 事件块（event: + data:），data 必须是 JSON 对象";
                errorCode = "invalid_stream_payload";
                return false;
            }

            return true;
        }

        // 整体流式响应转换（上游 OpenAI → 客户端 Anthropic）接受 SSE 全文或裸 JSON（转换器内部有 fallback）；
        // 其余 OpenAI 源的逐事件转换器只接受 data 后的原始 JSON。
        if (string.Equals(sourceProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(direction, "response", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(targetProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase))
            && (request.Payload.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || request.Payload.Contains("event:", StringComparison.OrdinalIgnoreCase)))
        {
            error = "OpenAI 流式 payload 只接受 data 后的原始 JSON";
            errorCode = "invalid_stream_payload";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(request.Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "流式 payload 必须是单个 JSON 对象";
                errorCode = "invalid_stream_payload";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "流式 payload 不是合法 JSON";
            errorCode = "invalid_stream_payload";
            return false;
        }

        return true;
    }

    private static ProtocolDiagnosticsConversionResult ConvertProtocolDiagnostics(ProtocolDiagnosticsRequest request)
    {
        var direction = request.Direction.Trim();
        var sourceProtocol = request.SourceProtocol.Trim();
        var targetProtocol = request.TargetProtocol.Trim();
        var conversionPath = BuildConversionPath(direction, sourceProtocol, targetProtocol, request.Streaming);
        var inputSummary = ExtractInputSummary(direction, sourceProtocol, request.Streaming, request.Payload, request.EventName);
        var fieldMappings = GetFieldMappings(direction, sourceProtocol, targetProtocol);
        var missingFields = GetMissingFields(direction, sourceProtocol, request.Streaming, request.Payload);
        // 链路与"方向"无关：请求方向 source=客户端/target=上游，响应方向相反，统一还原为完整双向链路。
        var clientProtocol = direction.Equals("request", StringComparison.OrdinalIgnoreCase) ? sourceProtocol : targetProtocol;
        var upstreamProtocol = direction.Equals("request", StringComparison.OrdinalIgnoreCase) ? targetProtocol : sourceProtocol;
        var chain = BuildChain(clientProtocol, upstreamProtocol, request.Streaming);
        var rulesApplied = false;

        try
        {
            string convertedPayload;
            string? failureReason = null;
            var completionDetected = false;

            if (!request.Streaming)
            {
                convertedPayload = string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase)
                    ? ProxyProtocolBridge.PrepareRequestBody(
                        sourceProtocol,
                        targetProtocol,
                        request.Payload,
                        request.ModelName.Trim(),
                        false,
                        request.OverrideReasoningEffort)
                    : ProxyProtocolBridge.AdaptResponseBodyForClient(
                        targetProtocol,
                        sourceProtocol,
                        request.Payload,
                        false,
                        request.ModelName.Trim(),
                        request.InputTokens,
                        request.CachedTokens,
                        request.OutputTokens);

                if (string.IsNullOrWhiteSpace(convertedPayload))
                {
                    failureReason = "转换结果为空，请检查输入协议与字段结构是否符合源协议要求";
                }
                else if (direction.Equals("request", StringComparison.OrdinalIgnoreCase)
                         && request.Rules is { Count: > 0 })
                {
                    // 试运行规则：仅请求方向（兼容规则本就是请求体规则），转换完成后按真实链路的
                    // 顺序应用（scope 按透传/兼容筛选）；响应方向传了 rules 也忽略。
                    var isPassthrough = sourceProtocol.Equals(targetProtocol, StringComparison.OrdinalIgnoreCase);
                    convertedPayload = ProxyProtocolBridge.ApplyCompatibilityProfile(
                        convertedPayload, request.Rules, isPassthrough);
                    rulesApplied = true;
                }
            }
            else if (string.Equals(direction, "request", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                    && targetProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                {
                    var chatState = new ChatToResponsesStreamState();
                    convertedPayload = ProxyProtocolBridge.ConvertChatStreamChunkToResponses(request.Payload, chatState);
                    completionDetected = chatState.Done;
                    failureReason = chatState.ConversionFailed
                        ? BuildStreamFailureReason("Chat→Responses") : null;
                }
                else if (sourceProtocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                         && targetProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                {
                    var chatState = new ChatToResponsesStreamState();
                    convertedPayload = ProxyProtocolBridge.ConvertAnthropicStreamChunkToResponses(
                        request.EventName!.Trim(), request.Payload, chatState);
                    completionDetected = chatState.Done;
                    failureReason = chatState.ConversionFailed
                        ? BuildStreamFailureReason("Anthropic→Responses") : null;
                }
                else if (sourceProtocol.Equals(targetProtocol, StringComparison.OrdinalIgnoreCase))
                {
                    // 同协议流式：无逐事件转换器，事件原样透传。
                    convertedPayload = request.Payload;
                    completionDetected = convertedPayload.Contains("[DONE]", StringComparison.OrdinalIgnoreCase)
                        || convertedPayload.Contains("message_stop", StringComparison.OrdinalIgnoreCase);
                    failureReason = null;
                }
                else
                {
                    var anthropicState = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
                    convertedPayload = ProxyProtocolBridge.BuildAnthropicStreamStart(request.ModelName.Trim(), anthropicState)
                        + ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(request.Payload, anthropicState);
                    if (request.Payload.Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                    {
                        convertedPayload += ProxyProtocolBridge.CompleteAnthropicStream(anthropicState);
                    }

                    completionDetected = convertedPayload.Contains("event: message_stop", StringComparison.OrdinalIgnoreCase);
                    failureReason = anthropicState.ConversionFailed
                        ? BuildStreamFailureReason("OpenAI→Anthropic") : null;
                }
            }
            else if (sourceProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                     && targetProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                var responsesState = new ResponsesToChatStreamState
                {
                    Model = request.ModelName.Trim(),
                    InputTokens = request.InputTokens,
                    CachedTokens = request.CachedTokens,
                    OutputTokens = request.OutputTokens
                };
                convertedPayload = ProxyProtocolBridge.ConvertResponsesStreamingToChat(request.Payload, responsesState);
                completionDetected = responsesState.Completed;
                failureReason = responsesState.ConversionFailed
                    ? BuildStreamFailureReason("Responses→Chat") : null;
            }
            else if (sourceProtocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                     && targetProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                // 上游 Anthropic 流式 → 客户端 OpenAI：走 AdaptResponseBodyForClient 的
                // BuildOpenAiStreamingResponseFromAnthropic（整体转换，非逐事件状态机）。
                convertedPayload = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "OpenAI", "Anthropic", request.Payload, true,
                    request.ModelName.Trim(), request.InputTokens, request.CachedTokens, request.OutputTokens);
                completionDetected = convertedPayload.Contains("[DONE]", StringComparison.OrdinalIgnoreCase)
                    || convertedPayload.Contains("finish_reason", StringComparison.OrdinalIgnoreCase);
                failureReason = string.IsNullOrWhiteSpace(convertedPayload)
                    ? "转换结果为空，请检查输入协议与字段结构是否符合源协议要求" : null;
            }
            else if (sourceProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                     && targetProtocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                // 上游 Responses 流式 → 客户端 Anthropic：两级转换 Responses→Chat→Anthropic。
                var responsesState = new ResponsesToChatStreamState
                {
                    Model = request.ModelName.Trim(),
                    InputTokens = request.InputTokens,
                    CachedTokens = request.CachedTokens,
                    OutputTokens = request.OutputTokens
                };
                var openAiPayload = ProxyProtocolBridge.ConvertResponsesStreamingToChat(request.Payload, responsesState);
                convertedPayload = ProxyProtocolBridge.AdaptResponseBodyForClient(
                    "Anthropic", "OpenAI", openAiPayload, true,
                    request.ModelName.Trim(), request.InputTokens, request.CachedTokens, request.OutputTokens);
                completionDetected = responsesState.Completed;
                failureReason = responsesState.ConversionFailed
                    ? BuildStreamFailureReason("Responses→Chat") : null;
            }
            else if (sourceProtocol.Equals(targetProtocol, StringComparison.OrdinalIgnoreCase))
            {
                // 同协议流式：事件原样透传。
                convertedPayload = request.Payload;
                completionDetected = convertedPayload.Contains("[DONE]", StringComparison.OrdinalIgnoreCase)
                    || convertedPayload.Contains("message_stop", StringComparison.OrdinalIgnoreCase);
                failureReason = null;
            }
            else
            {
                var anthropicState = new ProxyProtocolBridge.AnthropicOpenAiStreamState();
                convertedPayload = ProxyProtocolBridge.BuildAnthropicStreamStart(request.ModelName.Trim(), anthropicState)
                    + ProxyProtocolBridge.ConvertOpenAiStreamChunkToAnthropic(request.Payload, anthropicState);
                if (request.Payload.Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    convertedPayload += ProxyProtocolBridge.CompleteAnthropicStream(anthropicState);
                }

                completionDetected = convertedPayload.Contains("event: message_stop", StringComparison.OrdinalIgnoreCase);
                failureReason = anthropicState.ConversionFailed
                    ? BuildStreamFailureReason("OpenAI→Anthropic") : null;
            }

            return new ProtocolDiagnosticsConversionResult(
                convertedPayload,
                CountSseEvents(convertedPayload),
                failureReason is not null,
                completionDetected,
                conversionPath,
                failureReason,
                inputSummary,
                fieldMappings,
                missingFields,
                chain,
                rulesApplied);
        }
        catch (Exception ex)
        {
            // 转换内部抛出的异常（如字段类型不符）也返回具体原因，方便定位。
            return new ProtocolDiagnosticsConversionResult(
                string.Empty,
                0,
                true,
                false,
                conversionPath,
                ex.Message,
                inputSummary,
                fieldMappings,
                missingFields,
                chain,
                rulesApplied);
        }
    }

    /// <summary>
    /// 构建完整的转换链路（客户端 → 网关 → 上游 → 网关 → 客户端），
    /// 展示每个环节是透传还是兼容转换、走的哪个转换函数、流式事件如何映射。
    /// </summary>
    /// <param name="clientProtocol">客户端（入口）协议。</param>
    /// <param name="upstreamProtocol">上游站点协议。</param>
    /// <param name="streaming">是否流式。</param>
    private static ProtocolChainInfo BuildChain(string clientProtocol, string upstreamProtocol, bool streaming)
    {
        var isDirect = string.Equals(clientProtocol, upstreamProtocol, StringComparison.OrdinalIgnoreCase);
        var stages = new List<ProtocolChainStage>
        {
            new("client-request", "客户端请求", clientProtocol, null,
                "客户端原始请求体", false),
            new("transform", "请求转换", $"{clientProtocol} → {upstreamProtocol}",
                BuildRequestConversionFunction(clientProtocol, upstreamProtocol),
                isDirect
                    ? "同协议透传：仅替换 model / 校正 stream 与 store 字段"
                    : "跨协议转换：请求体整体改写为目标协议",
                !isDirect),
            new("upstream", "上游处理", upstreamProtocol, null,
                "站点按上游协议处理", false),
            new("transform-response", "响应转换", $"{upstreamProtocol} → {clientProtocol}",
                BuildResponseConversionFunction(clientProtocol, upstreamProtocol, streaming),
                BuildResponseConversionNote(clientProtocol, upstreamProtocol, streaming),
                !isDirect),
            new("client-response", "客户端响应", clientProtocol, null,
                "客户端按入口协议接收", false)
        };

        var eventMappings = streaming && !isDirect
            ? BuildEventMappings(clientProtocol, upstreamProtocol)
            : [];

        return new ProtocolChainInfo(isDirect ? "direct" : "bridge", stages, eventMappings);
    }

    private static string? BuildRequestConversionFunction(string client, string upstream)
    {
        if (string.Equals(client, upstream, StringComparison.OrdinalIgnoreCase))
        {
            return client.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                ? "ReplaceOpenAiModelAndEnsureStreamUsage"
                : "ReplaceModelName";
        }

        if (upstream.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? "BuildGeminiInnerFromAnthropic + WrapGeminiUpstreamBody"
                : "BuildGeminiInnerFromOpenAi + WrapGeminiUpstreamBody";
        }

        if (client.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Responses", StringComparison.OrdinalIgnoreCase))
        {
            return "ConvertChatRequestToResponses";
        }

        if (client.Equals("Responses", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return "ConvertResponsesRequestToChat";
        }

        if (client.Equals("Responses", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return "BuildAnthropicRequestFromResponses";
        }

        if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Responses", StringComparison.OrdinalIgnoreCase))
        {
            return "BuildResponsesRequestFromAnthropic";
        }

        if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return "BuildOpenAiRequestFromAnthropic";
        }

        return "BuildAnthropicRequestFromOpenAi";
    }

    private static string? BuildResponseConversionFunction(string client, string upstream, bool streaming)
    {
        if (string.Equals(client, upstream, StringComparison.OrdinalIgnoreCase))
        {
            return "直接透传";
        }

        if (upstream.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return streaming ? "ConvertGeminiSseChunkToAnthropic" : "ConvertGeminiResponseToAnthropic";
            }
            return streaming ? "ConvertGeminiSseChunkToOpenAi" : "ConvertGeminiResponseToOpenAi";
        }

        if (client.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Responses", StringComparison.OrdinalIgnoreCase))
        {
            return streaming ? "ConvertResponsesStreamingToChat" : "ConvertResponsesResponseToChat";
        }

        if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Responses", StringComparison.OrdinalIgnoreCase))
        {
            return streaming
                ? "BuildAnthropicStreamFromResponses"
                : "BuildAnthropicResponseFromResponses";
        }

        if (client.Equals("Responses", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return streaming
                ? "BuildResponsesStreamFromAnthropic"
                : "BuildResponsesResponseFromAnthropic";
        }

        if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return streaming ? "BuildAnthropicStreamingResponseFromOpenAi" : "BuildAnthropicResponseFromOpenAi";
        }

        if (client.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return streaming ? "BuildOpenAiStreamingResponseFromAnthropic" : "BuildOpenAiResponseFromAnthropic";
        }

        return streaming ? "BuildOpenAiStreamingResponseFromAnthropic（默认兜底路径 ⚠️）" : "BuildOpenAiResponseFromAnthropic（默认兜底路径 ⚠️）";
    }

    private static string? BuildResponseConversionNote(string client, string upstream, bool streaming)
    {
        if (string.Equals(client, upstream, StringComparison.OrdinalIgnoreCase))
        {
            return "同协议透传：上游响应原样返回客户端";
        }

        if (client.Equals("Responses", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return "Anthropic → Responses 直转：thinking 签名经 encrypted_content 桥接载体保留";
        }

        return streaming
            ? "流式：逐事件转换（下方为事件对应关系）"
            : "非流式：响应体整体改写为客户端协议";
    }

    /// <summary>
    /// 流式响应转换的事件级映射表（源事件 → 目标事件）。
    /// </summary>
    private static IReadOnlyList<ProtocolEventMapping> BuildEventMappings(string client, string upstream)
    {
        if (client.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Responses", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("response.created", "chat.completion.chunk", "首块：role=assistant"),
                new("response.output_text.delta", "chat.completion.chunk", "delta.content"),
                new("response.reasoning_summary_text.delta", "chat.completion.chunk", "delta.reasoning_content"),
                new("response.function_call_arguments.delta", "chat.completion.chunk", "delta.tool_calls"),
                new("response.completed", "chat.completion.chunk + [DONE]", "finish_reason + usage")
            ];
        }

        if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Responses", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("response.created", "message_start", "会话起始"),
                new("response.output_item.added", "content_block_start", "text → 文本块；function_call → tool_use"),
                new("response.output_text.delta", "content_block_delta", "delta.type=text_delta"),
                new("response.reasoning_summary_text.delta", "content_block_delta", "delta.type=thinking_delta"),
                new("response.function_call_arguments.delta", "content_block_delta", "delta.type=input_json_delta"),
                new("response.completed", "message_delta + message_stop", "stop_reason + 结束")
            ];
        }

        if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("chat.completion.chunk（首块）", "message_start", "会话起始 + usage"),
                new("chat.completion.chunk（delta.content）", "content_block_delta", "delta.type=text_delta"),
                new("chat.completion.chunk（delta.reasoning_content）", "content_block_delta", "delta.type=thinking_delta"),
                new("chat.completion.chunk（delta.tool_calls）", "content_block_start + content_block_delta", "tool_use + input_json_delta"),
                new("[DONE]", "message_delta + message_stop", "stop_reason + 结束")
            ];
        }

        if (client.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("message_start", "chat.completion.chunk", "首块：role=assistant"),
                new("content_block_delta（text_delta）", "chat.completion.chunk", "delta.content"),
                new("content_block_delta（thinking_delta）", "chat.completion.chunk", "delta.reasoning_content"),
                new("content_block_start + content_block_delta（input_json_delta）", "chat.completion.chunk", "delta.tool_calls"),
                new("message_delta", "chat.completion.chunk", "finish_reason"),
                new("message_stop", "[DONE]", "流结束")
            ];
        }

        if (client.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("generateContent (parts.text)", "chat.completion.chunk", "delta.content"),
                new("generateContent (parts.thought)", "chat.completion.chunk", "delta.reasoning_content (深度思考)"),
                new("generateContent (parts.functionCall)", "chat.completion.chunk", "delta.tool_calls"),
                new("generateContent (usageMetadata)", "chat.completion.chunk", "usage (promptTokenCount / candidatesTokenCount)"),
                new("generateContent (finishReason)", "chat.completion.chunk + [DONE]", "finish_reason (STOP / MAX_TOKENS / TOOL_CALLS)")
            ];
        }

        if (client.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            && upstream.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("generateContent (parts.text)", "content_block_delta", "delta.type=text_delta"),
                new("generateContent (parts.thought)", "content_block_delta", "delta.type=thinking_delta"),
                new("generateContent (parts.functionCall)", "content_block_start + content_block_delta", "tool_use + input_json_delta"),
                new("generateContent (usageMetadata)", "message_delta", "usage (input_tokens / output_tokens)"),
                new("generateContent (finishReason)", "message_delta + message_stop", "stop_reason + 结束")
            ];
        }

        return [];
    }

    private static string BuildConversionPath(string direction, string source, string target, bool streaming)
    {
        if (streaming)
        {
            if (direction.Equals("request", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) && target.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                {
                    return "ConvertChatStreamChunkToResponses";
                }

                if (source.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) && target.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                {
                    return "ConvertAnthropicStreamChunkToResponses";
                }

                return "ConvertOpenAiStreamChunkToAnthropic (+ BuildAnthropicStreamStart / CompleteAnthropicStream)";
            }

            return source.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                ? "ConvertResponsesStreamingToChat"
                : "ConvertOpenAiStreamChunkToAnthropic (+ BuildAnthropicStreamStart / CompleteAnthropicStream)";
        }

        return direction.Equals("request", StringComparison.OrdinalIgnoreCase)
            ? "PrepareRequestBody"
            : "AdaptResponseBodyForClient";
    }

    private static string BuildStreamFailureReason(string conversion)
        => $"流式转换失败（{conversion}）：事件类型或字段结构与目标协议不匹配，请对照下方的字段映射检查输入片段";

    /// <summary>
    /// 从输入 payload 中提取关键字段摘要，帮助用户一眼看出"网关识别到了什么"。
    /// </summary>
    private static JsonObject? ExtractInputSummary(
        string direction,
        string sourceProtocol,
        bool streaming,
        string payload,
        string? eventName)
    {
        try
        {
            var summary = new JsonObject();
            string? dataJson = payload;

            if (streaming)
            {
                // 流式输入优先提取事件名（完整 SSE 块或原始 JSON 都兼容）
                if (!string.IsNullOrWhiteSpace(eventName))
                {
                    summary["事件"] = eventName!.Trim();
                }
                else
                {
                    var eventLine = payload.Split('\n')
                        .FirstOrDefault(line => line.StartsWith("event:", StringComparison.OrdinalIgnoreCase));
                    if (eventLine is not null)
                    {
                        summary["事件"] = eventLine.Length > 6 ? eventLine[6..].Trim() : string.Empty;
                    }
                }

                var dataLine = payload.Split('\n')
                    .FirstOrDefault(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
                if (dataLine is not null)
                {
                    dataJson = dataLine.Length > 5 ? dataLine[5..].TrimStart() : string.Empty;
                }

                if (string.Equals(dataJson, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    summary["结束标记"] = "[DONE]";
                    return summary;
                }
            }

            if (string.IsNullOrWhiteSpace(dataJson))
            {
                return summary;
            }

            using var document = JsonDocument.Parse(dataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return summary;
            }

            var isRequest = direction.Equals("request", StringComparison.OrdinalIgnoreCase);
            if (sourceProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                TryAddSummary(summary, root, "模型", "model");
                TryAddSummary(summary, root, "流式", "stream");
                if (isRequest)
                {
                    TryAddSummary(summary, root, "消息数", "messages");
                    TryAddSummary(summary, root, "最大 token", "max_tokens");
                    TryAddSummary(summary, root, "温度", "temperature");
                    TryAddSummary(summary, root, "工具数", "tools");
                    TryAddSummary(summary, root, "推理等级", "reasoning_effort");
                }
                else
                {
                    TryAddSummary(summary, root, "返回条数", "choices");
                    TryAddSummary(summary, root, "输入 token", "usage", "prompt_tokens");
                    TryAddSummary(summary, root, "输出 token", "usage", "completion_tokens");
                    TryAddSummary(summary, root, "缓存 token", "usage", "prompt_tokens_details", "cached_tokens");
                    TryAddSummary(summary, root, "结束原因", "choices", "0", "finish_reason");
                }
            }
            else if (sourceProtocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                TryAddSummary(summary, root, "模型", "model");
                TryAddSummary(summary, root, "流式", "stream");
                if (isRequest)
                {
                    TryAddSummary(summary, root, "消息数", "messages");
                    TryAddSummary(summary, root, "最大 token", "max_tokens");
                    TryAddSummary(summary, root, "温度", "temperature");
                    TryAddSummary(summary, root, "工具数", "tools");
                }
                else
                {
                    TryAddSummary(summary, root, "内容块数", "content");
                    TryAddSummary(summary, root, "停止原因", "stop_reason");
                    TryAddSummary(summary, root, "输入 token", "usage", "input_tokens");
                    TryAddSummary(summary, root, "输出 token", "usage", "output_tokens");
                    TryAddSummary(summary, root, "缓存读", "usage", "cache_read_input_tokens");
                    TryAddSummary(summary, root, "缓存写", "usage", "cache_creation_input_tokens");
                }
            }
            else
            {
                TryAddSummary(summary, root, "模型", "model");
                TryAddSummary(summary, root, "流式", "stream");
                if (isRequest)
                {
                    TryAddSummary(summary, root, "输入条数", "input");
                    TryAddSummary(summary, root, "工具数", "tools");
                }
                else
                {
                    TryAddSummary(summary, root, "状态", "status");
                    TryAddSummary(summary, root, "输出条数", "output");
                    TryAddSummary(summary, root, "输入 token", "usage", "input_tokens");
                    TryAddSummary(summary, root, "输出 token", "usage", "output_tokens");
                    TryAddSummary(summary, root, "缓存 token", "usage", "input_tokens_details", "cached_tokens");
                }
            }

            return summary;
        }
        catch
        {
            return null;
        }
    }

    private static void TryAddSummary(JsonObject summary, JsonElement root, string label, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (segment == "0")
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() == 0)
                {
                    return;
                }

                current = current[0];
                continue;
            }

            if (!current.TryGetProperty(segment, out var next))
            {
                return;
            }

            current = next;
        }

        summary[label] = current.ValueKind switch
        {
            JsonValueKind.String => JsonValue.Create(current.GetString()),
            JsonValueKind.Number => JsonValue.Create(current.GetRawText()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Array => JsonValue.Create($"[{current.GetArrayLength()} 项]"),
            JsonValueKind.Object => JsonValue.Create("{...}"),
            _ => null
        };
    }

    /// <summary>
    /// 返回当前方向/协议组合的字段对应关系表，帮助定位转换后字段去向。
    /// </summary>
    private static IReadOnlyList<ProtocolFieldMapping> GetFieldMappings(string direction, string source, string target)
    {
        var isRequest = direction.Equals("request", StringComparison.OrdinalIgnoreCase);
        var key = $"{source}->{target}";
        var mappings = new List<ProtocolFieldMapping>();

        if (isRequest)
        {
            switch (key)
            {
                case "OpenAI->Anthropic":
                    mappings.Add(new("model", "model", "模型名透传"));
                    mappings.Add(new("messages", "messages", "角色/内容结构转换（system 并入、数组内容取文本）"));
                    mappings.Add(new("stream", "stream", "透传"));
                    mappings.Add(new("max_tokens", "max_tokens", "透传"));
                    mappings.Add(new("temperature", "temperature", "透传"));
                    mappings.Add(new("top_p", "top_p", "透传"));
                    mappings.Add(new("stop", "stop_sequences", "字符串自动转数组"));
                    mappings.Add(new("tools", "tools", "function → tool 结构转换"));
                    mappings.Add(new("tool_choice", "tool_choice", "auto/none 保持，指定名转 { type: tool }"));
                    mappings.Add(new("reasoning_effort", "thinking", "强制思考等级时内联覆盖"));
                    mappings.Add(new("frequency_penalty / presence_penalty / logit_bias", "—", "Anthropic 无对应字段，忽略"));
                    break;
                case "Anthropic->OpenAI":
                    mappings.Add(new("model", "model", "模型名透传"));
                    mappings.Add(new("messages", "messages", "角色/内容结构转换"));
                    mappings.Add(new("stream", "stream", "透传"));
                    mappings.Add(new("max_tokens", "max_tokens", "透传"));
                    mappings.Add(new("temperature", "temperature", "透传"));
                    mappings.Add(new("stop_sequences", "stop", "数组转字符串（单个时）"));
                    mappings.Add(new("tools", "tools", "tool → function 结构转换"));
                    mappings.Add(new("tool_choice", "tool_choice", "结构转换"));
                    mappings.Add(new("thinking", "reasoning_effort", "按策略转换"));
                    break;
                case "OpenAI->Responses":
                    mappings.Add(new("model", "model", "模型名透传"));
                    mappings.Add(new("messages", "input", "角色映射 + 内容结构转换"));
                    mappings.Add(new("stream", "stream", "透传"));
                    mappings.Add(new("max_tokens", "max_output_tokens", "透传（若存在）"));
                    mappings.Add(new("temperature", "temperature", "透传"));
                    mappings.Add(new("tools", "tools", "结构转换"));
                    mappings.Add(new("tool_choice", "tool_choice", "结构转换"));
                    mappings.Add(new("reasoning_effort", "reasoning", "结构转换"));
                    break;
                case "Anthropic->Responses":
                    mappings.Add(new("model", "model", "模型名透传"));
                    mappings.Add(new("messages", "input", "角色映射 + 内容结构转换"));
                    mappings.Add(new("stream", "stream", "透传"));
                    mappings.Add(new("max_tokens", "max_output_tokens", "透传（若存在）"));
                    mappings.Add(new("temperature", "temperature", "透传"));
                    mappings.Add(new("tools", "tools", "结构转换"));
                    mappings.Add(new("thinking", "reasoning", "结构转换"));
                    break;
                case "OpenAI->Gemini":
                    mappings.Add(new("model", "model", "模型名映射"));
                    mappings.Add(new("messages", "contents", "角色映射与轮次归一（user/assistant/tool → user/model）"));
                    mappings.Add(new("messages[role=system]", "systemInstruction", "提取为系统指令"));
                    mappings.Add(new("tools", "tools.functionDeclarations", "函数声明结构转换（清洗不支持的 Schema 字段）"));
                    mappings.Add(new("temperature / max_tokens", "generationConfig", "生成参数映射"));
                    mappings.Add(new("reasoning_effort", "generationConfig.thinkingConfig", "思考预算映射"));
                    break;
                case "Anthropic->Gemini":
                    mappings.Add(new("model", "model", "模型名映射"));
                    mappings.Add(new("messages", "contents", "多 part 展开与重排"));
                    mappings.Add(new("system", "systemInstruction", "系统指令提取"));
                    mappings.Add(new("tools", "tools.functionDeclarations", "工具声明转换"));
                    mappings.Add(new("thinking", "generationConfig.thinkingConfig", "思考预算映射"));
                    break;
                default:
                    mappings.Add(new(source + " 请求体", target + " 请求体", "逐字段转换，无对应字段时忽略"));
                    break;
            }
        }
        else
        {
            switch (key)
            {
                case "OpenAI->Anthropic":
                    mappings.Add(new("choices[].message.content", "content[].text", "输出文本转换"));
                    mappings.Add(new("choices[].message.tool_calls", "content[].type=tool_use", "工具调用转换"));
                    mappings.Add(new("choices[].finish_reason", "stop_reason", "stop → end_turn、tool_calls → tool_use 等"));
                    mappings.Add(new("usage.prompt_tokens", "usage.input_tokens", "含缓存口径还原"));
                    mappings.Add(new("usage.completion_tokens", "usage.output_tokens", "透传"));
                    mappings.Add(new("usage.prompt_tokens_details.cached_tokens", "usage.cache_read_input_tokens", "缓存命中映射"));
                    break;
                case "Anthropic->OpenAI":
                    mappings.Add(new("content[].text", "choices[].message.content", "输出文本转换"));
                    mappings.Add(new("content[].type=tool_use", "choices[].message.tool_calls", "工具调用转换"));
                    mappings.Add(new("stop_reason", "choices[].finish_reason", "end_turn → stop、tool_use → tool_calls 等"));
                    mappings.Add(new("usage.input_tokens", "usage.prompt_tokens", "含缓存口径还原"));
                    mappings.Add(new("usage.output_tokens", "usage.completion_tokens", "透传"));
                    mappings.Add(new("usage.cache_read_input_tokens", "usage.prompt_tokens_details.cached_tokens", "缓存命中映射"));
                    break;
                case "Responses->OpenAI":
                    mappings.Add(new("output[].output_text", "choices[].message.content", "输出文本转换"));
                    mappings.Add(new("output[].function_call", "choices[].message.tool_calls", "工具调用转换"));
                    mappings.Add(new("status", "choices[].finish_reason", "completed → stop 等"));
                    mappings.Add(new("usage.input_tokens", "usage.prompt_tokens", "含缓存口径还原"));
                    mappings.Add(new("usage.output_tokens", "usage.completion_tokens", "透传"));
                    mappings.Add(new("usage.input_tokens_details.cached_tokens", "usage.prompt_tokens_details.cached_tokens", "缓存命中映射"));
                    break;
                case "Responses->Anthropic":
                    mappings.Add(new("output[].output_text", "content[].text", "输出文本转换"));
                    mappings.Add(new("output[].function_call", "content[].type=tool_use", "工具调用转换"));
                    mappings.Add(new("status", "stop_reason", "completed → end_turn 等"));
                    mappings.Add(new("usage.input_tokens", "usage.input_tokens", "含缓存口径还原"));
                    mappings.Add(new("usage.output_tokens", "usage.output_tokens", "透传"));
                    mappings.Add(new("usage.input_tokens_details.cached_tokens", "usage.cache_read_input_tokens", "缓存命中映射"));
                    break;
                case "Gemini->OpenAI":
                    mappings.Add(new("candidates[].content.parts[].text", "choices[].message.content", "输出文本转换"));
                    mappings.Add(new("candidates[].content.parts[].thought", "choices[].message.reasoning_content", "深度思考提取"));
                    mappings.Add(new("candidates[].content.parts[].functionCall", "choices[].message.tool_calls", "工具调用转换"));
                    mappings.Add(new("candidates[].finishReason", "choices[].finish_reason", "STOP → stop、MAX_TOKENS → length 等"));
                    mappings.Add(new("usageMetadata", "usage", "promptTokenCount / candidatesTokenCount 映射"));
                    break;
                case "Gemini->Anthropic":
                    mappings.Add(new("candidates[].content.parts[].text", "content[].text", "输出文本转换"));
                    mappings.Add(new("candidates[].content.parts[].thought", "content[].type=thinking", "深度思考转换"));
                    mappings.Add(new("candidates[].content.parts[].functionCall", "content[].type=tool_use", "工具调用转换"));
                    mappings.Add(new("candidates[].finishReason", "stop_reason", "STOP → end_turn 等"));
                    mappings.Add(new("usageMetadata", "usage", "token 统计映射"));
                    break;
                default:
                    mappings.Add(new(source + " 响应体", target + " 响应体", "逐字段转换，无对应字段时忽略"));
                    break;
            }
        }

        return mappings;
    }

    /// <summary>
    /// 检查输入中缺失的关键字段，输出可执行的补全建议。
    /// </summary>
    private static IReadOnlyList<string> GetMissingFields(
        string direction,
        string sourceProtocol,
        bool streaming,
        string payload)
    {
        var missing = new List<string>();
        if (streaming)
        {
            // 流式诊断输入是单个事件片段（chat chunk / Anthropic 事件 / Responses SSE 块），
            // 没有"整体请求/响应字段"语义：messages/max_tokens/usage/content 等整体字段检查
            // 在单事件上必然缺失，会误报。返回空列表，由校验层负责格式正确性。
            return missing;
        }

        string? dataJson = payload;

        if (string.IsNullOrWhiteSpace(dataJson) || string.Equals(dataJson, "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return missing;
        }

        try
        {
            using var document = JsonDocument.Parse(dataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return missing;
            }

            var isRequest = direction.Equals("request", StringComparison.OrdinalIgnoreCase);
            if (isRequest)
            {
                if (sourceProtocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                    && !root.TryGetProperty("max_tokens", out _))
                {
                    missing.Add("Anthropic 请求缺少必填字段 max_tokens");
                }

                if (sourceProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                    && !root.TryGetProperty("messages", out _))
                {
                    missing.Add("OpenAI 请求缺少 messages，上游会返回参数错误");
                }

                if (sourceProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                    && !root.TryGetProperty("input", out _))
                {
                    missing.Add("Responses 请求缺少 input");
                }
            }
            else
            {
                if (!root.TryGetProperty("usage", out _))
                {
                    missing.Add("响应缺少 usage 对象，token 统计与客户端计费会显示为 0");
                }

                if (sourceProtocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                    && !root.TryGetProperty("choices", out _))
                {
                    missing.Add("OpenAI 响应缺少 choices");
                }

                if (sourceProtocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                    && !root.TryGetProperty("content", out _))
                {
                    missing.Add("Anthropic 响应缺少 content（空响应）");
                }

                if (sourceProtocol.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                    && !root.TryGetProperty("output", out _))
                {
                    missing.Add("Responses 响应缺少 output");
                }
            }
        }
        catch
        {
            // payload 解析失败不在此处报错，校验层已处理
        }

        return missing;
    }

    private static bool IsSupportedProtocol(string protocol)
        => protocol.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("Responses", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("Gemini", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedStreamingDirection(string direction, string source, string target)
        // 同协议：流式事件原样透传。
        => source.Equals(target, StringComparison.OrdinalIgnoreCase)
        || (direction.Equals("request", StringComparison.OrdinalIgnoreCase)
            && ((source.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                 && target.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("Responses", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))))
        || (direction.Equals("response", StringComparison.OrdinalIgnoreCase)
            && ((source.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                 && target.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                || (source.Equals("Responses", StringComparison.OrdinalIgnoreCase)
                    && target.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))));

    private static bool HasSseFraming(string payload)
        => payload.Contains("data:", StringComparison.OrdinalIgnoreCase)
            && payload.Contains("\n\n", StringComparison.Ordinal);

    private static bool HasValidResponsesSsePayload(string payload)
    {
        if (!HasSseFraming(payload))
        {
            return false;
        }

        var blocks = payload.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var dataLines = block.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[5..].Trim())
                .ToList();
            if (dataLines.Count == 0)
            {
                return false;
            }

            var data = string.Join("\n", dataLines);
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountSseEvents(string payload)
        => payload.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Count(block => block.Contains("data:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 检查开发者功能开关是否开启。
    /// </summary>
    private async Task<bool> IsDeveloperEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        return settings.DeveloperFeaturesEnabled;
    }

    private static bool IsSuccess(string? status)
        => string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsPending(string? status)
        => string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessOrPending(string? status)
        => IsSuccess(status) || IsPending(status);
}

/// <summary>
/// 离线协议诊断请求，仅承载用户手工输入的协议片段，不包含任何路由或凭据字段。
/// </summary>
public sealed class ProtocolDiagnosticsRequest
{
    public string Direction { get; set; } = string.Empty;
    public string SourceProtocol { get; set; } = string.Empty;
    public string TargetProtocol { get; set; } = string.Empty;
    public bool Streaming { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? EventName { get; set; }
    public string? OverrideReasoningEffort { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }

    /// <summary>
    /// 试运行时要套用的兼容规则（可选）。仅对非流式请求方向生效，scope 按透传/兼容路径筛选，
    /// 与真实链路 PrepareRequestBody 的规则应用语义一致。
    /// </summary>
    public List<CompatibilityRule>? Rules { get; set; }
}

internal sealed record ProtocolDiagnosticsConversionResult(
    string Payload,
    int EventCount,
    bool ConversionFailed,
    bool CompletionDetected,
    string ConversionPath,
    string? FailureReason,
    JsonObject? InputSummary,
    IReadOnlyList<ProtocolFieldMapping> FieldMappings,
    IReadOnlyList<string> MissingFields,
    ProtocolChainInfo Chain,
    bool RulesApplied);

/// <summary>
/// 一条协议字段对应关系（源字段 → 目标字段）。
/// </summary>
internal sealed record ProtocolFieldMapping(string Source, string Target, string? Note);

/// <summary>
/// 一次调用在 客户端 → 网关 → 上游 → 网关 → 客户端 链路中的完整转换可视化信息。
/// </summary>
internal sealed record ProtocolChainInfo(
    string Mode, // direct（透传）| bridge（兼容转换）
    IReadOnlyList<ProtocolChainStage> Stages,
    IReadOnlyList<ProtocolEventMapping> EventMappings);

/// <summary>
/// 链路中的一个环节。
/// </summary>
internal sealed record ProtocolChainStage(
    string Kind, // client-request | transform | upstream | transform-response | client-response
    string Label,
    string Protocol,
    string? Function,
    string? Note,
    bool IsBridge);

/// <summary>
/// 流式响应转换中的事件级对应关系（源事件 → 目标事件）。
/// </summary>
internal sealed record ProtocolEventMapping(string SourceEvent, string TargetEvent, string? Note);

/// <summary>
/// AI 智能诊断请求。
/// </summary>
public sealed class DeveloperAiDiagnoseRequest
{
    public Guid ModelId { get; set; }
    public Guid MappingId { get; set; }
    public bool EnableReasoning { get; set; }
    public string ReasoningEffort { get; set; } = "high";

    public string ClientProtocol { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string TargetSiteName { get; set; } = string.Empty;
    public string UpstreamProtocolType { get; set; } = string.Empty;
    public string ForwardingMode { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string OriginalRequestBody { get; set; } = string.Empty;
    public string PreparedRequestBody { get; set; } = string.Empty;
}

/// <summary>
/// AI 智能诊断响应。
/// </summary>
public sealed class DeveloperAiDiagnoseResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Reasoning { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public string SuggestedAction { get; set; } = string.Empty;
    public List<CompatibilityRule> Rules { get; set; } = [];
}
