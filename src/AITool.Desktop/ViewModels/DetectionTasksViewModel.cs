using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class DetectionTasksViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty] private ObservableCollection<DetectionTaskItem> _tasks = new();
    [ObservableProperty] private ObservableCollection<DetectionModelOption> _availableModels = new();
    [ObservableProperty] private string _taskName = string.Empty;
    [ObservableProperty] private string _cronExpression = "*/30 * * * *";
    [ObservableProperty] private DetectionModelOption? _selectedModel;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string? _executingTaskId;

    public DetectionTasksViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasTasks => Tasks.Count > 0;
    public bool HasNoTasks => !HasTasks;
    public bool ShowEmptyState => !IsLoading && !HasError && HasNoTasks;
    public bool CanCreate => !IsSaving && !string.IsNullOrWhiteSpace(TaskName) && !string.IsNullOrWhiteSpace(CronExpression);

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var response = await _apiService.SendAsync<DetectionTaskListResponse>(HttpMethod.Get, "/api/admin/detection-tasks", null);
            Tasks = new ObservableCollection<DetectionTaskItem>(response.Tasks);
            AvailableModels = new ObservableCollection<DetectionModelOption>(response.AvailableModels);
            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(HasNoTasks));
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

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate) return;
        IsSaving = true;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, "/api/admin/detection-tasks", new
            {
                name = TaskName.Trim(),
                cronExpression = CronExpression.Trim(),
                modelLibraryItemId = SelectedModel?.Id
            });
            TaskName = string.Empty;
            CronExpression = "*/30 * * * *";
            SelectedModel = null;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(DetectionTaskItem? task)
    {
        if (task is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/detection-tasks/{task.Id}/toggle", null);
            await LoadAsync();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    [RelayCommand]
    private async Task ExecuteAsync(DetectionTaskItem? task)
    {
        if (task is null || ExecutingTaskId is not null) return;
        ExecutingTaskId = task.Id;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/detection-tasks/{task.Id}/execute", null);
            await LoadAsync();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { ExecutingTaskId = null; }
    }

    [RelayCommand]
    private async Task DeleteAsync(DetectionTaskItem? task)
    {
        if (task is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Delete, $"/api/admin/detection-tasks/{task.Id}", null);
            await LoadAsync();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnTasksChanged(ObservableCollection<DetectionTaskItem> value)
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasNoTasks));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnTaskNameChanged(string value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnCronExpressionChanged(string value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
}
