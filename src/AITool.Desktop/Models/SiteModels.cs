using System.Collections.ObjectModel;

namespace AITool.Desktop.Models;

public partial class SiteListItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPathMode { get; set; } = "standard-root";
    public string ApiKeyMasked { get; set; } = string.Empty;
    public bool SupportsOpenAi { get; set; }
    public bool SupportsAnthropic { get; set; }
    public string ProtocolType { get; set; } = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isEnabled;

    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";

    public DateTimeOffset CreatedAt { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
    }
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

public sealed class SitePayload
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPathMode { get; set; } = "standard-root";
    public string ApiKey { get; set; } = string.Empty;
    public bool SupportsOpenAi { get; set; } = true;
    public bool SupportsAnthropic { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class SiteEditForm : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
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
