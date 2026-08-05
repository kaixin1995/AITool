using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class CodexViewModel : ViewModelBase, IDisposable
{
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private Timer? _tokenRefreshTimer;
    private bool _disposed;

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
    [ObservableProperty] private CodexAccount? _editingAccount;
    [ObservableProperty] private string _accountDisplayName = string.Empty;
    [ObservableProperty] private string _accountRefreshToken = string.Empty;
    [ObservableProperty] private bool _isAccountEditorOpen;
    [ObservableProperty] private bool _isAccountSaving;
    [ObservableProperty] private bool _isCredentialImportOpen;
    [ObservableProperty] private bool _isCredentialImporting;
    [ObservableProperty] private string _credentialJson = string.Empty;
    [ObservableProperty] private string _credentialImportResultText = string.Empty;
    [ObservableProperty] private bool _isModelEditorOpen;
    [ObservableProperty] private bool _isModelLoading;
    [ObservableProperty] private CodexAccount? _modelAccount;
    [ObservableProperty] private ObservableCollection<CodexRemoteModelItem> _remoteModels = new();
    [ObservableProperty] private string _modelSearchText = string.Empty;
    [ObservableProperty] private bool _isResetCreditOpen;
    [ObservableProperty] private bool _isResetCreditLoading;
    [ObservableProperty] private bool _isResetCreditSubmitting;
    [ObservableProperty] private CodexAccount? _resetCreditAccount;
    [ObservableProperty] private CodexResetCreditsInfo? _resetCreditInfo;

    public CodexViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasNoAccounts => !IsLoading && !HasError && !HasAccounts;
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
    public string InspectionRunSummary => InspectionLastRun is null
        ? string.Empty
        : $"{InspectionLastRun.RunModeText} · {InspectionLastRun.RefreshModeText}";
    public bool CanSaveAccount => !IsAccountSaving && EditingAccount is not null && !string.IsNullOrWhiteSpace(AccountDisplayName);
    public string EditingAccountEmailText => string.IsNullOrWhiteSpace(EditingAccount?.Email) ? "未提供邮箱" : $"账号：{EditingAccount.Email}";
    public bool CanImportCredentials => !IsCredentialImporting && !string.IsNullOrWhiteSpace(CredentialJson);
    public bool HasCredentialImportResult => !string.IsNullOrWhiteSpace(CredentialImportResultText);
    public IEnumerable<CodexRemoteModelItem> FilteredRemoteModels => RemoteModels.Where(IsModelMatch);
    public int SelectedModelCount => RemoteModels.Count(model => model.IsSelected);
    public string SelectedModelText => $"已选 {SelectedModelCount} 个";
    public bool CanImportSelectedModels => !IsModelLoading && SelectedModelCount > 0;
    public bool AllVisibleModelsSelected
    {
        get => FilteredRemoteModels.Any()
            && FilteredRemoteModels.All(model => model.IsSelected);
        set
        {
            foreach (var model in FilteredRemoteModels)
            {
                model.IsSelected = value;
            }

            NotifyModelSelectionProperties();
        }
    }
    public bool HasResetCreditInfo => ResetCreditInfo is not null;
    public bool HasResetCreditError => ResetCreditInfo is not null && !ResetCreditInfo.Success;
    public bool CanConsumeResetCredit => !IsResetCreditLoading
        && !IsResetCreditSubmitting
        && ResetCreditInfo?.Success == true
        && ResetCreditInfo.AvailableCount > 0;
    public string ResetCreditAccountText => string.IsNullOrWhiteSpace(ResetCreditAccount?.DisplayName)
        ? string.Empty
        : $"账号：{ResetCreditAccount.DisplayName}";

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
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
        }
        finally
        {
            IsLoading = false;
        }

        // 启动后台定时刷新：每 3 分钟检查即将过期的 token 并自动刷新，避免用户手动操作。
        StartTokenRefreshTimer();
    }

    private void StartTokenRefreshTimer()
    {
        if (_disposed) return;

        _tokenRefreshTimer?.Dispose();
        _tokenRefreshTimer = new Timer(
            async _ => await RefreshExpiringTokensAsync(),
            null,
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(3));
    }

    private async Task RefreshExpiringTokensAsync()
    {
        if (_disposed || !await _tokenRefreshLock.WaitAsync(0)) return;
        try
        {
            var expirationThreshold = DateTimeOffset.UtcNow.AddMinutes(10);
            // Timer 回调运行在线程池，读取绑定集合前切回 UI 线程，避免跨线程访问 ObservableCollection。
            var expiringAccounts = await Dispatcher.UIThread.InvokeAsync(() => Accounts
                .Where(account => account.IsEnabled
                    && !string.IsNullOrWhiteSpace(account.TokenExpiresAt)
                    && DateTimeOffset.TryParse(account.TokenExpiresAt, out var expiresAt)
                    && expiresAt <= expirationThreshold)
                .ToList());

            if (_disposed || expiringAccounts.Count == 0) return;

            // 网络刷新保持并行，绑定集合和提示消息统一在 UI 线程提交。
            var refreshTasks = expiringAccounts
                .Select<CodexAccount, Task<(CodexAccount? Account, string? Message)>>(async account =>
                {
                    try
                    {
                        return (await RefreshTokenCoreAsync(account), $"已自动刷新账号“{account.DisplayName}”的凭证");
                    }
                    catch
                    {
                        return (null, null);
                    }
                })
                .ToArray();

            var results = await Task.WhenAll(refreshTasks);
            if (_disposed) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                foreach (var (refreshedAccount, message) in results)
                {
                    if (refreshedAccount is not null)
                    {
                        ReplaceAccount(refreshedAccount);
                        Message = message!;
                    }
                }
            });
        }
        finally
        {
            _tokenRefreshLock.Release();
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
    private void OpenCredentialImport()
    {
        CredentialJson = string.Empty;
        CredentialImportResultText = string.Empty;
        IsCredentialImportOpen = true;
        OnPropertyChanged(nameof(CanImportCredentials));
    }

    [RelayCommand]
    private void CloseCredentialImport()
    {
        IsCredentialImportOpen = false;
        CredentialJson = string.Empty;
        OnPropertyChanged(nameof(CanImportCredentials));
    }

    [RelayCommand]
    private Task ImportCredentialsAsync()
    {
        if (!CanImportCredentials) return Task.CompletedTask;
        return ImportCredentialFilesAsync([("imported.json", CredentialJson)]);
    }

    public async Task<string?> ExportCredentialsJsonAsync()
    {
        if (Accounts.Count == 0)
        {
            Message = "暂无可导出的 Codex 账号";
            return null;
        }

        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<JsonElement>(
                HttpMethod.Post,
                "/api/admin/codex/accounts/export-credentials",
                new { accountIds = Accounts.Select(account => account.Id).ToList() });
            return JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            return null;
        }
    }

    [RelayCommand]
    private async Task OpenModelEditorAsync(CodexAccount? account)
    {
        if (account is null || IsModelLoading) return;

        ModelAccount = account;
        ModelSearchText = string.Empty;
        SetRemoteModels([]);
        IsModelEditorOpen = true;
        IsModelLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var models = await _apiService.SendAsync<List<CodexRemoteModelItem>>(
                HttpMethod.Get,
                $"/api/admin/codex/accounts/{account.Id}/fetch-models",
                null);
            foreach (var model in models)
            {
                model.Alias = string.IsNullOrWhiteSpace(model.ExistingDisplayName)
                    ? model.DisplayName
                    : model.ExistingDisplayName!;
                model.IsSelected = model.ExistingMappingId is null || model.IsEnabled;
            }

            SetRemoteModels(models);
            NotifyModelSelectionProperties();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsModelLoading = false;
            NotifyModelSelectionProperties();
        }
    }

    [RelayCommand]
    private void CloseModelEditor()
    {
        IsModelEditorOpen = false;
        ModelAccount = null;
        SetRemoteModels([]);
        ModelSearchText = string.Empty;
    }

    [RelayCommand]
    private async Task ImportSelectedModelsAsync()
    {
        if (!CanImportSelectedModels || ModelAccount is null) return;

        IsModelLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var selections = RemoteModels.Select(model => new
            {
                remoteModelName = model.RemoteModelName,
                displayName = model.EffectiveDisplayName,
                selected = model.IsSelected
            }).ToList();
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/codex/accounts/{ModelAccount.Id}/import-selected-models",
                new { selections });
            Message = $"已导入 {SelectedModelCount} 个 Codex 模型";
            CloseModelEditor();
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsModelLoading = false;
            NotifyModelSelectionProperties();
        }
    }

    [RelayCommand]
    private async Task OpenResetCreditAsync(CodexAccount? account)
    {
        if (account is null || IsResetCreditLoading) return;

        ResetCreditAccount = account;
        ResetCreditInfo = null;
        IsResetCreditOpen = true;
        IsResetCreditLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            ResetCreditInfo = await _apiService.SendAsync<CodexResetCreditsInfo>(
                HttpMethod.Get,
                $"/api/admin/codex/accounts/{account.Id}/reset-credits",
                null);
            if (ResetCreditInfo.Success == false
                && !string.IsNullOrWhiteSpace(ResetCreditInfo.Error))
            {
                ErrorMessage = ResetCreditInfo.Error;
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsResetCreditLoading = false;
            NotifyResetCreditProperties();
        }
    }

    [RelayCommand]
    private void CloseResetCredit()
    {
        IsResetCreditOpen = false;
        ResetCreditAccount = null;
        ResetCreditInfo = null;
        NotifyResetCreditProperties();
    }

    [RelayCommand]
    private async Task ConsumeResetCreditAsync()
    {
        if (!CanConsumeResetCredit || ResetCreditAccount is null) return;

        IsResetCreditSubmitting = true;
        ErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/codex/accounts/{ResetCreditAccount.Id}/consume-reset-credit",
                null);
            Message = "手动重置额度成功";
            CloseResetCredit();
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsResetCreditSubmitting = false;
            NotifyResetCreditProperties();
        }
    }

    private bool IsModelMatch(CodexRemoteModelItem model)
    {
        var keyword = ModelSearchText.Trim();
        return string.IsNullOrWhiteSpace(keyword)
            || $"{model.RemoteModelName} {model.DisplayName} {model.Alias}"
                .Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SetRemoteModels(IEnumerable<CodexRemoteModelItem> models)
    {
        foreach (var model in RemoteModels)
        {
            model.PropertyChanged -= OnRemoteModelPropertyChanged;
        }

        RemoteModels = new ObservableCollection<CodexRemoteModelItem>(models);
        foreach (var model in RemoteModels)
        {
            model.PropertyChanged += OnRemoteModelPropertyChanged;
        }

        NotifyModelSelectionProperties();
    }

    private void OnRemoteModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CodexRemoteModelItem.IsSelected)
            or nameof(CodexRemoteModelItem.Alias))
        {
            NotifyModelSelectionProperties();
        }
    }

    private void NotifyModelSelectionProperties()
    {
        OnPropertyChanged(nameof(FilteredRemoteModels));
        OnPropertyChanged(nameof(SelectedModelCount));
        OnPropertyChanged(nameof(SelectedModelText));
        OnPropertyChanged(nameof(CanImportSelectedModels));
        OnPropertyChanged(nameof(AllVisibleModelsSelected));
    }

    private void NotifyResetCreditProperties()
    {
        OnPropertyChanged(nameof(HasResetCreditInfo));
        OnPropertyChanged(nameof(HasResetCreditError));
        OnPropertyChanged(nameof(CanConsumeResetCredit));
        OnPropertyChanged(nameof(ResetCreditAccountText));
    }

    public async Task ImportCredentialFilesAsync(
        IReadOnlyList<(string FileName, string JsonText)> files)
    {
        if (IsCredentialImporting || files.Count == 0) return;

        CredentialImportResultText = string.Empty;
        IsCredentialImporting = true;
        ErrorMessage = string.Empty;
        try
        {
            var successes = new List<CodexAccount>();
            var failures = new List<CodexCredentialImportFailure>();

            foreach (var (fileName, jsonText) in files)
            {
                try
                {
                    using var document = JsonDocument.Parse(jsonText);
                    var result = await _apiService.SendAsync<CodexCredentialImportResult>(
                        HttpMethod.Post,
                        $"/api/admin/codex/import-credential?name={Uri.EscapeDataString(fileName)}",
                        document.RootElement);
                    successes.AddRange(result.Successes);
                    failures.AddRange(result.Failures);
                }
                catch (JsonException)
                {
                    failures.Add(new CodexCredentialImportFailure
                    {
                        FileName = fileName,
                        Error = "JSON 格式无效"
                    });
                }
                catch (Exception exception)
                {
                    failures.Add(new CodexCredentialImportFailure
                    {
                        FileName = fileName,
                        Error = exception.Message
                    });
                }
            }

            CredentialImportResultText = $"成功导入 {successes.Count} 个账号，失败 {failures.Count} 个。";
            if (failures.Count > 0)
            {
                CredentialImportResultText += "\n" + string.Join(
                    "\n",
                    failures.Select(f => $"{f.FileName ?? "凭证"}：{f.Error}"));
            }

            Message = $"已导入 {successes.Count} 个 Codex 账号";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsCredentialImporting = false;
            OnPropertyChanged(nameof(CanImportCredentials));
            OnPropertyChanged(nameof(HasCredentialImportResult));
        }
    }

    [RelayCommand]
    private void OpenAccountEditor(CodexAccount? account)
    {
        if (account is null) return;
        EditingAccount = account;
        AccountDisplayName = account.DisplayName;
        AccountRefreshToken = string.Empty;
        IsAccountEditorOpen = true;
        OnPropertyChanged(nameof(CanSaveAccount));
    }

    [RelayCommand]
    private void CloseAccountEditor()
    {
        IsAccountEditorOpen = false;
        EditingAccount = null;
        AccountDisplayName = string.Empty;
        AccountRefreshToken = string.Empty;
        OnPropertyChanged(nameof(CanSaveAccount));
    }

    [RelayCommand]
    private async Task SaveAccountAsync()
    {
        if (!CanSaveAccount || EditingAccount is null) return;
        IsAccountSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var account = await _apiService.SendAsync<CodexAccount>(
                HttpMethod.Put,
                $"/api/admin/codex/accounts/{EditingAccount.Id}",
                new
                {
                    displayName = AccountDisplayName.Trim(),
                    refreshToken = string.IsNullOrWhiteSpace(AccountRefreshToken) ? null : AccountRefreshToken.Trim()
                });
            ReplaceAccount(account);
            Message = "Codex 账号已更新";
            CloseAccountEditor();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsAccountSaving = false;
            OnPropertyChanged(nameof(CanSaveAccount));
        }
    }

    [RelayCommand]
    private async Task ResetQuotaAsync(CodexAccount? account)
    {
        if (account is null) return;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/codex/accounts/{account.Id}/reset-quota",
                null);
            Message = $"账号“{account.DisplayName}”的额度状态已重置";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
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

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasNoAccounts));
    }

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoAccounts));
    partial void OnInspectionLastRunChanged(CodexInspectionRunResult? value)
    {
        OnPropertyChanged(nameof(HasInspectionLastRun));
        OnPropertyChanged(nameof(InspectionRunSummary));
    }
    partial void OnOAuthUrlChanged(string value) => OnPropertyChanged(nameof(HasOAuthSession));
    partial void OnIsOAuthBusyChanged(bool value) => OnPropertyChanged(nameof(CanCompleteOAuth));
    partial void OnIsAccountSavingChanged(bool value) => OnPropertyChanged(nameof(CanSaveAccount));
    partial void OnCredentialJsonChanged(string value) => OnPropertyChanged(nameof(CanImportCredentials));
    partial void OnModelSearchTextChanged(string value) => NotifyModelSelectionProperties();
    partial void OnIsModelLoadingChanged(bool value) => NotifyModelSelectionProperties();
    partial void OnIsResetCreditLoadingChanged(bool value) => NotifyResetCreditProperties();
    partial void OnIsResetCreditSubmittingChanged(bool value) => NotifyResetCreditProperties();
    partial void OnResetCreditInfoChanged(CodexResetCreditsInfo? value) => NotifyResetCreditProperties();
    partial void OnResetCreditAccountChanged(CodexAccount? value) => NotifyResetCreditProperties();
    partial void OnAccountDisplayNameChanged(string value) => OnPropertyChanged(nameof(CanSaveAccount));
    partial void OnEditingAccountChanged(CodexAccount? value)
    {
        OnPropertyChanged(nameof(CanSaveAccount));
        OnPropertyChanged(nameof(EditingAccountEmailText));
    }
    partial void OnInspectionRunningChanged(bool value) => NotifyInspectionProperties();
    partial void OnInspectionErrorChanged(string value) => OnPropertyChanged(nameof(HasInspectionError));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenRefreshTimer?.Dispose();
        _tokenRefreshTimer = null;
    }
}
