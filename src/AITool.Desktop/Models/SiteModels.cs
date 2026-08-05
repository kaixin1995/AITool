using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class EndpointPathModeOption
{
    public EndpointPathModeOption(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }
    public string Label { get; }
}

public partial class SiteListItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPathMode { get; set; } = "standard-root";
    public string ApiKeyMasked { get; set; } = string.Empty;
    public bool SupportsOpenAi { get; set; }
    public bool SupportsAnthropic { get; set; }
    public string ProtocolType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isSelected;

    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";
    public bool SupportsResponses => !SupportsOpenAi && !SupportsAnthropic;
    public string StatusBackground => IsEnabled ? "#E8F7EA" : "#F1F5F9";
    public string StatusForeground => IsEnabled ? "#166534" : "#64748B";
    public string CreatedAtText => CreatedAt == default ? "-" : CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public event EventHandler? SelectionChanged;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class SiteDetail
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPathMode { get; set; } = "standard-root";
    public bool SupportsOpenAi { get; set; }
    public bool SupportsAnthropic { get; set; }
    public string ProtocolType { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class SitePayload
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPathMode { get; set; } = "standard-root";
    public string ApiKey { get; set; } = string.Empty;
    public bool SupportsOpenAi { get; set; } = true;
    public bool SupportsAnthropic { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class SiteExportItem : SitePayload
{
    public string Id { get; set; } = string.Empty;
}

public sealed class SiteImportPreviewItem : SitePayload
{
    public bool IsSelected { get; set; } = true;

    public string ProtocolType => !SupportsOpenAi && !SupportsAnthropic
        ? "Responses"
        : SupportsAnthropic && !SupportsOpenAi
            ? "Anthropic"
            : "OpenAI";

    public string ApiKeyMasked => string.IsNullOrWhiteSpace(ApiKey)
        ? string.Empty
        : ApiKey.Length <= 8 ? "***" : $"{ApiKey[..4]}***{ApiKey[^4..]}";
}

public sealed class SiteEditForm : ObservableObject
{
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;
    private string _endpointPathMode = "standard-root";
    private string _apiKey = string.Empty;
    private bool _supportsOpenAi = true;
    private bool _supportsAnthropic;
    private bool _isEnabled = true;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value); }
    public string EndpointPathMode { get => _endpointPathMode; set => SetProperty(ref _endpointPathMode, value); }
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public bool SupportsOpenAi { get => _supportsOpenAi; set => SetProperty(ref _supportsOpenAi, value); }
    public bool SupportsAnthropic { get => _supportsAnthropic; set => SetProperty(ref _supportsAnthropic, value); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    public void Reset()
    {
        Name = string.Empty;
        BaseUrl = string.Empty;
        EndpointPathMode = "standard-root";
        ApiKey = string.Empty;
        SupportsOpenAi = true;
        SupportsAnthropic = false;
        IsEnabled = true;
    }
}

public sealed class RemoteModelInfo
{
    public string RemoteModelName { get; set; } = string.Empty;
    public string? ExistingMappingId { get; set; }
    public bool IsEnabled { get; set; }
    public string? ExistingDisplayName { get; set; }
}

public sealed class SiteFetchResult
{
    public string SiteId { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public List<RemoteModelInfo> Models { get; set; } = new();
}

public sealed class FetchAllProgress
{
    public string TaskId { get; set; } = string.Empty;
    public int TotalSites { get; set; }
    public int CompletedSites { get; set; }
    public bool IsCompleted { get; set; }
    public List<SiteFetchResult> Sites { get; set; } = new();
}

public sealed class FetchAllStartResponse
{
    public string TaskId { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public sealed class ImportSitesResult
{
    public int ImportedCount { get; set; }
}

public sealed class ImportSelectedModelsRequest
{
    public List<ModelSelectionItem> Selections { get; set; } = new();
}

public sealed class ModelSelectionItem
{
    public string SiteId { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Selected { get; set; }
}

public partial class SiteCatalogModelItem : ObservableObject
{
    public SiteCatalogModelItem(string siteId, RemoteModelInfo model)
    {
        SiteId = siteId;
        RemoteModelName = model.RemoteModelName;
        ExistingMappingId = model.ExistingMappingId;
        ExistingIsEnabled = model.IsEnabled;
        DisplayName = string.IsNullOrWhiteSpace(model.ExistingDisplayName)
            ? model.RemoteModelName
            : model.ExistingDisplayName;
        IsSelected = model.ExistingMappingId is null || model.IsEnabled;
    }

    public string SiteId { get; }
    public string RemoteModelName { get; }
    public string? ExistingMappingId { get; private set; }
    public bool ExistingIsEnabled { get; private set; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isSelected;

    public string ExistingStatusText => ExistingMappingId is null
        ? string.Empty
        : ExistingIsEnabled ? "已导入" : "已禁用";

    public event EventHandler? SelectionChanged;

    public void UpdateRemoteState(RemoteModelInfo model)
    {
        ExistingMappingId = model.ExistingMappingId;
        ExistingIsEnabled = model.IsEnabled;
        OnPropertyChanged(nameof(ExistingStatusText));
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class SiteCatalogSiteItem : ObservableObject
{
    public SiteCatalogSiteItem(SiteFetchResult result)
    {
        SiteId = result.SiteId;
        SiteName = result.SiteName;
        Update(result);
    }

    public string SiteId { get; }
    public string SiteName { get; private set; }
    public string Status { get; private set; } = "pending";
    public string? Error { get; private set; }
    public ObservableCollection<SiteCatalogModelItem> Models { get; } = new();
    public bool HasError => !string.IsNullOrWhiteSpace(Error) || Status == "fail";
    public bool HasModels => Models.Count > 0;
    public string StatusText => Status switch
    {
        "running" => "拉取中",
        "success" => "成功",
        "fail" => "失败",
        _ => "等待中"
    };

    public void Update(SiteFetchResult result)
    {
        SiteName = string.IsNullOrWhiteSpace(result.SiteName) ? SiteName : result.SiteName;
        Status = result.Status;
        Error = result.Error;
        OnPropertyChanged(nameof(SiteName));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    public void NotifyModelsChanged()
    {
        OnPropertyChanged(nameof(HasModels));
    }
}
