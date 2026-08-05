using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class ChatModelTarget
{
    public string MappingId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelDisplayName { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string DisplayText => $"{SiteName} / {SiteModelName}";
}

public sealed class ChatSendResult
{
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ReasoningContent { get; set; }
    public string? Error { get; set; }
    public int? TotalDurationMs { get; set; }
    public List<ChatAttemptResult> Attempts { get; set; } = new();
}

public sealed class ChatAttemptResult
{
    public int? AttemptIndex { get; set; }
    public string? SiteName { get; set; }
    public string? AttemptedModel { get; set; }
    public string? SiteModelName { get; set; }
    public string? Status { get; set; }
    public string? ErrorMessage { get; set; }
    public bool? IsFinalResult { get; set; }
    public bool? IsStreaming { get; set; }
    public int? InputTokens { get; set; }
    public int? CachedTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int? FirstTokenLatencyMs { get; set; }
    public int? TotalDurationMs { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }
    public string? ForwardingMode { get; set; }
    public string? UpstreamProtocolType { get; set; }

    public string AttemptTitle => $"第 {AttemptIndex.GetValueOrDefault() + 1} 次尝试 · {SiteName ?? "未知站点"}";
    public string AttemptModelText => $"{AttemptedModel ?? "未知模型"} / {SiteModelName ?? "-"}";
    public string StatusText => string.IsNullOrWhiteSpace(ErrorMessage)
        ? (string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase) ? "成功" : Status ?? "未知")
        : "失败";
    public string DurationText => TotalDurationMs.HasValue ? $"耗时 {TotalDurationMs} ms" : "耗时 -";
    public string FirstTokenText => FirstTokenLatencyMs.HasValue ? $"首字 {FirstTokenLatencyMs} ms" : "首字 -";
    public string StreamingText => $"流式 {(IsStreaming == true ? "是" : "否")}";
    public string TokenText => $"输入 {InputTokens ?? 0} / 缓存 {CachedTokens ?? 0} / 输出 {OutputTokens ?? 0} / 总计 {TotalTokens ?? 0}";
    public bool IsSuccess => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase)
        || IsFinalResult == true && string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage) || !IsSuccess;
    public bool HasRequestBody => !string.IsNullOrWhiteSpace(RequestBody);
    public bool HasResponseBody => !string.IsNullOrWhiteSpace(ResponseBody);
}

public partial class ChatMessage : ObservableObject
{
    public bool IsUser { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string _reasoning = string.Empty;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private ObservableCollection<ChatAttemptResult> _attempts = new();
    [ObservableProperty] private int? _totalDurationMs;
    public string RoleText => IsUser ? "我" : "AI";
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
    public bool HasAttempts => Attempts.Count > 0;
    public string TotalDurationText => TotalDurationMs.HasValue ? $"总耗时 {TotalDurationMs} ms" : string.Empty;

    partial void OnReasoningChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoning));
    }

    partial void OnAttemptsChanged(ObservableCollection<ChatAttemptResult> value)
    {
        OnPropertyChanged(nameof(HasAttempts));
    }

    partial void OnTotalDurationMsChanged(int? value)
    {
        OnPropertyChanged(nameof(TotalDurationText));
    }
}
