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
    [ObservableProperty] private CodexInspectionStatus? _inspectionStatus;
    [ObservableProperty] private CodexInspectionRunResult? _inspectionLastRun;
    [ObservableProperty] private ObservableCollection<CodexInspectionLog> _inspectionLogs = new();
    [ObservableProperty] private bool _inspectionRunning;
    [ObservableProperty] private bool _inspectionDisabled;
    [ObservableProperty] private string _inspectionError = string.Empty;

    public CodexViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasNoAccounts => !HasAccounts;
    public bool CanCompleteOAuth => !IsOAuthBusy;
    public bool HasOAuthSession => !string.IsNullOrWhiteSpace(OAuthUrl);
    public bool HasInspection => InspectionStatus is not null && !InspectionDisabled;
    public bool HasInspectionLastRun => InspectionLastRun is not null;
    public bool HasInspectionLogs => InspectionLogs.Count > 0;
    public bool HasNoInspectionLogs => !HasInspectionLogs;
    public bool HasInspectionError => !string.IsNullOrWhiteSpace(InspectionError);
    public bool IsInspectionIdle => !InspectionRunning && InspectionStatus?.IsRunning != true;
    public string InspectionStateText => InspectionRunning || InspectionStatus?.IsRunning == true ? "巡检中" : "空闲";
    public string InspectionActionButtonText => InspectionRunning ? "巡检中..." : "手动巡检";

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var accounts = await _apiService.SendAsync<List<CodexAccount>>(
                HttpMethod.Get,
                "/api/admin/codex/accounts",
                null);

            Accounts = new ObservableCollection<CodexAccount>(accounts);
            await RefreshExpiringTokensAsync();
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(HasNoAccounts));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }

        await LoadInspectionAsync();
        IsLoading = false;
    }

    private async Task RefreshExpiringTokensAsync()
    {
        var expirationThreshold = DateTimeOffset.UtcNow.AddMinutes(10);
        foreach (var account in Accounts.ToList())
        {
            if (!account.IsEnabled
                || string.IsNullOrWhiteSpace(account.TokenExpiresAt)
                || !DateTimeOffset.TryParse(account.TokenExpiresAt, out var expiresAt)
                || expiresAt > expirationThreshold)
            {
                continue;
            }

            try
            {
                var refreshed = await RefreshTokenCoreAsync(account);
                ReplaceAccount(refreshed);
                Message = $"已自动刷新账号“{account.DisplayName}”的凭证";
            }
            catch
            {
                // 后台服务也会周期刷新；单个账号刷新失败不阻断其他账号展示。
            }
        }
    }

    private async Task LoadInspectionAsync(bool force = false)
    {
        if (InspectionDisabled && !force) return;
        InspectionError = string.Empty;

        try
        {
            InspectionStatus = await _apiService.SendAsync<CodexInspectionStatus>(
                HttpMethod.Get,
                "/api/admin/codex/inspection/status",
                null);
            InspectionDisabled = false;

            var lastRunTask = _apiService.SendAsync<CodexInspectionRunResult?>(
                HttpMethod.Get,
                "/api/admin/codex/inspection/last-run",
                null);
            var logsTask = _apiService.SendAsync<List<CodexInspectionLog>>(
                HttpMethod.Get,
                "/api/admin/codex/inspection/logs",
                null);
            InspectionLastRun = await lastRunTask;
            InspectionLogs = new ObservableCollection<CodexInspectionLog>(await logsTask);
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            InspectionDisabled = true;
            InspectionStatus = null;
            InspectionLastRun = null;
            InspectionLogs.Clear();
        }
        catch (Exception exception)
        {
            InspectionError = exception.Message;
        }

        NotifyInspectionProperties();
    }

    private async Task<CodexAccount> RefreshTokenCoreAsync(CodexAccount account)
    {
        return await _apiService.SendAsync<CodexAccount>(
            HttpMethod.Post,
            $"/api/admin/codex/accounts/{account.Id}/refresh-token",
            null);
    }

    private void ReplaceAccount(CodexAccount account)
    {
        var index = Accounts.ToList().FindIndex(item => item.Id == account.Id);
        if (index >= 0)
        {
            Accounts[index] = account;
        }
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
            var result = await _apiService.SendAsync<CodexOAuthResult>(
                HttpMethod.Post,
                "/api/admin/codex/start-oauth",
                new { });
            OAuthUrl = result.Url;
            Message = "请在浏览器完成授权后，将回调 URL 粘贴到下方。";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsOAuthBusy = false;
            OnPropertyChanged(nameof(CanCompleteOAuth));
        }
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
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                "/api/admin/codex/complete-oauth",
                new
                {
                    callbackUrl = OAuthCallbackUrl.Trim(),
                    displayName = OAuthDisplayName.Trim()
                });
            Message = "OAuth 账号已添加";
            OAuthUrl = string.Empty;
            OAuthCallbackUrl = string.Empty;
            OAuthDisplayName = string.Empty;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsOAuthBusy = false;
            OnPropertyChanged(nameof(CanCompleteOAuth));
        }
    }

    [RelayCommand]
    private async Task RefreshTokenAsync(CodexAccount? account)
    {
        if (account is null) return;
        try
        {
            ReplaceAccount(await RefreshTokenCoreAsync(account));
            Message = $"账号“{account.DisplayName}”的凭证已刷新";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(CodexAccount? account)
    {
        if (account is null) return;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/codex/accounts/{account.Id}/toggle",
                null);
            account.IsEnabled = !account.IsEnabled;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshQuotaAsync(CodexAccount? account)
    {
        if (account is null) return;
        try
        {
            if (IsTokenExpiring(account))
            {
                ReplaceAccount(await RefreshTokenCoreAsync(account));
            }

            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/codex/accounts/{account.Id}/refresh-quota",
                null);
            Message = "额度已刷新";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private static bool IsTokenExpiring(CodexAccount account)
    {
        return DateTimeOffset.TryParse(account.TokenExpiresAt, out var expiresAt)
            && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(10);
    }

    [RelayCommand]
    private async Task DeleteAsync(CodexAccount? account)
    {
        if (account is null) return;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Delete,
                $"/api/admin/codex/accounts/{account.Id}",
                null);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task RunInspectionAsync(bool force)
    {
        if (InspectionRunning) return;
        InspectionRunning = true;
        InspectionError = string.Empty;
        NotifyInspectionProperties();
        try
        {
            InspectionLastRun = await _apiService.SendAsync<CodexInspectionRunResult>(
                HttpMethod.Post,
                $"/api/admin/codex/inspection/run?force={force.ToString().ToLowerInvariant()}",
                null);
            Message = force ? "真实巡检已完成" : "手动巡检已完成";
            await LoadInspectionAsync(true);
        }
        catch (Exception exception)
        {
            InspectionError = exception.Message;
        }
        finally
        {
            InspectionRunning = false;
            NotifyInspectionProperties();
        }
    }

    [RelayCommand]
    private Task RefreshInspectionAsync() => LoadInspectionAsync(true);

    [RelayCommand]
    private Task RunManualInspectionAsync() => RunInspectionAsync(false);

    [RelayCommand]
    private Task RunRealInspectionAsync() => RunInspectionAsync(true);

    private void NotifyInspectionProperties()
    {
        OnPropertyChanged(nameof(HasInspection));
        OnPropertyChanged(nameof(HasInspectionLastRun));
        OnPropertyChanged(nameof(HasInspectionLogs));
        OnPropertyChanged(nameof(HasNoInspectionLogs));
        OnPropertyChanged(nameof(HasInspectionError));
        OnPropertyChanged(nameof(IsInspectionIdle));
        OnPropertyChanged(nameof(InspectionStateText));
        OnPropertyChanged(nameof(InspectionActionButtonText));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnOAuthUrlChanged(string value) => OnPropertyChanged(nameof(HasOAuthSession));
    partial void OnIsOAuthBusyChanged(bool value) => OnPropertyChanged(nameof(CanCompleteOAuth));
    partial void OnInspectionRunningChanged(bool value) => NotifyInspectionProperties();
    partial void OnInspectionErrorChanged(string value) => OnPropertyChanged(nameof(HasInspectionError));
}
