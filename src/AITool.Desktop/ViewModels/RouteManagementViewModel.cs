using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class RouteManagementViewModel : ViewModelBase
{
    private bool _compatibilityLoaded;

    public RouteManagementViewModel(ApiService apiService)
    {
        Routes = new RoutesViewModel(apiService);
        Compatibility = new CompatibilityViewModel(apiService);
    }

    public RoutesViewModel Routes { get; }
    public CompatibilityViewModel Compatibility { get; }

    [ObservableProperty]
    private bool _isRoutesTab = true;

    public bool IsCompatibilityTab => !IsRoutesTab;

    public async Task LoadAsync()
    {
        await Routes.LoadAsync();
    }

    [RelayCommand]
    private async Task SelectTabAsync(string? tab)
    {
        var isRoutesTab = !string.Equals(tab, "compatibility", StringComparison.OrdinalIgnoreCase);
        IsRoutesTab = isRoutesTab;

        if (!isRoutesTab && !_compatibilityLoaded)
        {
            await Compatibility.LoadAsync();
            _compatibilityLoaded = true;
        }
    }

    public async Task RefreshAsync()
    {
        if (IsRoutesTab)
        {
            await Routes.LoadAsync();
            return;
        }

        await Compatibility.LoadAsync();
        _compatibilityLoaded = true;
    }

    partial void OnIsRoutesTabChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCompatibilityTab));
    }
}
