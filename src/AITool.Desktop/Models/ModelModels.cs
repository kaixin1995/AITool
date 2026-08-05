using System.Collections.ObjectModel;

namespace AITool.Desktop.Models;

public sealed class ModelListItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isEnabled;

    public string Id { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ToggleActionText));
            }
        }
    }

    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";
    public string OverrideReasoningEffort { get; set; } = string.Empty;
    public string? CompatibilityProfileId { get; set; }
    public int SiteCount { get; set; }
}

public sealed class ModelVendorGroup
{
    public string VendorName { get; set; } = string.Empty;
    public string IconSvgBody { get; set; } = string.Empty;
    public string HeaderBackground { get; set; } = "#F8FAFC";
    public ObservableCollection<ModelListItem> Models { get; set; } = new();
}

public sealed class ModelListResponse
{
    public List<ModelVendorGroup> VendorGroups { get; set; } = new();
}

/// <summary>
/// 模型详情及其站点映射，用于桌面端映射管理面板。
/// </summary>
public sealed class ModelDetail
{
    public string Id { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string OverrideReasoningEffort { get; set; } = string.Empty;
    public string? CompatibilityProfileId { get; set; }
    public List<ModelSiteMapping> SiteMappings { get; set; } = new();
    public List<ModelAvailableSite> AvailableSites { get; set; } = new();
}

/// <summary>
/// 模型与站点之间的远程模型映射。
/// </summary>
public sealed partial class ModelSiteMapping : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string MappingId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsDisabled => !IsEnabled;
    public string StatusText => IsEnabled ? "已启用" : "已停用";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private int _maxConcurrency;

    public string ConcurrencyText => MaxConcurrency == 0 ? "不限制" : MaxConcurrency.ToString();

    partial void OnMaxConcurrencyChanged(int value) => OnPropertyChanged(nameof(ConcurrencyText));
}

/// <summary>
/// 可供模型新增映射的启用站点。
/// </summary>
public sealed class ModelAvailableSite
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class ModelPayload
{
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string OverrideReasoningEffort { get; set; } = string.Empty;
    public string? CompatibilityProfileId { get; set; }
}

public sealed class ModelEditForm : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private string _modelName = string.Empty;
    private string _displayName = string.Empty;
    private bool _isEnabled = true;
    private string _overrideReasoningEffort = string.Empty;

    public string ModelName { get => _modelName; set => SetProperty(ref _modelName, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string OverrideReasoningEffort { get => _overrideReasoningEffort; set => SetProperty(ref _overrideReasoningEffort, value); }

    public void Reset()
    {
        ModelName = string.Empty;
        DisplayName = string.Empty;
        IsEnabled = true;
        OverrideReasoningEffort = string.Empty;
    }
}
