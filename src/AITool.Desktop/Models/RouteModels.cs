using System.Text.Json;

namespace AITool.Desktop.Models;

/// <summary>
/// 路由可用性选项，值使用后端契约，标签用于界面显示。
/// </summary>
public sealed class RouteAvailabilityOption
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

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

    private string _availabilityMode = "AllDay";
    private string _timeRangesJson = string.Empty;
    private string _timeRangeStart = "00:00";
    private string _timeRangeEnd = "23:59";

    public string AvailabilityMode
    {
        get => _availabilityMode;
        set
        {
            if (!SetProperty(ref _availabilityMode, value)) return;
            OnPropertyChanged(nameof(HasTimeRange));
            if (string.Equals(value, "AllDay", StringComparison.OrdinalIgnoreCase))
            {
                TimeRangesJson = string.Empty;
            }
            else
            {
                UpdateTimeRangesJson();
            }
        }
    }

    public string TimeRangesJson
    {
        get => _timeRangesJson;
        set
        {
            if (!SetProperty(ref _timeRangesJson, value)) return;
            ParseTimeRanges(value);
        }
    }

    public string TimeRangeStart
    {
        get => _timeRangeStart;
        set
        {
            if (SetProperty(ref _timeRangeStart, value)) UpdateTimeRangesJson();
        }
    }

    public string TimeRangeEnd
    {
        get => _timeRangeEnd;
        set
        {
            if (SetProperty(ref _timeRangeEnd, value)) UpdateTimeRangesJson();
        }
    }

    public bool HasTimeRange => !string.Equals(AvailabilityMode, "AllDay", StringComparison.OrdinalIgnoreCase);

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
    // 状态颜色交给主题资源处理，避免模型层固定浅色主题颜色。
    public bool IsDisabled => !IsEnabled;
    public bool CanToggle => !string.IsNullOrWhiteSpace(RuleId);

    public void SetPriority(int value)
    {
        if (Priority == value) return;
        Priority = value;
        OnPropertyChanged(nameof(PriorityText));
    }

    private void ParseTimeRanges(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var ranges = JsonSerializer.Deserialize<List<RouteTimeRange>>(value);
            var first = ranges?.FirstOrDefault();
            if (first is null) return;
            _timeRangeStart = string.IsNullOrWhiteSpace(first.Start) ? "00:00" : first.Start;
            _timeRangeEnd = string.IsNullOrWhiteSpace(first.End) ? "23:59" : first.End;
            OnPropertyChanged(nameof(TimeRangeStart));
            OnPropertyChanged(nameof(TimeRangeEnd));
        }
        catch (JsonException)
        {
            _timeRangeStart = "00:00";
            _timeRangeEnd = "23:59";
            OnPropertyChanged(nameof(TimeRangeStart));
            OnPropertyChanged(nameof(TimeRangeEnd));
        }
    }

    private void UpdateTimeRangesJson()
    {
        if (string.Equals(AvailabilityMode, "AllDay", StringComparison.OrdinalIgnoreCase)) return;
        var value = JsonSerializer.Serialize(new[]
        {
            new RouteTimeRange
            {
                Start = TimeRangeStart,
                End = TimeRangeEnd
            }
        });
        SetProperty(ref _timeRangesJson, value, nameof(TimeRangesJson));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
        OnPropertyChanged(nameof(IsDisabled));
    }
}

public sealed class RouteTimeRange
{
    public string Start { get; set; } = "00:00";
    public string End { get; set; } = "23:59";
}
