using CommunityToolkit.Mvvm.ComponentModel;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ApiService _apiService;
    private readonly SseClient _sseClient;
    private readonly TokenStore _tokenStore;
    private readonly NavigationService _navigationService;

    [ObservableProperty]
    private object? _currentViewModel;

    private AuthViewModel? _authViewModel;
    private MainShellViewModel? _mainShellViewModel;
    private bool _disposed;

    public MainWindowViewModel(
        ApiService apiService,
        SseClient sseClient,
        TokenStore tokenStore,
        NavigationService navigationService)
    {
        _apiService = apiService;
        _sseClient = sseClient;
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

        // 若已有 MainShellViewModel，先取消旧的登出订阅，避免回调多次挂载。
        if (_mainShellViewModel is not null)
        {
            _mainShellViewModel.LogoutCompleted -= OnLogoutCompleted;
            (_mainShellViewModel as IDisposable)?.Dispose();
        }

        _mainShellViewModel = new MainShellViewModel(
            _apiService,
            _sseClient,
            _navigationService,
            _tokenStore,
            _authViewModel.Status);
        _mainShellViewModel.LogoutCompleted += OnLogoutCompleted;
        CurrentViewModel = _mainShellViewModel;
        await _mainShellViewModel.InitializeAsync();
    }

    private void OnLogoutCompleted(object? sender, EventArgs args)
    {
        // 取消旧的 MainShellViewModel 登出订阅并释放其资源（含 Navigated 订阅）。
        if (_mainShellViewModel is not null)
        {
            _mainShellViewModel.LogoutCompleted -= OnLogoutCompleted;
            (_mainShellViewModel as IDisposable)?.Dispose();
        }
        _mainShellViewModel = null;

        // 取消旧的 AuthViewModel 登录订阅，避免回调多次挂载。
        if (_authViewModel is not null)
        {
            _authViewModel.LoginSucceeded -= OnLoginSucceeded;
        }
        _authViewModel = new AuthViewModel(_apiService, _tokenStore);
        _authViewModel.LoginSucceeded += OnLoginSucceeded;
        CurrentViewModel = _authViewModel;
        _ = _authViewModel.InitializeAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        if (_mainShellViewModel is not null)
        {
            _mainShellViewModel.LogoutCompleted -= OnLogoutCompleted;
            _mainShellViewModel.Dispose();
            _mainShellViewModel = null;
        }

        if (_authViewModel is not null)
        {
            _authViewModel.LoginSucceeded -= OnLoginSucceeded;
            _authViewModel = null;
        }

        CurrentViewModel = null;
    }
}
