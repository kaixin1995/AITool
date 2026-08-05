namespace AITool.Desktop.Models;

public sealed class DeveloperListResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }
    public int FailedCount { get; set; }
    public int PendingCount { get; set; }
    public List<DeveloperInvocationSummary> Entries { get; set; } = new();
}

public sealed class DeveloperInvocationSummary
{
    public string TraceId { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string TargetSiteName { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int TotalDurationMs { get; set; }
    public int AttemptCount { get; set; }
    public string StatusText => Status is "success" or "ok" ? "成功" : Status == "pending" ? "等待" : "失败";
}

public sealed class DeveloperInvocationDetail
{
    public string TraceId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string RequestHeaders { get; set; } = string.Empty;
    public string RequestBody { get; set; } = string.Empty;
    public string TargetSiteName { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public List<DeveloperInvocationAttempt> Attempts { get; set; } = new();
}

public sealed class DeveloperInvocationAttempt
{
    public string AttemptId { get; set; } = string.Empty;
    public string TargetSiteName { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string PreparedRequestBody { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public int TotalDurationMs { get; set; }
}
