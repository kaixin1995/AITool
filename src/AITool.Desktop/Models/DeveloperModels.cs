using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class DeveloperInitResponse
{
    public int TotalCount { get; set; }
    public int FailedCount { get; set; }
    public int PendingCount { get; set; }
    public string DefaultBaseUrl { get; set; } = string.Empty;
    public string DefaultAccessKey { get; set; } = string.Empty;
    public List<DeveloperSimulatorModel> Models { get; set; } = new();
    public string DefaultOpenAiModel { get; set; } = string.Empty;
    public string DefaultAnthropicModel { get; set; } = string.Empty;
}

public sealed class DeveloperSimulatorModel
{
    public string ModelName { get; set; } = string.Empty;
    public int RouteCount { get; set; }
    public bool SupportsOpenAi { get; set; }
    public bool SupportsAnthropic { get; set; }
    public bool CanUseOpenAi { get; set; }
    public bool CanUseAnthropic { get; set; }
    public bool SupportsResponses { get; set; }
}

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
    public int SuccessAttemptCount { get; set; }
    public int FailedAttemptCount { get; set; }
    public int PendingAttemptCount { get; set; }

    public bool IsPending => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase);
    public bool IsSuccessful => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => !IsPending && !IsSuccessful;
    public string StatusText => IsPending ? "等待" : IsSuccessful ? "成功" : "失败";
    public string DurationText => FormatDuration(TotalDurationMs);
    public string AttemptStatsText => $"成功 {SuccessAttemptCount} / 失败 {FailedAttemptCount} / 等待 {PendingAttemptCount}";

    private static string FormatDuration(int value)
    {
        if (value <= 0) return "-";
        return value >= 1000 ? $"{value / 1000d:0.#}s" : $"{value}ms";
    }
}

public sealed class DeveloperInvocationDetail
{
    public string TraceId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string RequestBody { get; set; } = string.Empty;
    public string TargetSiteName { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
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
    public List<DeveloperInvocationAttempt> Attempts { get; set; } = new();

    public string RequestHeadersText => RequestHeaders.Count == 0
        ? "无"
        : string.Join(Environment.NewLine, RequestHeaders.Select(x => $"{x.Key}: {x.Value}"));
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string DurationText => TotalDurationMs <= 0 ? "-" : TotalDurationMs >= 1000 ? $"{TotalDurationMs / 1000d:0.#}s" : $"{TotalDurationMs}ms";
}

public sealed class DeveloperInvocationAttempt
{
    public string AttemptId { get; set; } = string.Empty;
    public string TargetSiteName { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string ForwardingMode { get; set; } = string.Empty;
    public string UpstreamProtocolType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string PreparedRequestBody { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public string ResponseContentType { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalDurationMs { get; set; }

    public bool IsPending => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase);
    public bool IsSuccessful => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => !IsPending && !IsSuccessful;
    public bool HasPreparedRequestBody => !string.IsNullOrWhiteSpace(PreparedRequestBody);
    public bool HasResponseBody => !string.IsNullOrWhiteSpace(ResponseBody);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string StatusText => IsPending ? "等待" : IsSuccessful ? "成功" : "失败";
    public string DurationText => TotalDurationMs <= 0 ? "-" : TotalDurationMs >= 1000 ? $"{TotalDurationMs / 1000d:0.#}s" : $"{TotalDurationMs}ms";
    public string TokensText => $"{InputTokens:N0} / {CachedTokens:N0} / {OutputTokens:N0}";
    public string HttpStatusText => $"HTTP {StatusCode}";
    public string TokenSummaryText => $"Token {TokensText}";
}

public sealed class DeveloperConcurrencyResponse
{
    public string RefreshedAt { get; set; } = string.Empty;
    public List<DeveloperConcurrencyItem> Items { get; set; } = new();
}

public sealed class DeveloperConcurrencyItem
{
    public string SiteId { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public int ActiveCount { get; set; }
    public int? MaxConcurrency { get; set; }
    public int QueueCount { get; set; }
    public string LastSeenAt { get; set; } = string.Empty;
    public string MaxConcurrencyText => MaxConcurrency?.ToString() ?? "不限";
}

public sealed class CircuitBreakerResponse
{
    public List<CircuitBreakerRoute> Routes { get; set; } = new();
}

public sealed class CircuitBreakerRoute
{
    public string RouteId { get; set; } = string.Empty;
    public string CircuitKey { get; set; } = string.Empty;
    public string EntryName { get; set; } = string.Empty;
    public string UpstreamModelName { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public bool IsBlocked { get; set; }
    public int FailureCount { get; set; }
    public string? BlockedUntil { get; set; }
    public int? RemainingSeconds { get; set; }
    public bool IsNotBlocked => !IsBlocked;
    public bool CanReset => IsBlocked || FailureCount > 0;
    public string StatusText => IsBlocked ? "已熔断" : "失败累计";
    public string FailureText => $"失败 {FailureCount} 次";
    public string RemainingText => RemainingSeconds is null ? "-" : RemainingSeconds < 60 ? $"{RemainingSeconds}s" : $"{RemainingSeconds / 60}m {RemainingSeconds % 60}s";
}

public sealed class DeveloperRawResponse
{
    public int StatusCode { get; init; }
    public string Body { get; init; } = string.Empty;
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

public sealed partial class DeveloperSimulatorTab : ObservableObject
{
    public DeveloperSimulatorTab(string key, string label, string endpoint, string method, bool streamable)
    {
        Key = key;
        Label = label;
        Endpoint = endpoint;
        Method = method;
        IsStreamable = streamable;
    }

    public string Key { get; }
    public string Label { get; }
    public string Endpoint { get; }
    public string Method { get; }
    public bool IsStreamable { get; }

    [ObservableProperty] private bool _streamEnabled;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isSelected;

    public bool CanSend => !IsRunning;
    [ObservableProperty] private string _response = "尚未请求";
    [ObservableProperty] private string _requestExample = string.Empty;
    [ObservableProperty] private string _endpointUrl = string.Empty;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanSend));
}
