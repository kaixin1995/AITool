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
    public string? SiteName { get; set; }
    public string? AttemptedModel { get; set; }
    public string? Status { get; set; }
    public string? ErrorMessage { get; set; }
    public int? TotalDurationMs { get; set; }
}

public partial class ChatMessage : ObservableObject
{
    public bool IsUser { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string _reasoning = string.Empty;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isStreaming;
    public string RoleText => IsUser ? "我" : "AI";
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    partial void OnReasoningChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoning));
    }
}
