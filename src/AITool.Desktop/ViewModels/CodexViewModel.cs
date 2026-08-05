using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class CodexViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty] private ObservableCollection<CodexAccount> _accounts = new();
    [ObservableProperty] private string _oAuthUrl = string.Empty;
    [ObservableProperty] private string _oAuthCallbackUrl = string.Empty;
    [ObservableProperty] private string _oAuthDisplayName = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isOAuthBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _message = string.Empty;

    public CodexViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasNoAccounts => !HasAccounts;
    public bool CanCompleteOAuth => !IsOAuthBusy;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var accounts = await _apiService.SendAsync<List<CodexAccount>>(HttpMethod.Get, "/api/admin/codex/accounts", null);
            Accounts = new ObservableCollection<CodexAccount>(accounts);
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(HasNoAccounts));
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task StartOAuthAsync()
    {
        IsOAuthBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<CodexOAuthResult>(HttpMethod.Post, "/api/admin/codex/start-oauth", new { });
            OAuthUrl = result.Url;
            Message = "请在浏览器完成授权后，将回调 URL 粘贴到下方。";
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsOAuthBusy = false; OnPropertyChanged(nameof(CanCompleteOAuth)); }
    }

    [RelayCommand]
    private async Task CompleteOAuthAsync()
    {
        if (string.IsNullOrWhiteSpace(OAuthCallbackUrl))
        {
            ErrorMessage = "请先粘贴 OAuth 回调 URL";
            return;
        }

        if (!CanCompleteOAuth) return;
        IsOAuthBusy = true;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, "/api/admin/codex/complete-oauth", new { callbackUrl = OAuthCallbackUrl.Trim(), displayName = OAuthDisplayName.Trim() });
            Message = "OAuth 账号已添加";
            OAuthUrl = string.Empty;
            OAuthCallbackUrl = string.Empty;
            OAuthDisplayName = string.Empty;
            await LoadAsync();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsOAuthBusy = false; OnPropertyChanged(nameof(CanCompleteOAuth)); }
    }

    [RelayCommand]
    private async Task ToggleAsync(CodexAccount? account)
    {
        if (account is null) return;
        try { await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/codex/accounts/{account.Id}/toggle", null); account.IsEnabled = !account.IsEnabled; }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    [RelayCommand]
    private async Task RefreshQuotaAsync(CodexAccount? account)
    {
        if (account is null) return;
        try { await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/codex/accounts/{account.Id}/refresh-quota", null); Message = "额度已刷新"; await LoadAsync(); }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    [RelayCommand]
    private async Task DeleteAsync(CodexAccount? account)
    {
        if (account is null) return;
        try { await _apiService.SendAsync<object>(HttpMethod.Delete, $"/api/admin/codex/accounts/{account.Id}", null); await LoadAsync(); }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnIsOAuthBusyChanged(bool value) => OnPropertyChanged(nameof(CanCompleteOAuth));
}
