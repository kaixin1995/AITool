using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class ModelHealthViewModel : ViewModelBase, IDisposable
{
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;
    private bool _suppressRangeReload;
    private bool _disposed;

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
    [ObservableProperty] private string _activeTab = "health";

    public ModelHealthViewModel(ApiService apiService)
    {
        _apiService = apiService;
        RouteFallback = new RouteFallbackViewModel(apiService);
    }

    public RouteFallbackViewModel RouteFallback { get; }
    public bool IsHealthTab => string.Equals(ActiveTab, "health", StringComparison.Ordinal);
    public bool IsFallbackTab => string.Equals(ActiveTab, "fallback", StringComparison.Ordinal);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMonitoredModels => MonitoredModels.Count > 0;
    public bool HasNoMonitoredModels => !IsLoading && !HasError && !HasMonitoredModels;
    public bool HasAvailableModels => AvailableModels.Count > 0;
    public bool HasNoAvailableModels => !HasAvailableModels;
    public bool CanAddMonitor => SelectedModel is not null && !IsSaving && !IsLoading;
    public bool CanRetry => !IsLoading && !IsSaving;
    public bool CanRemoveMonitor => !IsSaving && !IsLoading;
    public bool IsEmptyAfterFilter => !IsLoading && !HasError && HasMonitoredModels && !FilteredModels.Any();
    public bool IsContentVisible => !IsLoading && !HasError && HasMonitoredModels && FilteredModels.Any();
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
        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _loadCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _loadGeneration);
        var lockAcquired = false;

        try
        {
            await _loadLock.WaitAsync(localCancellation.Token);
            lockAcquired = true;
            if (IsCurrentLoad(generation, localCancellation))
            {
                IsLoading = true;
                ErrorMessage = string.Empty;
            }

            var range = string.IsNullOrWhiteSpace(SelectedRange?.Value)
                ? "7d"
                : SelectedRange.Value;
            var dashboard = await _apiService.SendAsync<ModelHealthDashboard>(
                HttpMethod.Get,
                $"/api/admin/model-health?range={Uri.EscapeDataString(range)}",
                null,
                true,
                localCancellation.Token);

            if (!IsCurrentLoad(generation, localCancellation)) return;
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
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 时间范围切换或页面销毁时取消旧请求，不显示为普通错误。
        }
        catch (Exception exception) when (IsCurrentLoad(generation, localCancellation))
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            if (IsCurrentLoad(generation, localCancellation))
            {
                IsLoading = false;
                Interlocked.CompareExchange(ref _loadCancellation, null, localCancellation);
                NotifyCanAddMonitor();
            }

            if (lockAcquired) _loadLock.Release();
            localCancellation.Dispose();
        }
    }

    private bool IsCurrentLoad(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _loadGeneration)
            && ReferenceEquals(_loadCancellation, localCancellation);

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
    private async Task SelectTabAsync(string? tab)
    {
        var nextTab = string.Equals(tab, "fallback", StringComparison.Ordinal) ? "fallback" : "health";
        if (string.Equals(ActiveTab, nextTab, StringComparison.Ordinal)) return;

        ActiveTab = nextTab;
        if (IsFallbackTab)
        {
            await RouteFallback.LoadAsync(1);
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
        OnPropertyChanged(nameof(IsContentVisible));
    }

    private void NotifyCanAddMonitor()
    {
        OnPropertyChanged(nameof(CanAddMonitor));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemoveMonitor));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasNoMonitoredModels));
        OnPropertyChanged(nameof(IsEmptyAfterFilter));
        OnPropertyChanged(nameof(IsContentVisible));
    }

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

    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsHealthTab));
        OnPropertyChanged(nameof(IsFallbackTab));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        NotifyCanAddMonitor();
        OnPropertyChanged(nameof(HasNoMonitoredModels));
        OnPropertyChanged(nameof(IsEmptyAfterFilter));
        OnPropertyChanged(nameof(IsContentVisible));
    }

    partial void OnIsSavingChanged(bool value) => NotifyCanAddMonitor();

    partial void OnSelectedRangeChanged(ModelHealthRangeOption? value)
    {
        if (!_suppressRangeReload && value is not null)
        {
            _ = LoadAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        var loadCancellation = Interlocked.Exchange(ref _loadCancellation, null);
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();

        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _loadLock.Dispose();
    }
}
