using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class DeveloperInvocationsViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty] private ObservableCollection<DeveloperInvocationSummary> _items = new();
    [ObservableProperty] private DeveloperInvocationDetail? _selectedDetail;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDetailLoading;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _pendingCount;

    public DeveloperInvocationsViewModel(ApiService apiService) => _apiService = apiService;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !HasItems;
    public bool HasDetail => SelectedDetail is not null;
    public bool CanPrevious => Page > 1 && !IsLoading;
    public bool CanNext => Page < TotalPages && !IsLoading;
    public string PageText => TotalPages == 0 ? "第 0 / 0 页" : $"第 {Page} / {TotalPages} 页";

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var response = await _apiService.SendAsync<DeveloperListResponse>(HttpMethod.Get, $"/api/admin/developer/invocations/list?page={Page}&pageSize=40", null);
            Items = new ObservableCollection<DeveloperInvocationSummary>(response.Entries);
            Page = response.Page;
            TotalPages = response.TotalPages;
            TotalCount = response.TotalCount;
            FailedCount = response.FailedCount;
            PendingCount = response.PendingCount;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoItems));
            UpdatePaging();
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsLoading = false; UpdatePaging(); }
    }

    [RelayCommand] private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CanPrevious) { Page--; await LoadAsync(); }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CanNext) { Page++; await LoadAsync(); }
    }

    [RelayCommand]
    private async Task OpenDetailAsync(DeveloperInvocationSummary? item)
    {
        if (item is null) return;
        IsDetailLoading = true;
        try { SelectedDetail = await _apiService.SendAsync<DeveloperInvocationDetail>(HttpMethod.Get, $"/api/admin/developer/invocations/{Uri.EscapeDataString(item.TraceId)}?summarize=false", null); }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsDetailLoading = false; }
    }

    [RelayCommand] private void CloseDetail() => SelectedDetail = null;

    private void UpdatePaging()
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSelectedDetailChanged(DeveloperInvocationDetail? value) => OnPropertyChanged(nameof(HasDetail));
}
