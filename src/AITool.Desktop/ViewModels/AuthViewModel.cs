using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class AuthViewModel : ViewModelBase
{
    private readonly ApiService _apiService;
    private readonly TokenStore _tokenStore;

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private AuthStatus? _status;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public AuthViewModel(ApiService apiService, TokenStore tokenStore)
    {
        _apiService = apiService;
        _tokenStore = tokenStore;
        _serverUrl = tokenStore.Settings.ServerUrl;
    }

    public bool IsSetupMode => Status is { HasPassword: false };

    public string PageSubtitle => IsSetupMode ? "首次使用，请设置管理密码" : "请输入管理密码登录";

    public string SubmitButtonText => IsSetupMode ? "初始化并登录" : "登录";

    public bool CanSubmit => !IsBusy;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public event EventHandler? LoginSucceeded;

    public async Task InitializeAsync()
    {
        try
        {
            _apiService.ConfigureBaseAddress();
            Status = await _apiService.GetAuthStatusAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    partial void OnStatusChanged(AuthStatus? value)
    {
        OnPropertyChanged(nameof(IsSetupMode));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(SubmitButtonText));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSubmit));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnServerUrlChanged(string value)
    {
        _tokenStore.SaveServerUrl(value);
        try
        {
            _apiService.ConfigureBaseAddress();
        }
        catch
        {
            // 提交时会再次校验服务端地址并显示具体错误。
        }
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = string.Empty;
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out _))
        {
            ErrorMessage = "请输入有效的服务端地址";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "请输入密码";
            return;
        }

        if (IsSetupMode)
        {
            if (Password.Length < 6)
            {
                ErrorMessage = "密码长度至少 6 位";
                return;
            }

            if (Password != ConfirmPassword)
            {
                ErrorMessage = "两次输入的密码不一致";
                return;
            }
        }

        IsBusy = true;
        try
        {
            _tokenStore.SaveServerUrl(ServerUrl);
            _apiService.ConfigureBaseAddress();
            var tokens = IsSetupMode
                ? await _apiService.SetupAsync(Password, ConfirmPassword)
                : await _apiService.LoginAsync(Password);
            _tokenStore.SaveTokens(tokens);
            Status = await _apiService.GetAuthStatusAsync();
            Password = string.Empty;
            ConfirmPassword = string.Empty;
            LoginSucceeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
