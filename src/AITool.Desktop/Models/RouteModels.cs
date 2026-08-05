namespace AITool.Desktop.Models;

/// <summary>
/// 可添加到路由候选队列的站点模型实例。
/// </summary>
public sealed class SiteInstanceItem
{
    public string SiteId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public bool SiteEnabled { get; set; } = true;

    public string DisplayText => $"{SiteName} / {SiteModelName} / {ProtocolType}";
}

public partial class RouteEntry : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string EntryName { get; set; } = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private int _candidateCount;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSelected;
}

public partial class RouteRuleItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string RuleId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public bool SiteEnabled { get; set; }
    public bool IsSiteDisabled => !SiteEnabled;
    public string UpstreamModelName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string InstanceSummary => $"{UpstreamModelName} · {SiteModelName}";
    public int Priority { get; set; }
    public int ModelPriority { get; set; }
    public int InstancePriority { get; set; }
    public string AvailabilityMode { get; set; } = "AllDay";
    public string TimeRangesJson { get; set; } = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isEnabled;

    private bool _canMoveUp;
    private bool _canMoveDown;

    public bool CanMoveUp
    {
        get => _canMoveUp;
        set => SetProperty(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        set => SetProperty(ref _canMoveDown, value);
    }

    public string PriorityText => (Priority + 1).ToString();
    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";

    public void SetPriority(int value)
    {
        if (Priority == value) return;
        Priority = value;
        OnPropertyChanged(nameof(PriorityText));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
    }
}
