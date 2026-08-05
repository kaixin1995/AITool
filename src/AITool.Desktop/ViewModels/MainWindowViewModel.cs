using CommunityToolkit.Mvvm.ComponentModel;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ApiService _apiService;
    private readonly TokenStore _tokenStore;
    private readonly NavigationService _navigationService;

    [ObservableProperty]
    private object? _currentViewModel;

    private AuthViewModel? _authViewModel;
    private MainShellViewModel? _mainShellViewModel;

    public MainWindowViewModel(
        ApiService apiService,
        TokenStore tokenStore,
        NavigationService navigationService)
    {
        _apiService = apiService;
        _tokenStore = tokenStore;
        _navigationService = navigationService;
    }

    public async Task InitializeAsync()
    {
        _authViewModel = new AuthViewModel(_apiService, _tokenStore);
        _authViewModel.LoginSucceeded += OnLoginSucceeded;
        CurrentViewModel = _authViewModel;
        await _authViewModel.InitializeAsync();

        if (_authViewModel.Status?.IsAuthenticated == true)
        {
            await ShowMainShellAsync();
        }
    }

    private async void OnLoginSucceeded(object? sender, EventArgs args)
    {
        await ShowMainShellAsync();
    }

    private async Task ShowMainShellAsync()
    {
        if (_authViewModel?.Status is null) return;

        _mainShellViewModel = new MainShellViewModel(
            _apiService,
            _navigationService,
            _authViewModel.Status);
        _mainShellViewModel.LogoutCompleted += OnLogoutCompleted;
        CurrentViewModel = _mainShellViewModel;
        await _mainShellViewModel.InitializeAsync();
    }

    private void OnLogoutCompleted(object? sender, EventArgs args)
    {
        _mainShellViewModel = null;
        _authViewModel = new AuthViewModel(_apiService, _tokenStore);
        _authViewModel.LoginSucceeded += OnLoginSucceeded;
        CurrentViewModel = _authViewModel;
        _ = _authViewModel.InitializeAsync();
    }
}
