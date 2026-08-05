using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class DetectionTaskListResponse
{
    public List<DetectionTaskItem> Tasks { get; set; } = new();
    public List<DetectionModelOption> AvailableModels { get; set; } = new();
}

public sealed class DetectionModelOption
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class DetectionTaskHistoryItem
{
    public string StartedAt { get; set; } = string.Empty;
    public string? FinishedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Summary { get; set; }

    public string DurationText
    {
        get
        {
            if (!DateTimeOffset.TryParse(StartedAt, out var start)
                || !DateTimeOffset.TryParse(FinishedAt, out var finish)) return "-";
            return $"{(finish - start).TotalSeconds:0.0}s";
        }
    }
}

public partial class DetectionTaskItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string? ModelLibraryItemId { get; set; }
    public string? ModelName { get; set; }
    public string? LastExecutionStartedAt { get; set; }
    public string? LastExecutionStatus { get; set; }
    public List<DetectionTaskHistoryItem> ExecutionHistory { get; set; } = new();

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";
    public string ModelText => string.IsNullOrWhiteSpace(ModelName) ? "全部模型" : ModelName!;
    public string StatusBackground => IsEnabled ? "#E8F7EA" : "#F1F5F9";
    public string StatusForeground => IsEnabled ? "#166534" : "#64748B";
    public bool CanToggle => !IsBusy;
    public bool CanExecute => !IsBusy;
    public bool CanDelete => !IsBusy;
    public bool HasBusyText => !string.IsNullOrWhiteSpace(BusyText);

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanDelete));
    }

    partial void OnBusyTextChanged(string value) => OnPropertyChanged(nameof(HasBusyText));
}
