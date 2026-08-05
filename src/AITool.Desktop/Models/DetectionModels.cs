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

    // 保存最近一次探测的错误，便于在单项检测后直接展示服务端返回的信息。
    [ObservableProperty]
    private string _lastError = string.Empty;

    [ObservableProperty]
    private string _lastCheckedAt = string.Empty;

    [ObservableProperty]
    private int? _lastDurationMs;

    public string StatusText => LastStatus switch
    {
        "success" => "成功",
        "fail" => "失败",
        _ => string.IsNullOrWhiteSpace(LastStatus) ? "未检测" : LastStatus
    };

    // 状态颜色交给主题资源处理，避免模型层固定浅色主题颜色。
    public bool IsSuccess => string.Equals(LastStatus, "success", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(LastStatus, "fail", StringComparison.OrdinalIgnoreCase);
    public bool IsUnknown => !IsSuccess && !IsFailed;
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public string DurationText => LastDurationMs.HasValue ? $"{LastDurationMs} ms" : "-";

    partial void OnLastStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsUnknown));
    }

    partial void OnLastErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnLastDurationMsChanged(int? value) => OnPropertyChanged(nameof(DurationText));
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
