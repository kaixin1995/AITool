using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class UsageLogsViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 20;
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _pageLoadLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Timer? _refreshTimer;
    private Timer? _filterDebounceTimer;
    private CancellationTokenSource? _listCancellation;
    private CancellationTokenSource? _detailCancellation;
    private int _listGeneration;
    private int _detailGeneration;
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
    [ObservableProperty] private string _startTime = string.Empty;
    [ObservableProperty] private string _endTime = string.Empty;
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
            new() { Id = "deepseek-harness", Name = "DeepSeek Harness" },
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
    public bool HasNoItems => !IsLoading && !HasError && !HasItems;
    public bool HasDetail => SelectedDetail is not null;
    public bool CanRetry => !IsLoading;
    public bool CanPrevious => Page > 1 && !IsLoading;
    public bool CanNext => Page < TotalPages && !IsLoading;
    public bool CanFirst => CanPrevious;
    public bool CanLast => CanNext;
    public bool IsCustomRange => SelectedRange?.Id == "custom";
    public string PageText => TotalPages == 0 ? "第 0 / 0 页" : $"第 {Page} / {TotalPages} 页";
    public string PaginationSummary => TotalCount == 0
        ? "共 0 条"
        : $"显示第 {(Page - 1) * PageSize + 1:N0} - {Math.Min(Page * PageSize, TotalCount):N0} 条，共 {TotalCount:N0} 条";

    public async Task LoadAsync()
    {
        CancelCurrentListRequest();
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            if (IsCustomRange && (!TryParseCustomTime(StartTime, out _) || !TryParseCustomTime(EndTime, out _)))
            {
                ErrorMessage = "请输入有效的开始时间和结束时间，例如 2026-08-06 00:00:00。";
                return;
            }

            var filters = await _apiService.SendAsync<UsageLogFilters>(
                HttpMethod.Get,
                "/api/admin/usage-logs/filters",
                null,
                true,
                _lifetimeCancellation.Token);
            Sites = new ObservableCollection<UsageLogFilterItem>(filters.Sites);
            AccessKeys = new ObservableCollection<UsageLogFilterItem>(filters.AccessKeys);
            await LoadPageAsync(Page, showLoading: false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 页面销毁时取消请求，不向用户显示普通错误。
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

    private async Task LoadPageAsync(int page, bool showLoading = true)
    {
        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _listCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _listGeneration);
        var lockAcquired = false;

        try
        {
            await _pageLoadLock.WaitAsync(localCancellation.Token);
            lockAcquired = true;
            if (showLoading && IsCurrentListRequest(generation, localCancellation))
            {
                IsLoading = true;
                ErrorMessage = string.Empty;
            }

            var query = BuildQuery(page);
            var listTask = _apiService.SendAsync<UsageLogListResponse>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/list?{query}",
                null,
                true,
                localCancellation.Token);
            var summaryTask = _apiService.SendAsync<UsageLogSummary>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/summary?{query}",
                null,
                true,
                localCancellation.Token);
            await Task.WhenAll(listTask, summaryTask);

            if (!IsCurrentListRequest(generation, localCancellation)) return;
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
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 新查询或页面销毁会取消旧请求，取消不应显示为失败。
        }
        catch (Exception exception) when (IsCurrentListRequest(generation, localCancellation))
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            if (IsCurrentListRequest(generation, localCancellation))
            {
                if (showLoading) IsLoading = false;
                Interlocked.CompareExchange(ref _listCancellation, null, localCancellation);
                UpdatePagingProperties();
            }

            if (lockAcquired) _pageLoadLock.Release();
            localCancellation.Dispose();
        }
    }

    private bool IsCurrentListRequest(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _listGeneration)
            && ReferenceEquals(_listCancellation, localCancellation);

    private string BuildQuery(int page)
    {
        var range = SelectedRange?.Id ?? "day";
        var values = new List<string>
        {
            $"page={page}",
            $"pageSize={PageSize}",
            $"rangeType={Uri.EscapeDataString(range)}"
        };
        if (SelectedSite is not null) values.Add($"siteId={Uri.EscapeDataString(SelectedSite.Id)}");
        if (SelectedAccessKey is not null) values.Add($"accessKeyId={Uri.EscapeDataString(SelectedAccessKey.Id)}");
        if (!string.IsNullOrWhiteSpace(SelectedSource?.Id)) values.Add($"source={Uri.EscapeDataString(SelectedSource.Id)}");
        if (!string.IsNullOrWhiteSpace(SelectedStatus?.Id)) values.Add($"status={Uri.EscapeDataString(SelectedStatus.Id)}");
        if (!string.IsNullOrWhiteSpace(ModelKeyword)) values.Add($"modelKeyword={Uri.EscapeDataString(ModelKeyword.Trim())}");
        if (range == "custom")
        {
            if (TryParseCustomTime(StartTime, out var startTime))
            {
                values.Add($"startTime={Uri.EscapeDataString(startTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}");
            }

            if (TryParseCustomTime(EndTime, out var endTime))
            {
                values.Add($"endTime={Uri.EscapeDataString(endTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}");
            }
        }

        return string.Join('&', values);
    }

    // 支持日期、时分秒和带小数秒输入，并统一转换为后端原有的 ISO 参数格式。
    private static bool TryParseCustomTime(string value, out DateTimeOffset result)
    {
        return DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out result)
            || DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out result);
    }

    // 用 RequestId（唯一标识）+ RequestedAt（时间戳）轻量比对，避免逐行 JSON 序列化。
    private bool ItemsChanged(IList<UsageLogItem> next)
    {
        if (next.Count != Items.Count) return true;
        for (var i = 0; i < Items.Count; i++)
        {
            var current = Items[i];
            var incoming = next[i];
            if (!string.Equals(current.RequestId, incoming.RequestId, StringComparison.Ordinal)
                || !string.Equals(current.RequestedAt, incoming.RequestedAt, StringComparison.Ordinal)
                || !string.Equals(current.Status, incoming.Status, StringComparison.Ordinal)
                || !string.Equals(current.ErrorMessage, incoming.ErrorMessage, StringComparison.Ordinal)
                || current.TotalDurationMs != incoming.TotalDurationMs
                || current.FirstTokenLatencyMs != incoming.FirstTokenLatencyMs
                || current.InputTokens != incoming.InputTokens
                || current.CachedTokens != incoming.CachedTokens
                || current.OutputTokens != incoming.OutputTokens
                || current.TotalTokens != incoming.TotalTokens
                || current.FallbackTriggered != incoming.FallbackTriggered
                || current.IsStreamInterrupted != incoming.IsStreamInterrupted)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RefreshIncrementallyAsync()
    {
        if (_disposed || !AutoRefresh || IsLoading || Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        if (IsCustomRange && (!TryParseCustomTime(StartTime, out _) || !TryParseCustomTime(EndTime, out _)))
        {
            ErrorMessage = "请输入有效的开始时间和结束时间，例如 2026-08-06 00:00:00。";
            Interlocked.Exchange(ref _refreshInFlight, 0);
            return;
        }

        if (!await _pageLoadLock.WaitAsync(0))
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
            return;
        }

        var generation = Volatile.Read(ref _listGeneration);
        try
        {
            var query = BuildQuery(Page);
            var listTask = _apiService.SendAsync<UsageLogListResponse>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/list?{query}",
                null,
                true,
                _lifetimeCancellation.Token);
            var summaryTask = _apiService.SendAsync<UsageLogSummary>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/summary?{query}",
                null,
                true,
                _lifetimeCancellation.Token);
            await Task.WhenAll(listTask, summaryTask);

            if (_disposed || generation != Volatile.Read(ref _listGeneration)) return;
            var list = await listTask;
            var summary = await summaryTask;
            if (Page != list.Page || TotalPages != list.TotalPages || TotalCount != list.TotalCount)
            {
                Page = list.Page;
                TotalPages = list.TotalPages;
                TotalCount = list.TotalCount;
                Items = new ObservableCollection<UsageLogItem>(list.Items);
            }
            else if (Items.Count != list.Items.Count || ItemsChanged(list.Items))
            {
                Items = new ObservableCollection<UsageLogItem>(list.Items);
            }

            if (Summary.TotalRequests != summary.TotalRequests
                || Summary.FailedRequests != summary.FailedRequests
                || Summary.SuccessRate != summary.SuccessRate
                || Summary.TotalTokens != summary.TotalTokens
                || Summary.MaxDurationMs != summary.MaxDurationMs)
            {
                Summary = summary;
            }

            ErrorMessage = string.Empty;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoItems));
            UpdatePagingProperties();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 页面销毁时取消自动刷新请求，不显示为普通错误。
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            _pageLoadLock.Release();
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private void CancelCurrentListRequest()
    {
        Interlocked.Increment(ref _listGeneration);
        var cancellation = Interlocked.Exchange(ref _listCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void ScheduleFilterSearch()
    {
        if (_disposed) return;

        _filterDebounceTimer?.Dispose();
        _filterDebounceTimer = new Timer(
            _ => Dispatcher.UIThread.Post(() => _ = SearchAsync()),
            null,
            TimeSpan.FromMilliseconds(300),
            Timeout.InfiniteTimeSpan);
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

    // 使用普通按钮切换筛选区，避免 ToggleButton 的 checked 模板覆盖页面样式。
    [RelayCommand]
    private void ToggleFilters()
    {
        FiltersExpanded = !FiltersExpanded;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsCustomRange && (!TryParseCustomTime(StartTime, out _) || !TryParseCustomTime(EndTime, out _)))
        {
            ErrorMessage = "请输入有效的开始时间和结束时间，例如 2026-08-06 00:00:00。";
            return;
        }

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
    private Task RetryAsync() => LoadAsync();

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
        if (item is null || string.IsNullOrWhiteSpace(item.RequestId) || _disposed) return;

        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _detailCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _detailGeneration);
        IsDetailLoading = true;
        try
        {
            var detail = await _apiService.SendAsync<UsageLogRequestDetail>(
                HttpMethod.Get,
                $"/api/admin/usage-logs/request-detail/{Uri.EscapeDataString(item.RequestId)}",
                null,
                true,
                localCancellation.Token);

            if (!IsCurrentDetailRequest(generation, localCancellation)) return;

            // 明细 DTO 的尝试项不重复携带请求模型，补入请求级模型以支持展示回退。
            foreach (var attempt in detail.Attempts)
            {
                attempt.RequestModel = detail.RequestModel;
            }

            SelectedDetail = detail;
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 用户切换到另一条日志或页面销毁时取消旧详情请求。
        }
        catch (Exception exception) when (IsCurrentDetailRequest(generation, localCancellation))
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            if (IsCurrentDetailRequest(generation, localCancellation))
            {
                IsDetailLoading = false;
                Interlocked.CompareExchange(ref _detailCancellation, null, localCancellation);
            }

            localCancellation.Dispose();
        }
    }

    private bool IsCurrentDetailRequest(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _detailGeneration)
            && ReferenceEquals(_detailCancellation, localCancellation);

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
        _filterDebounceTimer?.Dispose();
        _filterDebounceTimer = null;
        CancelCurrentListRequest();
        var detailCancellation = Interlocked.Exchange(ref _detailCancellation, null);
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _pageLoadLock.Dispose();
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasNoItems));
    }
    partial void OnSelectedDetailChanged(UsageLogRequestDetail? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnSelectedRangeChanged(UsageLogOption value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
        ScheduleFilterSearch();
    }
    partial void OnSelectedSiteChanged(UsageLogFilterItem? value) => ScheduleFilterSearch();
    partial void OnSelectedAccessKeyChanged(UsageLogFilterItem? value) => ScheduleFilterSearch();
    partial void OnSelectedSourceChanged(UsageLogOption? value) => ScheduleFilterSearch();
    partial void OnSelectedStatusChanged(UsageLogOption? value) => ScheduleFilterSearch();
    partial void OnModelKeywordChanged(string value) => ScheduleFilterSearch();
    partial void OnStartTimeChanged(string value) => ScheduleFilterSearch();
    partial void OnEndTimeChanged(string value) => ScheduleFilterSearch();
    partial void OnPageChanged(int value) => UpdatePagingProperties();
    partial void OnTotalPagesChanged(int value) => UpdatePagingProperties();
    partial void OnTotalCountChanged(int value) => UpdatePagingProperties();
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(CanRetry));
        UpdatePagingProperties();
    }
    partial void OnAutoRefreshChanged(bool value) => ConfigureAutoRefresh();
}
