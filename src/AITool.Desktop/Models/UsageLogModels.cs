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
    public int TotalTokens { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsStreamInterrupted { get; set; }
    public int TotalDurationMs { get; set; }
    public string RequestedAt { get; set; } = string.Empty;

    public string StatusText => Status is "success" or "ok" ? "成功" : "失败";
    public string DurationText => TotalDurationMs > 0 ? $"{TotalDurationMs} ms" : "-";
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
