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

    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ModelText => string.IsNullOrWhiteSpace(ModelName) ? "全部模型" : ModelName!;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}
