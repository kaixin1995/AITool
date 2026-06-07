using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
    /// 注入数据库上下文、代理转发服务和日志服务
    /// </summary>
    public ModelHealthRequestService(
        AppDbContext dbContext,
        IProxyForwardService forwardService,
        IUsageLogService usageLogService)
    {
        _dbContext = dbContext;
        _forwardService = forwardService;
        _usageLogService = usageLogService;
    }

    /// <summary>
    /// 对指定映射发起一次真实请求式检测，并记录到 UsageLogs。
    /// </summary>
    public async Task<ModelHealthProbeResult> ProbeMappingAsync(Guid mappingId, string source, CancellationToken cancellationToken)
    {
        var mapping = await _dbContext.SiteModelMappings
            .FirstOrDefaultAsync(x => x.Id == mappingId, cancellationToken);
        if (mapping is null)
        {
            return new ModelHealthProbeResult
            {
                MappingId = mappingId,
                Status = "fail",
                ErrorMessage = "映射不存在"
            };
        }

        var site = await _dbContext.Sites.FirstOrDefaultAsync(x => x.Id == mapping.SiteId, cancellationToken);
        var model = await _dbContext.ModelLibraryItems.FirstOrDefaultAsync(x => x.Id == mapping.ModelLibraryItemId, cancellationToken);
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

        var protocolType = ResolveSiteProtocolType(site.SupportsOpenAi, site.SupportsAnthropic);
        var runtimeSettings = await _dbContext.SystemRuntimeSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken)
            ?? new AITool.Domain.Operations.SystemRuntimeSettings();
        var requestBody = BuildProbeRequestBody(protocolType, mapping.RemoteModelName, BuildRandomMathPrompt());
        var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = site.BaseUrl,
            TargetEndpointPathMode = site.EndpointPathMode,
            TargetApiKey = site.ApiKey,
            ProtocolType = protocolType,
            TargetModelName = mapping.RemoteModelName,
            RequestBody = requestBody,
            PreparedRequestBody = requestBody,
            EnableStreaming = false,
            RequestTimeoutSeconds = runtimeSettings.DetectionRequestTimeoutSeconds,
            RetryCount = runtimeSettings.DetectionRetryCount,
            TargetPath = string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                ? SiteEndpointPathResolver.ResolvePath(site.EndpointPathMode, "responses")
                : null
        }, cancellationToken);

        var status = forwardResult.Success ? "success" : "fail";
        mapping.LastStatus = status;
        await _dbContext.SaveChangesAsync(cancellationToken);

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
    /// 按站点支持能力推导一次普通非流式聊天请求协议。
    /// </summary>
    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        if (!supportsOpenAi && !supportsAnthropic)
        {
            return "Responses";
        }

        return supportsOpenAi || !supportsAnthropic ? "OpenAI" : "Anthropic";
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
                ["stream"] = false
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
