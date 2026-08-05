using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly ApiService _apiService;
    private Action<string>? _navigateCallback;

    [ObservableProperty]
    private DashboardStats? _stats;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public DashboardViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    /// <summary>
    /// 注入导航回调，回调参数为目标页面的导航 Key（例如 "sites"），
    /// 由 MainShellViewModel 提供以触发实际页面切换。
    /// </summary>
    public void SetNavigateCallback(Action<string> navigateCallback)
    {
        _navigateCallback = navigateCallback;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasStats => Stats is not null;
    public bool ShowNoStats => !IsLoading && !HasError && !HasStats;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            Stats = await _apiService.SendAsync<DashboardStats>(
                HttpMethod.Get,
                "/api/admin/dashboard/stats",
                null);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnStatsChanged(DashboardStats? value)
    {
        OnPropertyChanged(nameof(HasStats));
        OnPropertyChanged(nameof(ShowNoStats));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowNoStats));

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowNoStats));
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        return LoadAsync();
    }

    [RelayCommand]
    private void NavigateToSites()
    {
        NavigateTo("sites");
    }

    [RelayCommand]
    private void NavigateToModels()
    {
        NavigateTo("models");
    }

    [RelayCommand]
    private void NavigateToRoutes()
    {
        NavigateTo("routes");
    }

    [RelayCommand]
    private void NavigateToLogs()
    {
        NavigateTo("usage-logs");
    }

    private void NavigateTo(string key)
    {
        _navigateCallback?.Invoke(key);
    }
}
