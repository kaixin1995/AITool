using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class DetectionMatrix
{
    public List<DetectionModelGroup> ModelGroups { get; set; } = new();
}

public sealed class DetectionModelGroup
{
    public string ModelLibraryItemId { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<DetectionSiteStatus> Sites { get; set; } = new();
}

public partial class DetectionSiteStatus : ObservableObject
{
    public string MappingId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;

    [ObservableProperty]
    private string _lastStatus = string.Empty;

    public string LastCheckedAt { get; set; } = string.Empty;
    public int? LastDurationMs { get; set; }

    public string StatusText => LastStatus switch
    {
        "success" => "成功",
        "fail" => "失败",
        _ => string.IsNullOrWhiteSpace(LastStatus) ? "未检测" : LastStatus
    };

    public string DurationText => LastDurationMs.HasValue ? $"{LastDurationMs} ms" : "-";

    partial void OnLastStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));
}

public sealed class ProbeResultItem
{
    public string MappingId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? DurationMs { get; set; }
    public string? Error { get; set; }
}

public sealed class ProbeProgress
{
    public string TaskId { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Completed { get; set; }
    public bool IsCompleted { get; set; }
    public List<ProbeResultItem> NewResults { get; set; } = new();
}
