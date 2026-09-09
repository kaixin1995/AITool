using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Protocol;

namespace AITool.Admin.Services;

/// <summary>
/// AI 助手服务：给后台管理功能（请求头模板「AI 查最新版」、模型价格「AI 查价格」等）
/// 提供统一的一次性非流式 AI 调用通道。
/// <para>
/// 站点/模型取自设置页配置的 <see cref="Domain.Operations.SystemRuntimeSettings.DefaultAiTargetMappingId"/>，
/// 转发复用 <see cref="IProxyForwardService"/>（自动处理协议路径、鉴权头、出口代理、SSE 聚合），
/// 请求构造方式与 AI 诊断（DeveloperInvocationsApiController）保持一致。
/// 管理端低频手动调用，不占用模型并发槽位。
/// </para>
/// </summary>
public sealed class AiAssistantService
{
    /// <summary>
    /// AI 调用超时下限（秒）：查询/归纳类回答可能明显慢于普通对话，避免被默认 60s 掐断。
    /// </summary>
    private const int MinTimeoutSeconds = 120;

    /// <summary>单次回答的最大输出 token。80 个模型条目的 JSON 数组约 5k token，留出余量。</summary>
    private const int MaxTokens = 8192;

    /// <summary>空内容（思考耗尽输出预算等瞬态现象）的最大尝试次数：首次 + 自动重试。</summary>
    private const int EmptyContentMaxAttempts = 2;

    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IProxyForwardService _forwardService;
    private readonly ILogger<AiAssistantService> _logger;

    public AiAssistantService(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IProxyForwardService forwardService,
        ILogger<AiAssistantService> logger)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _forwardService = forwardService;
        _logger = logger;
    }

    /// <summary>一次 AI 调用的结果。业务失败（未配置/上游失败）以 Success=false 表达，不抛异常。</summary>
    public sealed record AiAssistantResult(bool Success, string? Error, string? Content)
    {
        public static AiAssistantResult Ok(string content) => new(true, null, content);
        public static AiAssistantResult Fail(string error) => new(false, error, null);
    }

    /// <summary>
    /// 用设置页配置的默认站点/模型执行一次单轮非流式补全，返回模型输出文本。
    /// </summary>
    public async Task<AiAssistantResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return AiAssistantResult.Fail("提示词为空");
        }

        var settings = await _dbContext.SystemRuntimeSettings
            .FirstAsync(x => x.Id == 1, cancellationToken);
        if (settings?.DefaultAiTargetMappingId is not { } mappingId || mappingId == Guid.Empty)
        {
            return AiAssistantResult.Fail("尚未配置默认 AI 站点/模型，请先到「设置 → AI 助手」选择");
        }

        var targets = await _metadataCache.GetChatTargetsAsync(cancellationToken);
        var target = targets.FirstOrDefault(x => x.MappingId == mappingId);
        if (target is null)
        {
            return AiAssistantResult.Fail("配置的默认 AI 站点/模型已不存在或被禁用，请到「设置 → AI 助手」重新选择");
        }

        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);

        var chatRequestBody = BuildChatRequestBody(target.SiteModelName, prompt);
        var isGeminiRoute = string.Equals(target.ProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase);
        string preparedRequestBody = chatRequestBody;
        if (string.Equals(target.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            // Responses 端点不接受 chat 形状的 messages：先转换（messages→input），再做 Codex 参数清洗，
            // 顺序与 ChatApiController 的聊天测试一致（漏转换会被上游 400 "Unsupported parameter: messages" 拒绝）。
            preparedRequestBody = ProxyProtocolBridge.NormalizeResponsesBody(
                ProxyProtocolBridge.ConvertChatRequestToResponses(chatRequestBody, target.SiteModelName, false),
                ProxyProtocolBridge.IsCodexTarget(target.BaseUrl));
        }
        else if (isGeminiRoute)
        {
            preparedRequestBody = ProxyProtocolBridge.PrepareRequestBody(
                "OpenAI", "Gemini", chatRequestBody, target.SiteModelName, false,
                null, target.BaseUrl, null, isPassthrough: false, isCompact: false,
                geminiProjectId: target.GoogleProjectId);
        }

        var isAntigravity = ProxyProtocolBridge.IsAntigravityTarget(target.BaseUrl);
        var effectiveEmulation = !string.IsNullOrWhiteSpace(target.ClientEmulation)
            && !string.Equals(Domain.Sites.ClientEmulationConstants.None, target.ClientEmulation, StringComparison.OrdinalIgnoreCase)
            ? target.ClientEmulation
            : (isGeminiRoute ? Domain.Sites.ClientEmulationConstants.Antigravity : Domain.Sites.ClientEmulationConstants.None);

        var forwardHeaders = ClientEmulationEngine.ResolveHeaders(
            effectiveEmulation,
            target.ExtraHeaders,
            target.SiteModelName,
            target.GoogleProjectId,
            isAntigravity);

        try
        {
            // 思考型模型（GLM/DeepSeek 等）偶发把输出预算全部耗在推理上、正文为空（finish=length），
            // 属瞬态现象：同一请求重试通常即可命中。空内容自动重试一次，仍为空才判失败。
            for (var attempt = 1; ; attempt++)
            {
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
                    RequestTimeoutSeconds = Math.Max(MinTimeoutSeconds, runtimeSettings.ProxyRequestTimeoutSeconds),
                    RetryCount = 0,
                    ForwardHeaders = forwardHeaders,
                    EgressProxyUrl = target.EgressProxyUrl,
                    TargetPath = isGeminiRoute
                        ? "/v1internal:generateContent"
                        : string.Equals(target.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                            ? SiteEndpointPathResolver.ResolvePath(target.EndpointPathMode, "responses")
                            : null
                }, cancellationToken);

                if (!forwardResult.Success)
                {
                    _logger.LogWarning("AI assistant call failed (site {SiteId}, model {Model}): {Status} {Error}",
                        target.SiteId, target.SiteModelName, forwardResult.StatusCode, forwardResult.ErrorMessage);
                    return AiAssistantResult.Fail($"AI 调用失败 (HTTP {forwardResult.StatusCode}): {Truncate(forwardResult.ErrorMessage)}");
                }

                var (content, _) = ExtractChatCompletionContent(forwardResult.ResponseBody, target.ProtocolType);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return AiAssistantResult.Ok(content);
                }

                // 记录原始响应片段，便于定位新的响应形态；同时识别「只有思考没有正文」给出可操作提示。
                _logger.LogWarning(
                    "AI assistant empty content (attempt {Attempt}, protocol {Protocol}, site {SiteId}, model {Model}). Response head: {Head}",
                    attempt, target.ProtocolType, target.SiteId, target.SiteModelName, Truncate(forwardResult.ResponseBody));

                if (attempt >= EmptyContentMaxAttempts)
                {
                    var hint = HasReasoningWithoutText(forwardResult.ResponseBody)
                        ? "（模型只返回了思考内容没有正文，多为思考型模型在输出上限内未完成推理——建议在「设置 → AI 助手」换用非思考模型）"
                        : string.Empty;
                    return AiAssistantResult.Fail($"AI 返回了空内容（已自动重试 {EmptyContentMaxAttempts - 1} 次）{hint}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI assistant call threw (site {SiteId}, model {Model})", target.SiteId, target.SiteModelName);
            return AiAssistantResult.Fail("AI 调用异常：" + Truncate(ex.Message));
        }
    }

    /// <summary>
    /// 从模型回复中提取首个 JSON 块：优先剥 ```json 围栏，其次截取首尾大括号/中括号之间的内容。
    /// 提取失败返回空串。所有 AI 助手功能共用的容错解析。
    /// </summary>
    public static string ExtractJsonBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var startIndex = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (startIndex >= 0)
        {
            startIndex += 7;
            var endIndex = text.IndexOf("```", startIndex, StringComparison.Ordinal);
            return (endIndex > startIndex ? text[startIndex..endIndex] : text[startIndex..]).Trim();
        }

        var firstObject = text.IndexOf('{');
        var firstArray = text.IndexOf('[');
        var start = firstObject >= 0 && (firstArray < 0 || firstObject < firstArray) ? firstObject : firstArray;
        if (start < 0) return string.Empty;

        var lastObject = text.LastIndexOf('}');
        var lastArray = text.LastIndexOf(']');
        var end = Math.Max(lastObject, lastArray);
        return end > start ? text[start..(end + 1)].Trim() : string.Empty;
    }

    /// <summary>
    /// 按目标协议构建单轮非流式请求体（system 指令并入 user 消息，规避各协议 system 字段差异）。
    /// </summary>
    private static string BuildChatRequestBody(string modelName, string prompt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            },
            ["stream"] = false,
            ["max_tokens"] = MaxTokens
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// 从各协议响应体中提取纯文本回复（与 AI 诊断的提取逻辑同构）。
    /// OpenAI 兼容分支覆盖：字符串 content、数组 content、旧版顶层 text。
    /// 公开为静态以便单测锁定各响应形态。
    /// </summary>
    public static (string Content, string? Reasoning) ExtractChatCompletionContent(string responseBody, string protocolType)
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
                        if (!item.TryGetProperty("type", out var typeProp)) continue;
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
                return (sb.ToString(), reasoning);
            }

            if (string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("output_text", out var outText))
                {
                    return (outText.GetString() ?? string.Empty, null);
                }
                if (root.TryGetProperty("output", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "message"
                            && item.TryGetProperty("content", out var contentList) && contentList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in contentList.EnumerateArray())
                            {
                                if (c.TryGetProperty("type", out var ct) && ct.GetString() == "output_text"
                                    && c.TryGetProperty("text", out var tp))
                                {
                                    sb.Append(tp.GetString());
                                }
                            }
                        }
                    }
                    return (sb.ToString(), null);
                }
                return (string.Empty, null);
            }

            if (string.Equals(protocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                {
                    var first = candidates.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object
                        && first.TryGetProperty("content", out var content)
                        && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in parts.EnumerateArray())
                        {
                            if (p.TryGetProperty("text", out var tp))
                            {
                                sb.Append(tp.GetString());
                            }
                        }
                    }
                }
                return (sb.ToString(), null);
            }

            // OpenAI Chat Completions
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var first = choices.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var contentEl))
                {
                    if (contentEl.ValueKind == JsonValueKind.String)
                    {
                        return (contentEl.GetString() ?? string.Empty, null);
                    }
                    // 新版 OpenAI 兼容形态：content 为分块数组 [{type:"text", text:"..."}]，
                    // 只认字符串会把这类响应误判为空内容。
                    if (contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in contentEl.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.Object
                                && part.TryGetProperty("text", out var textProp)
                                && textProp.ValueKind == JsonValueKind.String)
                            {
                                sb.Append(textProp.GetString());
                            }
                        }
                        return (sb.ToString(), null);
                    }
                }
                // 旧版 /v1/completions 形态：正文在顶层 text 字段。
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("text", out var legacyText)
                    && legacyText.ValueKind == JsonValueKind.String)
                {
                    return (legacyText.GetString() ?? string.Empty, null);
                }
            }
            return (string.Empty, null);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    /// <summary>
    /// 识别「只有思考、没有正文」的响应：reasoning_content / reasoning 字段有内容
    /// （DeepSeek / OpenRouter 等思考型模型的常见形态）。正文提取失败时用于给出可操作提示。
    /// </summary>
    private static bool HasReasoningWithoutText(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return false;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            foreach (var field in new[] { "reasoning_content", "reasoning" })
            {
                if (root.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(el.GetString()))
                {
                    return true;
                }
            }
            // OpenAI 形态：reasoning 藏在 message 里
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind == JsonValueKind.Object
                        && choice.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in new[] { "reasoning_content", "reasoning" })
                        {
                            if (message.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(el.GetString()))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
