using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class DetectionViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty] private ObservableCollection<DetectionModelGroup> _groups = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private int _progressPercent;

    public DetectionViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasGroups => Groups.Count > 0;
    public bool HasNoGroups => !HasGroups;
    public bool ShowEmptyState => !IsLoading && HasNoGroups;
    public bool CanProbe => !IsBusy && !IsLoading && HasGroups;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var response = await _apiService.SendAsync<DetectionMatrix>(HttpMethod.Get, "/api/admin/detection/matrix", null);
            Groups = new ObservableCollection<DetectionModelGroup>(response.ModelGroups);
            OnPropertyChanged(nameof(HasGroups));
            OnPropertyChanged(nameof(HasNoGroups));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(CanProbe));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanProbe));
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task ProbeMappingAsync(DetectionSiteStatus? site)
    {
        if (site is null || IsBusy) return;
        IsBusy = true;
        try
        {
            await _apiService.SendAsync<ProbeResultItem>(HttpMethod.Post, $"/api/admin/detection/probe/{site.MappingId}", null);
            await LoadAsync();
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

    [RelayCommand]
    private Task ProbeModelAsync(DetectionModelGroup? group)
    {
        return group is null ? Task.CompletedTask : StartBatchAsync($"/api/admin/detection/probe-model/{group.ModelLibraryItemId}");
    }

    [RelayCommand]
    private Task ProbeAllAsync() => StartBatchAsync("/api/admin/detection/probe-all");

    private async Task StartBatchAsync(string path)
    {
        if (IsBusy || IsLoading || !HasGroups) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        ProgressPercent = 0;
        ProgressText = "正在提交检测任务...";
        try
        {
            var created = await _apiService.SendAsync<Dictionary<string, string>>(HttpMethod.Post, path, null);
            if (!created.TryGetValue("taskId", out var taskId) || string.IsNullOrWhiteSpace(taskId))
            {
                throw new ApiException("检测任务标识无效", string.Empty, 200);
            }

            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(1200);
                var progress = await _apiService.SendAsync<ProbeProgress>(HttpMethod.Get, $"/api/admin/detection/progress/{taskId}", null);
                ProgressPercent = progress.Total == 0 ? 0 : Math.Min(100, progress.Completed * 100 / progress.Total);
                ProgressText = $"检测进度：{progress.Completed} / {progress.Total}";
                if (progress.IsCompleted)
                {
                    ProgressPercent = 100;
                    ProgressText = "检测完成";
                    break;
                }
            }

            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ProgressText = string.Empty;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanProbe));
        }
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanProbe));
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(CanProbe));
    }

    partial void OnGroupsChanged(ObservableCollection<DetectionModelGroup> value)
    {
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(CanProbe));
    }
}
