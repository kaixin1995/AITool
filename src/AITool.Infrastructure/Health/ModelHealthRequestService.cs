using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
using AITool.Protocol;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Sites;

namespace AITool.Infrastructure.Health;

/// <summary>
/// 真实请求式模型检测服务，使用与正常调用一致的请求方式并写入 UsageLogs。
/// </summary>
public sealed class ModelHealthRequestService
{
    /// <summary>
    /// 数据库上下文，用于查询映射、站点、模型等数据
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理转发服务，用于向目标站点发起真实请求
    /// </summary>
    private readonly IProxyForwardService _forwardService;
    /// <summary>
    /// 使用日志服务，用于记录每次检测的调用结果
    /// </summary>
    private readonly IUsageLogService _usageLogService;
    /// <summary>
    /// 站点密钥选择器，取站点活动密钥（多 Key 站点用优先级最高的启用项）。
    /// </summary>
    private readonly SiteKeySelector _siteKeySelector;

    /// <summary>
    /// 注入数据库上下文、代理转发服务、日志服务和站点密钥选择器
    /// </summary>
    public ModelHealthRequestService(
        AppDbContext dbContext,
        IProxyForwardService forwardService,
        IUsageLogService usageLogService,
        SiteKeySelector siteKeySelector)
    {
        _dbContext = dbContext;
        _forwardService = forwardService;
        _usageLogService = usageLogService;
        _siteKeySelector = siteKeySelector;
    }

    /// <summary>
    /// 对指定映射发起一次真实请求式检测，并记录到 UsageLogs。
    /// </summary>
    public async Task<ModelHealthProbeResult> ProbeMappingAsync(Guid mappingId, string source, CancellationToken cancellationToken)
    {
        var mapping = await _dbContext.SiteModelMappings
            .FirstAsync(x => x.Id == mappingId, cancellationToken);
        if (mapping is null)
        {
            return new ModelHealthProbeResult
            {
                MappingId = mappingId,
                Status = "fail",
                ErrorMessage = "映射不存在"
            };
        }

        var site = await _dbContext.Sites.FirstAsync(x => x.Id == mapping.SiteId, cancellationToken);
        var model = await _dbContext.ModelLibraryItems.FirstAsync(x => x.Id == mapping.ModelLibraryItemId, cancellationToken);
        if (site is null || model is null)
        {
            return new ModelHealthProbeResult
            {
                MappingId = mapping.Id,
                SiteName = site?.Name ?? string.Empty,
                RemoteModelName = mapping.RemoteModelName,
                Status = "fail",
                ErrorMessage = "站点或模型不存在"
            };
        }

        var protocolType = ProxyProtocolResolver.ResolveSiteProtocolType(
            site.SupportsOpenAi,
            site.SupportsAnthropic,
            site.SupportsResponses,
            site.ProtocolType);
        var runtimeSettings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken)
            ?? new AITool.Domain.Operations.SystemRuntimeSettings();
        var requestBody = BuildProbeRequestBody(protocolType, mapping.RemoteModelName, BuildRandomMathPrompt());
        // Codex 上游（Responses 协议）不接受 max_output_tokens，会返回
        // {"detail":"Unsupported parameter: max_output_tokens"}（400）。
        // BuildProbeRequestBody 对 Responses 会设 max_output_tokens，此处按需剔除。
        if (string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(site.BaseUrl)
            && ProxyProtocolBridge.IsCodexTarget(site.BaseUrl))
        {
            requestBody = StripCodexUnsupportedFields(requestBody);
        }

        // 级联解析自定义请求头与客户端仿真特征（Mapping > Model > Site）
        Dictionary<string, string> extraHeaders = new(StringComparer.OrdinalIgnoreCase);
        var isAntigravity = ProxyProtocolBridge.IsAntigravityTarget(site.BaseUrl);
        var effectiveEmulation = ResolveClientEmulation(mapping.ClientEmulation, model.ClientEmulation, site.ClientEmulation, protocolType, isAntigravity);

        // 命中请求头模板方案（内置预设被编辑 / 自定义 Key）时作为最底层注入，显式 Site/Model/Mapping 头仍可覆盖。
        if (!string.IsNullOrWhiteSpace(effectiveEmulation))
        {
            var headerProfile = await _dbContext.HeaderProfiles
                .FirstAsync(p => p.Key == effectiveEmulation && p.IsEnabled, cancellationToken);
            if (headerProfile?.HeadersJson is not null)
            {
                MergeHeadersJson(extraHeaders, headerProfile.HeadersJson);
            }
        }

        MergeHeadersJson(extraHeaders, site.ExtraHeadersJson);
        MergeHeadersJson(extraHeaders, model.ExtraHeadersJson);
        MergeHeadersJson(extraHeaders, mapping.ExtraHeadersJson);

        var effectiveProxyRaw = !string.IsNullOrWhiteSpace(mapping.EgressProxyUrl) ? mapping.EgressProxyUrl.Trim() : site.EgressProxyUrl;
        string? effectiveProxyUrl = null;
        if (!string.IsNullOrWhiteSpace(effectiveProxyRaw) &&
            !string.Equals(effectiveProxyRaw, "None", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(effectiveProxyRaw, "direct", StringComparison.OrdinalIgnoreCase))
        {
            var profile = await _dbContext.ProxyProfiles.FirstAsync(p => p.Key == effectiveProxyRaw && p.IsEnabled, cancellationToken);
            effectiveProxyUrl = profile != null ? profile.ProxyUrl : effectiveProxyRaw;
        }

        var forwardHeaders = ClientEmulationEngine.ResolveHeaders(
            effectiveEmulation,
            extraHeaders,
            mapping.RemoteModelName,
            null,
            isAntigravity);

        // 取站点活动密钥：多 Key 站点用优先级最高的启用项，没有 SiteKey 时回退 site.ApiKey（兼容 Codex/未迁移）。
        var activeApiKey = await _siteKeySelector.GetActiveKeyAsync(site.Id, cancellationToken);
        if (string.IsNullOrEmpty(activeApiKey))
        {
            activeApiKey = site.ApiKey;
        }

        var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = site.BaseUrl,
            TargetEndpointPathMode = site.EndpointPathMode,
            TargetApiKey = activeApiKey,
            ProtocolType = protocolType,
            TargetModelName = mapping.RemoteModelName,
            RequestBody = requestBody,
            PreparedRequestBody = requestBody,
            EnableStreaming = false,
            RequestTimeoutSeconds = runtimeSettings.DetectionRequestTimeoutSeconds,
            RetryCount = runtimeSettings.DetectionRetryCount,
            ForwardHeaders = forwardHeaders,
            EgressProxyUrl = effectiveProxyUrl,
            TargetPath = string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                ? SiteEndpointPathResolver.ResolvePath(site.EndpointPathMode, "responses")
                : null
        }, cancellationToken);

        var status = forwardResult.Success ? "success" : "fail";
        mapping.LastStatus = status;
        await _dbContext.UpdateAsync(mapping, cancellationToken);

        await _usageLogService.LogAsync(new UsageLogEntry
        {
            RequestId = Guid.NewGuid(),
            AccessKeyId = Guid.Empty,
            ProtocolType = protocolType,
            RequestModel = model.ModelName,
            AttemptedModel = mapping.RemoteModelName,
            TargetSiteId = site.Id,
            Status = status,
            Source = source,
            RetryCount = 0,
            AttemptIndex = 1,
            IsFinalResult = true,
            FallbackTriggered = false,
            ErrorMessage = forwardResult.Success ? string.Empty : (forwardResult.ErrorMessage ?? string.Empty),
            HttpStatusCode = forwardResult.StatusCode > 0 ? forwardResult.StatusCode : null,
            InputTokens = forwardResult.InputTokens,
            CachedTokens = forwardResult.CachedTokens,
            OutputTokens = forwardResult.OutputTokens,
            IsStreaming = false,
            IsStreamInterrupted = forwardResult.IsStreamInterrupted,
            FirstTokenLatencyMs = forwardResult.FirstTokenLatencyMs,
            StreamDurationMs = forwardResult.StreamDurationMs,
            TotalDurationMs = forwardResult.TotalDurationMs,
            ReasoningEffort = string.Empty
        }, cancellationToken);

        return new ModelHealthProbeResult
        {
            MappingId = mapping.Id,
            SiteName = site.Name,
            RemoteModelName = mapping.RemoteModelName,
            Status = status,
            DurationMs = forwardResult.TotalDurationMs,
            ErrorMessage = forwardResult.Success ? null : forwardResult.ErrorMessage
        };
    }

    /// <summary>
    /// 生成随机四则运算题，避免固定请求内容过于单一。
    /// </summary>
    private static string BuildRandomMathPrompt()
    {
        var left = Random.Shared.Next(1, 100);
        var right = Random.Shared.Next(1, 100);
        var operation = Random.Shared.Next(0, 4);

        return operation switch
        {
            0 => $"请直接回答结果，不要解释：{left} + {right} = ?",
            1 => $"请直接回答结果，不要解释：{left} - {right} = ?",
            2 => $"请直接回答结果，不要解释：{left} * {right} = ?",
            _ => $"请直接回答结果，不要解释：{left * right} / {right} = ?"
        };
    }

    /// <summary>
    /// 按站点协议构建一次普通非流式聊天请求。
    /// </summary>
    private static string BuildProbeRequestBody(string protocolType, string modelName, string message)
    {
        if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new Dictionary<string, object?>
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
                ["max_tokens"] = 64,
                ["stream"] = false
            });
        }

        if (string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["model"] = modelName,
                ["input"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "input_text",
                                ["text"] = message
                            }
                        }
                    }
                },
                ["max_output_tokens"] = 64,
                ["stream"] = false,
                // Codex 上游强制要求 store=false，缺失会返回 400 "store must be set to false"。
                ["store"] = false
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
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
            ["max_tokens"] = 64,
            ["stream"] = false
        });
    }

    /// <summary>
    /// 剔除 Codex 上游不接受的请求体字段。
    /// Codex（chatgpt.com/backend-api/codex/responses）对参数白名单很严格，
    /// max_output_tokens / temperature / metadata 等任一字段都会触发
    /// {"detail":"Unsupported parameter: xxx"}（400）。
    /// 复用 AITool.Protocol 的 CodexUnsupportedParameters 字段清单，避免与协议层漂移。
    /// </summary>
    private static string StripCodexUnsupportedFields(string requestBody)
    {
        try
        {
            var rootNode = System.Text.Json.Nodes.JsonNode.Parse(requestBody) as System.Text.Json.Nodes.JsonObject;
            if (rootNode is null)
            {
                return requestBody;
            }

            foreach (var field in ProxyProtocolBridge.CodexUnsupportedParameters)
            {
                rootNode.Remove(field);
            }

            if (rootNode["store"] is null)
            {
                rootNode["store"] = false;
            }

            return rootNode.ToJsonString();
        }
        catch
        {
            return requestBody;
        }
    }

    private static void MergeHeadersJson(Dictionary<string, string> target, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            if (dict == null) return;
            foreach (var (k, v) in dict)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    target[k] = v ?? string.Empty;
                }
            }
        }
        catch { }
    }

    private static string ResolveClientEmulation(string? mappingEmulation, string? modelEmulation, string? siteEmulation, string protocolType, bool isAntigravity)
    {
        // 与 ProxyRequestMetadataCache.ResolveClientEmulation 口径一致：
        // 内置预设归一化返回；未知值视为自定义 HeaderProfile Key 原样透传（无匹配档案时引擎不注入任何头，无副作用）。
        foreach (var candidate in new[] { mappingEmulation, modelEmulation, siteEmulation })
        {
            var trimmed = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var normalized = Domain.Sites.ClientEmulationConstants.Normalize(trimmed);
            if (!string.Equals(normalized, Domain.Sites.ClientEmulationConstants.None, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return trimmed;
        }

        if (string.Equals(protocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return isAntigravity ? Domain.Sites.ClientEmulationConstants.Antigravity : Domain.Sites.ClientEmulationConstants.GeminiCli;
        }

        return Domain.Sites.ClientEmulationConstants.None;
    }
}

/// <summary>
/// 单次真实请求式检测结果。
/// </summary>
public sealed class ModelHealthProbeResult
{
    /// <summary>
    /// 被检测的站点模型映射 ID
    /// </summary>
    public Guid MappingId { get; set; }
    /// <summary>
    /// 站点名称
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 站点上的实际模型名称
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 检测结果状态：success 或 fail
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 请求耗时（毫秒），可能为空
    /// </summary>
    public int? DurationMs { get; set; }
    /// <summary>
    /// 失败时的错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }
}
