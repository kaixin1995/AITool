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
            }
        }
    }

    public string StatusText => IsEnabled ? "已启用" : "已停用";
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
