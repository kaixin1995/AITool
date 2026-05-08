using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AITool.Web.Services;

// 开发者调用调试记录存储，仅保留内存中的最近 100 条请求链路。
public sealed class DeveloperInvocationTraceStore
{
    private const int MaxEntryCount = 100;
    private readonly object _gate = new();
    private readonly LinkedList<DeveloperInvocationTraceEntry> _entries = [];
    private readonly Dictionary<Guid, LinkedListNode<DeveloperInvocationTraceEntry>> _nodes = [];

    // 请求刚到达时先创建一条记录，保证页面能立即看到请求信息。
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
            Status = "pending"
        };

        lock (_gate)
        {
            var node = _entries.AddFirst(entry);
            _nodes[entry.TraceId] = node;
            TrimUnsafe();
            return entry.TraceId;
        }
    }

    // 每次真实路由尝试都单独记录，避免前面的失败被最后一次成功覆盖。
    public Guid AddAttempt(Guid traceId, DeveloperInvocationAttempt attempt)
    {
        lock (_gate)
        {
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

    // 收到上游响应或失败后更新对应尝试结果，并同步更新整条请求的最终状态。
    public void CompleteAttempt(Guid traceId, Guid attemptId, DeveloperInvocationResult result)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(traceId, out var node))
            {
                return;
            }

            var attempt = node.Value.Attempts.FirstOrDefault(x => x.AttemptId == attemptId);
            if (attempt is null)
            {
                return;
            }

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
            node.Value.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<DeveloperInvocationTraceEntry> List()
    {
        lock (_gate)
        {
            return _entries.Select(Clone).ToList();
        }
    }

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
            Attempts = entry.Attempts.Select(CloneAttempt).ToList()
        };
    }

    private static DeveloperInvocationTraceAttempt CloneAttempt(DeveloperInvocationTraceAttempt attempt)
    {
        return new DeveloperInvocationTraceAttempt
        {
            AttemptId = attempt.AttemptId,
            CreatedAt = attempt.CreatedAt,
            UpdatedAt = attempt.UpdatedAt,
            AttemptedModel = attempt.AttemptedModel,
            UpstreamProtocolType = attempt.UpstreamProtocolType,
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
            TotalDurationMs = attempt.TotalDurationMs
        };
    }

    public static Dictionary<string, string> CaptureHeaders(IHeaderDictionary headers)
    {
        return headers
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    public static string FormatBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return body;
        }
    }
}

public sealed class DeveloperInvocationTraceRequest
{
    public Guid RequestId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string RequestBody { get; set; } = string.Empty;
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
}

public sealed class DeveloperInvocationAttempt
{
    public string AttemptedModel { get; set; } = string.Empty;
    public string UpstreamProtocolType { get; set; } = string.Empty;
    public Guid? TargetSiteId { get; set; }
    public string TargetSiteName { get; set; } = string.Empty;
}

public sealed class DeveloperInvocationResult
{
    public string Status { get; set; } = string.Empty;
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

public sealed class DeveloperInvocationTraceEntry
{
    public Guid TraceId { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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
    public string RequestBody { get; set; } = string.Empty;
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public string ResponseContentType { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalDurationMs { get; set; }
    public List<DeveloperInvocationTraceAttempt> Attempts { get; set; } = [];
}

public sealed class DeveloperInvocationTraceAttempt
{
    public Guid AttemptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string AttemptedModel { get; set; } = string.Empty;
    public string UpstreamProtocolType { get; set; } = string.Empty;
    public Guid? TargetSiteId { get; set; }
    public string TargetSiteName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
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
