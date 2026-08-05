using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class ModelHealthViewModel : ViewModelBase
{
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _suppressRangeReload;

    [ObservableProperty] private ObservableCollection<ModelHealthMonitoredModel> _monitoredModels = new();
    [ObservableProperty] private ObservableCollection<ModelHealthModelOption> _availableModels = new();
    [ObservableProperty] private ObservableCollection<ModelHealthRangeOption> _rangeOptions = new();
    [ObservableProperty] private ModelHealthRangeOption? _selectedRange;
    [ObservableProperty] private ModelHealthModelOption? _selectedModel;
    [ObservableProperty] private string _availableKeyword = string.Empty;
    [ObservableProperty] private string _modelKeyword = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public ModelHealthViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMonitoredModels => MonitoredModels.Count > 0;
    public bool HasNoMonitoredModels => !HasMonitoredModels;
    public bool HasAvailableModels => AvailableModels.Count > 0;
    public bool HasNoAvailableModels => !HasAvailableModels;
    public bool CanAddMonitor => SelectedModel is not null && !IsSaving && !IsLoading;
    public bool CanRemoveMonitor => !IsSaving;
    public bool IsEmptyAfterFilter => HasMonitoredModels && !FilteredModels.Any();
    public bool HasNoFilteredAvailableModels => HasAvailableModels && !FilteredAvailableModels.Any();

    public IEnumerable<ModelHealthModelOption> FilteredAvailableModels
    {
        get
        {
            var keyword = AvailableKeyword.Trim();
            return string.IsNullOrWhiteSpace(keyword)
                ? AvailableModels
                : AvailableModels.Where(model =>
                    model.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IEnumerable<ModelHealthMonitoredModel> FilteredModels
    {
        get
        {
            var keyword = ModelKeyword.Trim();
            return string.IsNullOrWhiteSpace(keyword)
                ? MonitoredModels
                : MonitoredModels.Where(model =>
                    model.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task LoadAsync()
    {
        await _loadLock.WaitAsync();
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var range = string.IsNullOrWhiteSpace(SelectedRange?.Value)
                ? "7d"
                : SelectedRange.Value;
            var dashboard = await _apiService.SendAsync<ModelHealthDashboard>(
                HttpMethod.Get,
                $"/api/admin/model-health?range={Uri.EscapeDataString(range)}",
                null);

            foreach (var model in dashboard.MonitoredModels)
            {
                if (dashboard.HealthData.TryGetValue(model.ModelLibraryItemId, out var sites))
                {
                    model.SetSites(sites);
                }
                else
                {
                    model.SetSites(Array.Empty<ModelHealthSite>());
                }
            }

            MonitoredModels = new ObservableCollection<ModelHealthMonitoredModel>(dashboard.MonitoredModels);
            AvailableModels = new ObservableCollection<ModelHealthModelOption>(dashboard.AvailableModels);
            RangeOptions = new ObservableCollection<ModelHealthRangeOption>(dashboard.RangeOptions);

            _suppressRangeReload = true;
            SelectedRange = RangeOptions.FirstOrDefault(option =>
                string.Equals(option.Value, range, StringComparison.OrdinalIgnoreCase))
                ?? RangeOptions.FirstOrDefault();
            _suppressRangeReload = false;
            NotifyCollectionProperties();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
            _loadLock.Release();
            NotifyCanAddMonitor();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task AddMonitorAsync()
    {
        if (!CanAddMonitor || SelectedModel is null) return;
        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/model-health/{Uri.EscapeDataString(SelectedModel.Id)}/monitor",
                null);
            SelectedModel = null;
            AvailableKeyword = string.Empty;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsSaving = false;
            NotifyCanAddMonitor();
        }
    }

    [RelayCommand]
    private async Task RemoveMonitorAsync(ModelHealthMonitoredModel? model)
    {
        if (model is null || IsSaving) return;
        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Delete,
                $"/api/admin/model-health/{Uri.EscapeDataString(model.ModelLibraryItemId)}/monitor",
                null);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsSaving = false;
            NotifyCanAddMonitor();
        }
    }

    [RelayCommand]
    private void ToggleDetails(ModelHealthMonitoredModel? model)
    {
        if (model is not null)
        {
            model.IsExpanded = !model.IsExpanded;
        }
    }

    private void NotifyCollectionProperties()
    {
        OnPropertyChanged(nameof(FilteredModels));
        OnPropertyChanged(nameof(HasMonitoredModels));
        OnPropertyChanged(nameof(HasNoMonitoredModels));
        OnPropertyChanged(nameof(HasAvailableModels));
        OnPropertyChanged(nameof(HasNoAvailableModels));
        OnPropertyChanged(nameof(FilteredAvailableModels));
        OnPropertyChanged(nameof(HasNoFilteredAvailableModels));
        OnPropertyChanged(nameof(IsEmptyAfterFilter));
    }

    private void NotifyCanAddMonitor()
    {
        OnPropertyChanged(nameof(CanAddMonitor));
        OnPropertyChanged(nameof(CanRemoveMonitor));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnAvailableKeywordChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredAvailableModels));
        OnPropertyChanged(nameof(HasNoFilteredAvailableModels));
    }

    partial void OnModelKeywordChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredModels));
        OnPropertyChanged(nameof(IsEmptyAfterFilter));
    }

    partial void OnSelectedModelChanged(ModelHealthModelOption? value) => NotifyCanAddMonitor();

    partial void OnIsLoadingChanged(bool value) => NotifyCanAddMonitor();

    partial void OnIsSavingChanged(bool value) => NotifyCanAddMonitor();

    partial void OnSelectedRangeChanged(ModelHealthRangeOption? value)
    {
        if (!_suppressRangeReload && value is not null)
        {
            _ = LoadAsync();
        }
    }
}
