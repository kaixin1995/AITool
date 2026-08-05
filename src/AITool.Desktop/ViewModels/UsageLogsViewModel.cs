using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class UsageLogsViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty] private ObservableCollection<UsageLogItem> _items = new();
    [ObservableProperty] private ObservableCollection<UsageLogFilterItem> _sites = new();
    [ObservableProperty] private ObservableCollection<UsageLogFilterItem> _accessKeys = new();
    [ObservableProperty] private UsageLogFilterItem? _selectedSite;
    [ObservableProperty] private UsageLogFilterItem? _selectedAccessKey;
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _modelKeyword = string.Empty;
    [ObservableProperty] private string _rangeType = "day";
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private UsageLogSummary _summary = new();
    [ObservableProperty] private UsageLogRequestDetail? _selectedDetail;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDetailLoading;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public UsageLogsViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

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
        ErrorMessage = string.Empty;
        try
        {
            var filters = await _apiService.SendAsync<UsageLogFilters>(HttpMethod.Get, "/api/admin/usage-logs/filters", null);
            Sites = new ObservableCollection<UsageLogFilterItem>(filters.Sites);
            AccessKeys = new ObservableCollection<UsageLogFilterItem>(filters.AccessKeys);
            await LoadPageAsync(Page);
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsLoading = false; UpdatePagingProperties(); }
    }

    private async Task LoadPageAsync(int page)
    {
        var query = BuildQuery(page);
        var list = await _apiService.SendAsync<UsageLogListResponse>(HttpMethod.Get, $"/api/admin/usage-logs/list?{query}", null);
        Summary = await _apiService.SendAsync<UsageLogSummary>(HttpMethod.Get, $"/api/admin/usage-logs/summary?{query}", null);
        Items = new ObservableCollection<UsageLogItem>(list.Items);
        Page = list.Page;
        TotalPages = list.TotalPages;
        TotalCount = list.TotalCount;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        UpdatePagingProperties();
    }

    private string BuildQuery(int page)
    {
        var values = new List<string> { $"page={page}", "pageSize=20", $"rangeType={Uri.EscapeDataString(RangeType)}" };
        if (SelectedSite is not null) values.Add($"siteId={Uri.EscapeDataString(SelectedSite.Id)}");
        if (SelectedAccessKey is not null) values.Add($"accessKeyId={Uri.EscapeDataString(SelectedAccessKey.Id)}");
        if (!string.IsNullOrWhiteSpace(Source)) values.Add($"source={Uri.EscapeDataString(Source.Trim())}");
        if (!string.IsNullOrWhiteSpace(Status)) values.Add($"status={Uri.EscapeDataString(Status.Trim())}");
        if (!string.IsNullOrWhiteSpace(ModelKeyword)) values.Add($"modelKeyword={Uri.EscapeDataString(ModelKeyword.Trim())}");
        return string.Join('&', values);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task SearchAsync()
    {
        try { await LoadPageAsync(1); }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CanPrevious) { try { await LoadPageAsync(Page - 1); } catch (Exception exception) { ErrorMessage = exception.Message; } }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CanNext) { try { await LoadPageAsync(Page + 1); } catch (Exception exception) { ErrorMessage = exception.Message; } }
    }

    [RelayCommand]
    private async Task OpenDetailAsync(UsageLogItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.RequestId)) return;
        IsDetailLoading = true;
        try { SelectedDetail = await _apiService.SendAsync<UsageLogRequestDetail>(HttpMethod.Get, $"/api/admin/usage-logs/request-detail/{Uri.EscapeDataString(item.RequestId)}", null); }
        catch (Exception exception) { ErrorMessage = exception.Message; }
        finally { IsDetailLoading = false; }
    }

    [RelayCommand]
    private void CloseDetail() => SelectedDetail = null;

    private void UpdatePagingProperties()
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSelectedDetailChanged(UsageLogRequestDetail? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnPageChanged(int value) => UpdatePagingProperties();
    partial void OnTotalPagesChanged(int value) => UpdatePagingProperties();
    partial void OnIsLoadingChanged(bool value) => UpdatePagingProperties();
}
