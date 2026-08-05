namespace AITool.Desktop.Models;

public partial class RouteEntry : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string EntryName { get; set; } = string.Empty;
    public int CandidateCount { get; set; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSelected;
}

public partial class RouteRuleItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string RuleId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public bool SiteEnabled { get; set; }
    public string UpstreamModelName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int ModelPriority { get; set; }
    public int InstancePriority { get; set; }
    public string AvailabilityMode { get; set; } = "AllDay";
    public string TimeRangesJson { get; set; } = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isEnabled;

    public string StatusText => IsEnabled ? "已启用" : "已停用";

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
    }
}
