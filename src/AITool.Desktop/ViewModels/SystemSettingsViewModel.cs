using System.Net.Http;
using AITool.Desktop.Models;
using AITool.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AITool.Desktop.ViewModels;

public partial class SystemSettingsViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty] private SystemSettings _settings = new();
    [ObservableProperty] private string _clearSource = string.Empty;
    [ObservableProperty] private string _clearStartTime = string.Empty;
    [ObservableProperty] private string _clearEndTime = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isClearingLogs;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public SystemSettingsViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool CanSave => !IsLoading && !IsSaving;
    public bool CanClearLogs => !IsLoading && !IsSaving && !IsClearingLogs;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            Settings = await _apiService.SendAsync<SystemSettings>(HttpMethod.Get, "/api/admin/system/settings", null);
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsLoading = false; OnPropertyChanged(nameof(CanSave)); }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Put, "/api/admin/system/settings", Settings);
            Message = "设置已保存";
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsSaving = false; OnPropertyChanged(nameof(CanSave)); }
    }

    [RelayCommand]
    private async Task ClearFilteredLogsAsync()
    {
        if (!CanClearLogs) return;
        IsClearingLogs = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<Dictionary<string, int>>(HttpMethod.Post, "/api/admin/system/clear-usage-logs?clearAll=false", new
            {
                source = string.IsNullOrWhiteSpace(ClearSource) ? null : ClearSource.Trim(),
                startTime = string.IsNullOrWhiteSpace(ClearStartTime) ? null : ClearStartTime,
                endTime = string.IsNullOrWhiteSpace(ClearEndTime) ? null : ClearEndTime
            });
            Message = $"已清空 {result.GetValueOrDefault("deletedCount")} 条日志";
            await LoadAsync();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally
        {
            IsClearingLogs = false;
        }
    }

    [RelayCommand]
    private async Task ClearAllLogsAsync()
    {
        if (!CanClearLogs) return;
        IsClearingLogs = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<Dictionary<string, int>>(HttpMethod.Post, "/api/admin/system/clear-usage-logs?clearAll=true", null);
            Message = $"已清空 {result.GetValueOrDefault("deletedCount")} 条日志";
            await LoadAsync();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally
        {
            IsClearingLogs = false;
        }
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanClearLogs));
    }

    partial void OnIsClearingLogsChanged(bool value) => OnPropertyChanged(nameof(CanClearLogs));
}
