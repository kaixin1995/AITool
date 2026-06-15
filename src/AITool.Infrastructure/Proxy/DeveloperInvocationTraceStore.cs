using System.Text.Json;
using AITool.Application.Common;
using Microsoft.AspNetCore.Http;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 开发者调用跟踪存储。
/// <para>
/// 当追踪记录完成时（状态从 pending 变为 success / error 等），
/// 会触发 <see cref="OnTraceCompleted"/> 事件，供外部订阅者（如事件发布器）消费。
/// 事件触发是 fire-and-forget 方式，不会阻塞调用追踪的主流程。
/// </para>
/// </summary>
public sealed class DeveloperInvocationTraceStore
{
    /// <summary>
    /// 最大保留记录数。
    /// </summary>
    private const int MaxEntryCount = 100;
    /// <summary>
    /// 调用记录保留时长。
    /// </summary>
    private static readonly TimeSpan EntryRetention = TimeSpan.FromHours(6);
    /// <summary>
    /// 过期清理检查的最小间隔。锁内每次操作都无条件调用 PurgeExpiredUnsafe 会放大锁竞争，
    /// 节流为每 PurgeThrottleInterval 最多执行一次清理，过期项最多多存活这段时间，功能不变。
    /// </summary>
    private static readonly TimeSpan PurgeThrottleInterval = TimeSpan.FromSeconds(30);
    /// <summary>
    /// 并发访问锁对象。
    /// </summary>
    private readonly object _gate = new();
    /// <summary>
    /// 调用跟踪记录列表。
    /// </summary>
    private readonly LinkedList<DeveloperInvocationTraceEntry> _entries = [];
    /// <summary>
    /// 调用跟踪节点索引。
    /// </summary>
    private readonly Dictionary<Guid, LinkedListNode<DeveloperInvocationTraceEntry>> _nodes = [];
    /// <summary>
    /// 上次执行过期清理的时间（UTC），用于节流。
    /// </summary>
    private DateTimeOffset _lastPurgeTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// 当追踪记录状态从 pending 变为最终状态时触发。
    /// 订阅者可以将已完成的追踪发布为 Core 事件。
    /// 参数为已完成的追踪记录的深拷贝，线程安全。
    /// </summary>
    public event Action<DeveloperInvocationTraceEntry>? OnTraceCompleted;

    /// <summary>
    /// 添加调用请求记录。
    /// </summary>
    public Guid AddRequest(DeveloperInvocationTraceRequest request)
    {
        var entry = new DeveloperInvocationTraceEntry
        {
            TraceId = Guid.NewGuid(),
            RequestId = request.RequestId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Source = request.Source,
            UserAgent = request.UserAgent,
            ClientIp = request.ClientIp,
            ProtocolType = request.ProtocolType,
            RequestPath = request.RequestPath,
            RequestModel = request.RequestModel,
            RequestBody = request.RequestBody,
            RequestHeaders = request.RequestHeaders,
            AccessKeyId = request.AccessKeyId,
            ReasoningEffort = request.ReasoningEffort,
            Status = "pending"
        };

        lock (_gate)
        {
            MaybePurgeExpiredUnsafe();
            var node = _entries.AddFirst(entry);
            _nodes[entry.TraceId] = node;
            TrimUnsafe();
            return entry.TraceId;
        }
    }

    /// <summary>
    /// 添加调用尝试记录。
    /// </summary>
    public Guid AddAttempt(Guid traceId, DeveloperInvocationAttempt attempt)
    {
        lock (_gate)
        {
            MaybePurgeExpiredUnsafe();
            if (!_nodes.TryGetValue(traceId, out var node))
            {
                return Guid.Empty;
            }

            var traceAttempt = new DeveloperInvocationTraceAttempt
            {
                AttemptId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                AttemptedModel = attempt.AttemptedModel,
                UpstreamProtocolType = attempt.UpstreamProtocolType,
                ForwardingMode = attempt.ForwardingMode,
                TargetSiteId = attempt.TargetSiteId,
                TargetSiteName = attempt.TargetSiteName,
                Status = "pending"
            };
            node.Value.Attempts.Add(traceAttempt);
            node.Value.AttemptedModel = traceAttempt.AttemptedModel;
            node.Value.UpstreamProtocolType = traceAttempt.UpstreamProtocolType;
            node.Value.TargetSiteId = traceAttempt.TargetSiteId;
            node.Value.TargetSiteName = traceAttempt.TargetSiteName;
            node.Value.UpdatedAt = DateTimeOffset.UtcNow;
            return traceAttempt.AttemptId;
        }
    }

    /// <summary>
    /// 完成一次调用尝试并回写结果。
    /// 当追踪记录状态从 pending 变为最终状态时，触发 <see cref="OnTraceCompleted"/> 事件。
    /// </summary>
    public void CompleteAttempt(Guid traceId, Guid attemptId, DeveloperInvocationResult result)
    {
        DeveloperInvocationTraceEntry? completedEntry = null;

        lock (_gate)
        {
            MaybePurgeExpiredUnsafe();
            if (!_nodes.TryGetValue(traceId, out var node))
            {
                return;
            }

            var attempt = node.Value.Attempts.FirstOrDefault(x => x.AttemptId == attemptId);
            if (attempt is null)
            {
                return;
            }

            var wasPending = string.Equals(node.Value.Status, "pending", StringComparison.OrdinalIgnoreCase);

            attempt.Status = result.Status;
            attempt.StatusCode = result.StatusCode;
            attempt.ErrorMessage = result.ErrorMessage;
            attempt.ResponseBody = result.ResponseBody;
            attempt.ResponseContentType = result.ResponseContentType;
            attempt.IsStreaming = result.IsStreaming;
            attempt.InputTokens = result.InputTokens;
            attempt.CachedTokens = result.CachedTokens;
            attempt.OutputTokens = result.OutputTokens;
            attempt.TotalDurationMs = result.TotalDurationMs;
            attempt.FirstTokenLatencyMs = result.FirstTokenLatencyMs;
            attempt.StreamDurationMs = result.StreamDurationMs;
            attempt.IsStreamInterrupted = result.IsStreamInterrupted;
            attempt.UpdatedAt = DateTimeOffset.UtcNow;

            node.Value.AttemptedModel = attempt.AttemptedModel;
            node.Value.UpstreamProtocolType = attempt.UpstreamProtocolType;
            node.Value.TargetSiteId = attempt.TargetSiteId;
            node.Value.TargetSiteName = attempt.TargetSiteName;
            node.Value.Status = result.Status;
            node.Value.StatusCode = result.StatusCode;
            node.Value.ErrorMessage = result.ErrorMessage;
            node.Value.ResponseBody = result.ResponseBody;
            node.Value.ResponseContentType = result.ResponseContentType;
            node.Value.IsStreaming = result.IsStreaming;
            node.Value.InputTokens = result.InputTokens;
            node.Value.CachedTokens = result.CachedTokens;
            node.Value.OutputTokens = result.OutputTokens;
            node.Value.TotalDurationMs = result.TotalDurationMs;
            node.Value.FirstTokenLatencyMs = result.FirstTokenLatencyMs;
            node.Value.StreamDurationMs = result.StreamDurationMs;
            node.Value.IsStreamInterrupted = result.IsStreamInterrupted;
            node.Value.RetryCount = result.RetryCount;
            node.Value.IsFinalResult = result.IsFinalResult;
            node.Value.FallbackTriggered = result.FallbackTriggered;
            node.Value.UpdatedAt = DateTimeOffset.UtcNow;

            // 状态从 pending 变为最终状态时，克隆一份给事件订阅者
            if (wasPending && !string.Equals(result.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                completedEntry = Clone(node.Value);
            }
        }

        // 在锁外部触发事件，避免死锁风险
        if (completedEntry is not null)
        {
            OnTraceCompleted?.Invoke(completedEntry);
        }
    }

    /// <summary>
    /// 客户端断开时强制将 pending 状态的追踪记录标记为 error。
    /// 仅在记录仍处于 pending 状态时生效，已完成的记录不会被覆盖。
    /// </summary>
    /// <param name="traceId">要取消的追踪标识。</param>
    /// <param name="reason">取消原因，写入 ErrorMessage 字段。</param>
    public void CancelPending(Guid traceId, string reason)
    {
        DeveloperInvocationTraceEntry? completedEntry = null;

        lock (_gate)
        {
            MaybePurgeExpiredUnsafe();
            if (!_nodes.TryGetValue(traceId, out var node))
            {
                return;
            }

            // 仅 pending 状态的记录才收尾，防止覆盖已完成的正常状态
            if (!string.Equals(node.Value.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 将所有 pending 的 attempt 也标记为 error
            foreach (var attempt in node.Value.Attempts)
            {
                if (string.Equals(attempt.Status, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    attempt.Status = "error";
                    attempt.ErrorMessage = reason;
                    attempt.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            node.Value.Status = "error";
            node.Value.ErrorMessage = reason;
            node.Value.UpdatedAt = DateTimeOffset.UtcNow;

            completedEntry = Clone(node.Value);
        }

        if (completedEntry is not null)
        {
            OnTraceCompleted?.Invoke(completedEntry);
        }
    }

    /// <summary>
    /// 返回当前调用记录列表。
    /// </summary>
    public IReadOnlyList<DeveloperInvocationTraceEntry> List()
    {
        lock (_gate)
        {
            MaybePurgeExpiredUnsafe();
            return _entries.Select(Clone).ToList();
        }
    }

    /// <summary>
    /// 按跟踪标识获取调用记录。
    /// </summary>
    public DeveloperInvocationTraceEntry? Get(Guid traceId)
    {
        lock (_gate)
        {
            MaybePurgeExpiredUnsafe();
            return _nodes.TryGetValue(traceId, out var node) ? Clone(node.Value) : null;
        }
    }

    /// <summary>
    /// 清理过期记录（带节流）。
    /// 仅在距上次清理超过 <see cref="PurgeThrottleInterval"/> 时才真正执行，
    /// 避免每次锁内操作都做 O(过期数) 扫描放大锁竞争。必须在持锁状态下调用。
    /// </summary>
    private void MaybePurgeExpiredUnsafe()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPurgeTime < PurgeThrottleInterval)
        {
            return;
        }
        _lastPurgeTime = now;
        PurgeExpiredUnsafeCore();
    }

    /// <summary>
    /// 清理过期记录的实际实现（无节流）。
    /// </summary>
    private void PurgeExpiredUnsafeCore()
    {
        var expireBefore = DateTimeOffset.UtcNow - EntryRetention;
        while (_entries.Last is { } last && last.Value.CreatedAt < expireBefore)
        {
            _nodes.Remove(last.Value.TraceId);
            _entries.RemoveLast();
        }
    }

    /// <summary>
    /// 裁剪超出上限的记录。
    /// </summary>
    private void TrimUnsafe()
    {
        while (_entries.Count > MaxEntryCount)
        {
            var last = _entries.Last;
            if (last is null)
            {
                break;
            }

            _nodes.Remove(last.Value.TraceId);
            _entries.RemoveLast();
        }
    }

    /// <summary>
    /// 复制调用记录。
    /// </summary>
    private static DeveloperInvocationTraceEntry Clone(DeveloperInvocationTraceEntry entry)
    {
        return new DeveloperInvocationTraceEntry
        {
            TraceId = entry.TraceId,
            RequestId = entry.RequestId,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
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
            RequestHeaders = entry.RequestHeaders.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            Status = entry.Status,
            StatusCode = entry.StatusCode,
            ErrorMessage = entry.ErrorMessage,
            ResponseBody = entry.ResponseBody,
            ResponseContentType = entry.ResponseContentType,
            IsStreaming = entry.IsStreaming,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            TotalDurationMs = entry.TotalDurationMs,
            AccessKeyId = entry.AccessKeyId,
            ReasoningEffort = entry.ReasoningEffort,
            FirstTokenLatencyMs = entry.FirstTokenLatencyMs,
            StreamDurationMs = entry.StreamDurationMs,
            IsStreamInterrupted = entry.IsStreamInterrupted,
            RetryCount = entry.RetryCount,
            IsFinalResult = entry.IsFinalResult,
            FallbackTriggered = entry.FallbackTriggered,
            Attempts = entry.Attempts.Select(CloneAttempt).ToList()
        };
    }

    /// <summary>
    /// 复制调用尝试记录。
    /// </summary>
    private static DeveloperInvocationTraceAttempt CloneAttempt(DeveloperInvocationTraceAttempt attempt)
    {
        return new DeveloperInvocationTraceAttempt
        {
            AttemptId = attempt.AttemptId,
            CreatedAt = attempt.CreatedAt,
            UpdatedAt = attempt.UpdatedAt,
            AttemptedModel = attempt.AttemptedModel,
            UpstreamProtocolType = attempt.UpstreamProtocolType,
            ForwardingMode = attempt.ForwardingMode,
            TargetSiteId = attempt.TargetSiteId,
            TargetSiteName = attempt.TargetSiteName,
            Status = attempt.Status,
            StatusCode = attempt.StatusCode,
            ErrorMessage = attempt.ErrorMessage,
            ResponseBody = attempt.ResponseBody,
            ResponseContentType = attempt.ResponseContentType,
            IsStreaming = attempt.IsStreaming,
            InputTokens = attempt.InputTokens,
            CachedTokens = attempt.CachedTokens,
            OutputTokens = attempt.OutputTokens,
            TotalDurationMs = attempt.TotalDurationMs,
            FirstTokenLatencyMs = attempt.FirstTokenLatencyMs,
            StreamDurationMs = attempt.StreamDurationMs,
            IsStreamInterrupted = attempt.IsStreamInterrupted
        };
    }

    /// <summary>
    /// 提取请求头。
    /// </summary>
    public static Dictionary<string, string> CaptureHeaders(IHeaderDictionary headers)
    {
        return headers
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 格式化请求或响应内容。
    /// </summary>
    public static string FormatBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document, JsonSerializerPresets.WriteIndented);
        }
        catch
        {
            return body;
        }
    }
}

/// <summary>
/// 开发者调用请求信息。
/// </summary>
public sealed class DeveloperInvocationTraceRequest
{
    /// <summary>
    /// 请求标识。
    /// </summary>
    public Guid RequestId { get; set; }
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
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;
    /// <summary>
    /// 请求模型名称。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;
    /// <summary>
    /// 请求体。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;
    /// <summary>
    /// 请求头。
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    /// <summary>
    /// 访问密钥标识，用于关联使用日志。
    /// </summary>
    public Guid AccessKeyId { get; set; }
    /// <summary>
    /// 推理深度参数。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;
}

/// <summary>
/// 开发者调用尝试信息。
/// </summary>
public sealed class DeveloperInvocationAttempt
{
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
    /// 目标站点标识。
    /// </summary>
    public Guid? TargetSiteId { get; set; }
    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string TargetSiteName { get; set; } = string.Empty;
}

/// <summary>
/// 开发者调用结果。
/// </summary>
public sealed class DeveloperInvocationResult
{
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
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
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 流式响应持续时长（毫秒）。
    /// </summary>
    public int StreamDurationMs { get; set; }
    /// <summary>
    /// 流式传输是否被中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }
    /// <summary>
    /// 重试次数。
    /// </summary>
    public int RetryCount { get; set; }
    /// <summary>
    /// 是否为最终结果。
    /// </summary>
    public bool IsFinalResult { get; set; }
    /// <summary>
    /// 是否触发了降级。
    /// </summary>
    public bool FallbackTriggered { get; set; }
}

/// <summary>
/// 开发者调用跟踪记录。
/// </summary>
public sealed class DeveloperInvocationTraceEntry
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
    /// 更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
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
    /// 请求体。
    /// </summary>
    public string RequestBody { get; set; } = string.Empty;
    /// <summary>
    /// 请求头。
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    /// <summary>
    /// 访问密钥标识，用于关联使用日志。
    /// </summary>
    public Guid AccessKeyId { get; set; }
    /// <summary>
    /// 推理深度参数。
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
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
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 流式响应持续时长（毫秒）。
    /// </summary>
    public int StreamDurationMs { get; set; }
    /// <summary>
    /// 流式传输是否被中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }
    /// <summary>
    /// 重试次数。
    /// </summary>
    public int RetryCount { get; set; }
    /// <summary>
    /// 是否为最终结果。
    /// </summary>
    public bool IsFinalResult { get; set; }
    /// <summary>
    /// 是否触发了降级。
    /// </summary>
    public bool FallbackTriggered { get; set; }
    /// <summary>
    /// 尝试记录列表。
    /// </summary>
    public List<DeveloperInvocationTraceAttempt> Attempts { get; set; } = [];
}

/// <summary>
/// 开发者调用尝试记录。
/// </summary>
public sealed class DeveloperInvocationTraceAttempt
{
    /// <summary>
    /// 调用尝试标识。
    /// </summary>
    public Guid AttemptId { get; set; }
    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>
    /// 更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
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
    /// 目标站点标识。
    /// </summary>
    public Guid? TargetSiteId { get; set; }
    /// <summary>
    /// 目标站点名称。
    /// </summary>
    public string TargetSiteName { get; set; } = string.Empty;
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
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
    /// 首 Token 延迟（毫秒）。
    /// </summary>
    public int FirstTokenLatencyMs { get; set; }
    /// <summary>
    /// 流式响应持续时长（毫秒）。
    /// </summary>
    public int StreamDurationMs { get; set; }
    /// <summary>
    /// 流式传输是否被中断。
    /// </summary>
    public bool IsStreamInterrupted { get; set; }
}
