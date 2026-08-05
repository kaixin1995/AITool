namespace AITool.Desktop.Models;

public sealed class UsageLogFilters
{
    public List<UsageLogFilterItem> Sites { get; set; } = new();
    public List<UsageLogFilterItem> AccessKeys { get; set; } = new();
}

public sealed class UsageLogFilterItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class UsageLogOption
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class UsageLogListResponse
{
    public List<UsageLogItem> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

public sealed class UsageLogSummary
{
    public int TotalRequests { get; set; }
    public int FailedRequests { get; set; }
    public double SuccessRate { get; set; }
    public long TotalTokens { get; set; }
    public int MaxDurationMs { get; set; }

    public string SuccessRateText => $"{SuccessRate * 100:0.0}%";
}

public sealed class UsageLogItem
{
    public string Id { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string AccessKeyName { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public int AttemptIndex { get; set; }
    public bool IsFinalResult { get; set; }
    public bool FallbackTriggered { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsStreamInterrupted { get; set; }
    public int FirstTokenLatencyMs { get; set; }
    public int StreamDurationMs { get; set; }
    public int TotalDurationMs { get; set; }
    public string ReasoningEffort { get; set; } = string.Empty;
    public string RequestedAt { get; set; } = string.Empty;
    public string InputTokensText => InputTokens.ToString("N0");
    public string CachedTokensText => CachedTokens.ToString("N0");
    public string OutputTokensText => OutputTokens.ToString("N0");
    public string TotalTokensText => TotalTokens.ToString("N0");

    public bool IsSuccessfulStatus => Status is "success" or "ok";
    public bool IsFailedStatus => !IsSuccessfulStatus;
    public string SourceLabel => Source?.Trim().ToLowerInvariant() switch
    {
        "proxy" => "代理",
        "chat" => "对话测试",
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        "open-code" => "Open Code",
        "zcode" => "ZCode",
        "detection-manual" => "手动检测",
        "detection-task" => "定时检测",
        _ => SourceText
    };
    public string StatusText => FallbackTriggered && IsSuccessfulStatus ? "回退后成功" : IsStreamInterrupted && IsFailedStatus ? "流中断" : IsSuccessfulStatus ? "成功" : "失败";
    public string DurationText => TotalDurationMs > 0 ? $"{TotalDurationMs} ms" : "-";
    public string FirstTokenText => IsStreaming && FirstTokenLatencyMs > 0 ? $"{FirstTokenLatencyMs} ms" : "-";
    public string StreamingText => IsStreaming ? "流" : "非流";
    public string SourceText => string.IsNullOrWhiteSpace(Source) ? "-" : Source;
}

public sealed class UsageLogRequestDetail
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string RouteEntry { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string ForwardingMode { get; set; } = string.Empty;
    public string ReasoningEffort { get; set; } = string.Empty;
    public List<UsageLogItem> Attempts { get; set; } = new();
}
