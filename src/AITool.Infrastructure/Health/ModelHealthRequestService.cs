using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AITool.Infrastructure.Health;

// 真实请求式模型检测服务，使用与正常调用一致的请求方式并写入 UsageLogs。
public sealed class ModelHealthRequestService
{
    private readonly AppDbContext _dbContext;
    private readonly IProxyForwardService _forwardService;
    private readonly IUsageLogService _usageLogService;

    public ModelHealthRequestService(
        AppDbContext dbContext,
        IProxyForwardService forwardService,
        IUsageLogService usageLogService)
    {
        _dbContext = dbContext;
        _forwardService = forwardService;
        _usageLogService = usageLogService;
    }

    // 对指定映射发起一次真实请求式检测，并记录到 UsageLogs。
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

        var runtimeSettings = await _dbContext.SystemRuntimeSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken)
            ?? new AITool.Domain.Operations.SystemRuntimeSettings();
        var requestBody = BuildProbeRequestBody(site.ProtocolType, mapping.RemoteModelName, BuildRandomMathPrompt());
        var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = site.BaseUrl,
            TargetApiKey = site.ApiKey,
            ProtocolType = site.ProtocolType,
            TargetModelName = mapping.RemoteModelName,
            RequestBody = requestBody,
            PreparedRequestBody = requestBody,
            EnableStreaming = false,
            RequestTimeoutSeconds = runtimeSettings.DetectionRequestTimeoutSeconds,
            RetryCount = runtimeSettings.DetectionRetryCount
        }, cancellationToken);

        var status = forwardResult.Success ? "success" : "fail";
        mapping.LastStatus = status;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _usageLogService.LogAsync(new UsageLogEntry
        {
            RequestId = Guid.NewGuid(),
            AccessKeyId = Guid.Empty,
            ProtocolType = site.ProtocolType,
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

    // 生成随机四则运算题，避免固定请求内容过于单一。
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

    // 按站点协议构建一次普通非流式聊天请求。
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

// 单次真实请求式检测结果。
public sealed class ModelHealthProbeResult
{
    public Guid MappingId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
}
