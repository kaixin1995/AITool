using System.Globalization;
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
    public IReadOnlyList<ClearLogSourceOption> ClearSourceOptions { get; } =
    [
        new() { Value = string.Empty, Label = "全部来源" },
        new() { Value = "proxy", Label = "代理" },
        new() { Value = "chat", Label = "对话测试" },
        new() { Value = "claude-code", Label = "Claude Code" },
        new() { Value = "codex", Label = "Codex" },
        new() { Value = "open-code", Label = "Open Code" },
        new() { Value = "zcode", Label = "ZCode" },
        new() { Value = "detection-manual", Label = "手动检测" },
        new() { Value = "detection-task", Label = "定时检测" }
    ];
    public string ClearScopeText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ClearSource))
            {
                var label = ClearSourceOptions.FirstOrDefault(option => option.Value == ClearSource)?.Label ?? ClearSource;
                parts.Add($"来源 {label}");
            }

            if (!string.IsNullOrWhiteSpace(ClearStartTime)) parts.Add($"从 {ClearStartTime}");
            if (!string.IsNullOrWhiteSpace(ClearEndTime)) parts.Add($"到 {ClearEndTime}");
            return parts.Count == 0 ? "全部 UsageLogs" : string.Join("，", parts);
        }
    }

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

    private bool TryBuildClearRange(out string? startTime, out string? endTime)
    {
        startTime = null;
        endTime = null;
        startTime = ParseClearTime(ClearStartTime, "开始时间");
        if (!string.IsNullOrWhiteSpace(ClearStartTime) && startTime is null) return false;

        endTime = ParseClearTime(ClearEndTime, "结束时间");
        if (!string.IsNullOrWhiteSpace(ClearEndTime) && endTime is null) return false;

        if (startTime is not null
            && endTime is not null
            && DateTimeOffset.Parse(startTime, CultureInfo.InvariantCulture) >= DateTimeOffset.Parse(endTime, CultureInfo.InvariantCulture))
        {
            ErrorMessage = "结束时间必须晚于开始时间";
            return false;
        }

        return true;
    }

    private string? ParseClearTime(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        ErrorMessage = $"{fieldName}格式无效，请输入例如 2026-08-01 00:00";
        return null;
    }

    [RelayCommand]
    private async Task ClearFilteredLogsAsync()
    {
        if (!CanClearLogs || !TryBuildClearRange(out var startTime, out var endTime)) return;
        IsClearingLogs = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<Dictionary<string, int>>(HttpMethod.Post, "/api/admin/system/clear-usage-logs?clearAll=false", new
            {
                source = string.IsNullOrWhiteSpace(ClearSource) ? null : ClearSource.Trim(),
                startTime,
                endTime
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
    partial void OnClearSourceChanged(string value) => OnPropertyChanged(nameof(ClearScopeText));
    partial void OnClearStartTimeChanged(string value) => OnPropertyChanged(nameof(ClearScopeText));
    partial void OnClearEndTimeChanged(string value) => OnPropertyChanged(nameof(ClearScopeText));
}
