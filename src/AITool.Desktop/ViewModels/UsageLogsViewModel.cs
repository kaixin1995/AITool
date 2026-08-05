using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class UsageLogsViewModel : ViewModelBase, IDisposable
{
    private readonly ApiService _apiService;
    private Timer? _refreshTimer;
    private int _refreshInFlight;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<UsageLogItem> _items = new();
    [ObservableProperty] private ObservableCollection<UsageLogFilterItem> _sites = new();
    [ObservableProperty] private ObservableCollection<UsageLogFilterItem> _accessKeys = new();
    [ObservableProperty] private UsageLogFilterItem? _selectedSite;
    [ObservableProperty] private UsageLogFilterItem? _selectedAccessKey;
    [ObservableProperty] private UsageLogOption? _selectedSource;
    [ObservableProperty] private UsageLogOption? _selectedStatus;
    [ObservableProperty] private UsageLogOption _selectedRange = new() { Id = "day", Name = "按天" };
    [ObservableProperty] private DateTimeOffset? _startTime;
    [ObservableProperty] private DateTimeOffset? _endTime;
    [ObservableProperty] private string _modelKeyword = string.Empty;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _pageJumpInput = string.Empty;
    [ObservableProperty] private UsageLogSummary _summary = new();
    [ObservableProperty] private UsageLogRequestDetail? _selectedDetail;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDetailLoading;
    [ObservableProperty] private bool _filtersExpanded;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public UsageLogsViewModel(ApiService apiService)
    {
        _apiService = apiService;
        RangeOptions = new ObservableCollection<UsageLogOption>
        {
            new() { Id = "day", Name = "按天" },
            new() { Id = "week", Name = "按周" },
            new() { Id = "month", Name = "按月" },
            new() { Id = "all", Name = "全部" },
            new() { Id = "custom", Name = "指定时间范围" }
        };
        SourceOptions = new ObservableCollection<UsageLogOption>
        {
            new() { Id = "", Name = "全部来源" },
            new() { Id = "proxy", Name = "代理" },
            new() { Id = "chat", Name = "对话测试" },
            new() { Id = "claude-code", Name = "Claude Code" },
            new() { Id = "codex", Name = "Codex" },
            new() { Id = "open-code", Name = "Open Code" },
            new() { Id = "zcode", Name = "ZCode" },
            new() { Id = "detection-manual", Name = "手动检测" },
            new() { Id = "detection-task", Name = "定时检测" }
        };
        StatusOptions = new ObservableCollection<UsageLogOption>
        {
            new() { Id = "", Name = "全部状态" },
            new() { Id = "success", Name = "成功" },
            new() { Id = "fail", Name = "失败" }
        };
        SelectedSource = SourceOptions[0];
        SelectedStatus = StatusOptions[0];
    }

    public ObservableCollection<UsageLogOption> RangeOptions { get; }
    public ObservableCollection<UsageLogOption> SourceOptions { get; }
    public ObservableCollection<UsageLogOption> StatusOptions { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !HasItems;
    public bool HasDetail => SelectedDetail is not null;
    public bool CanPrevious => Page > 1 && !IsLoading;
    public bool CanNext => Page < TotalPages && !IsLoading;
    public bool CanFirst => CanPrevious;
    public bool CanLast => CanNext;
    public bool IsCustomRange => SelectedRange?.Id == "custom";
    public string PageText => TotalPages == 0 ? "第 0 / 0 页" : $"第 {Page} / {TotalPages} 页";
    public string PaginationSummary => TotalCount == 0
        ? "共 0 条"
        : $"显示第 {(Page - 1) * 20 + 1:N0} - {Math.Min(Page * 20, TotalCount):N0} 条，共 {TotalCount:N0} 条";

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var filters = await _apiService.SendAsync<UsageLogFilters>(
                HttpMethod.Get,
                "/api/admin/usage-logs/filters",
                null);
            Sites = new ObservableCollection<UsageLogFilterItem>(filters.Sites);
            AccessKeys = new ObservableCollection<UsageLogFilterItem>(filters.AccessKeys);
            await LoadPageAsync(Page);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
            UpdatePagingProperties();
            ConfigureAutoRefresh();
        }
    }

    private async Task LoadPageAsync(int page)
    {
        var query = BuildQuery(page);
        var listTask = _apiService.SendAsync<UsageLogListResponse>(
            HttpMethod.Get,
            $"/api/admin/usage-logs/list?{query}",
            null);
        var summaryTask = _apiService.SendAsync<UsageLogSummary>(
            HttpMethod.Get,
            $"/api/admin/usage-logs/summary?{query}",
            null);
        await Task.WhenAll(listTask, summaryTask);

        var list = await listTask;
        Summary = await summaryTask;
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
        var range = SelectedRange?.Id ?? "day";
        var values = new List<string>
        {
            $"page={page}",
            "pageSize=20",
            $"rangeType={Uri.EscapeDataString(range)}"
        };
        if (SelectedSite is not null) values.Add($"siteId={Uri.EscapeDataString(SelectedSite.Id)}");
        if (SelectedAccessKey is not null) values.Add($"accessKeyId={Uri.EscapeDataString(SelectedAccessKey.Id)}");
        if (!string.IsNullOrWhiteSpace(SelectedSource?.Id)) values.Add($"source={Uri.EscapeDataString(SelectedSource.Id)}");
        if (!string.IsNullOrWhiteSpace(SelectedStatus?.Id)) values.Add($"status={Uri.EscapeDataString(SelectedStatus.Id)}");
        if (!string.IsNullOrWhiteSpace(ModelKeyword)) values.Add($"modelKeyword={Uri.EscapeDataString(ModelKeyword.Trim())}");
        if (range == "custom")
        {
            if (StartTime.HasValue) values.Add($"startTime={Uri.EscapeDataString(StartTime.Value.ToUniversalTime().ToString("O"))}");
            if (EndTime.HasValue) values.Add($"endTime={Uri.EscapeDataString(EndTime.Value.ToUniversalTime().ToString("O"))}");
        }

        return string.Join('&', values);
    }

    private async Task RefreshIncrementallyAsync()
    {
        if (_disposed || !AutoRefresh || IsLoading || Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            var query = BuildQuery(Page);
            var listTask = _apiService.SendAsync<UsageLogListResponse>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/list?{query}",
                null);
            var summaryTask = _apiService.SendAsync<UsageLogSummary>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/summary?{query}",
                null);
            await Task.WhenAll(listTask, summaryTask);

            var list = await listTask;
            var summary = await summaryTask;
            if (Page != list.Page || TotalPages != list.TotalPages || TotalCount != list.TotalCount)
            {
                Page = list.Page;
                TotalPages = list.TotalPages;
                TotalCount = list.TotalCount;
                Items = new ObservableCollection<UsageLogItem>(list.Items);
            }
            else if (Items.Count != list.Items.Count || list.Items.Select((item, index) =>
                         index >= Items.Count || JsonSerializer.Serialize(item) != JsonSerializer.Serialize(Items[index])).Any(changed => changed))
            {
                Items = new ObservableCollection<UsageLogItem>(list.Items);
            }

            if (JsonSerializer.Serialize(Summary) != JsonSerializer.Serialize(summary))
            {
                Summary = summary;
            }

            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoItems));
            UpdatePagingProperties();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private void ConfigureAutoRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        if (!_disposed && AutoRefresh)
        {
            _refreshTimer = new Timer(
                _ => Dispatcher.UIThread.Post(() => _ = RefreshIncrementallyAsync()),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            await LoadPageAsync(1);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task FirstPageAsync()
    {
        if (CanFirst) await GoToPageAsync(1);
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CanPrevious) await GoToPageAsync(Page - 1);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CanNext) await GoToPageAsync(Page + 1);
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        if (CanLast) await GoToPageAsync(TotalPages);
    }

    [RelayCommand]
    private async Task JumpPageAsync()
    {
        if (!int.TryParse(PageJumpInput, out var page)) return;
        page = Math.Clamp(page, 1, Math.Max(1, TotalPages));
        await GoToPageAsync(page);
    }

    private async Task GoToPageAsync(int page)
    {
        try
        {
            await LoadPageAsync(page);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task OpenDetailAsync(UsageLogItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.RequestId)) return;
        IsDetailLoading = true;
        try
        {
            SelectedDetail = await _apiService.SendAsync<UsageLogRequestDetail>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/request-detail/{Uri.EscapeDataString(item.RequestId)}",
                null);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    [RelayCommand]
    private void CloseDetail() => SelectedDetail = null;

    private void UpdatePagingProperties()
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PaginationSummary));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(CanFirst));
        OnPropertyChanged(nameof(CanLast));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSelectedDetailChanged(UsageLogRequestDetail? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnSelectedRangeChanged(UsageLogOption value) => OnPropertyChanged(nameof(IsCustomRange));
    partial void OnPageChanged(int value) => UpdatePagingProperties();
    partial void OnTotalPagesChanged(int value) => UpdatePagingProperties();
    partial void OnTotalCountChanged(int value) => UpdatePagingProperties();
    partial void OnIsLoadingChanged(bool value) => UpdatePagingProperties();
    partial void OnAutoRefreshChanged(bool value) => ConfigureAutoRefresh();
}
