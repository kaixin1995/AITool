using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class RouteFallbackViewModel : ViewModelBase, IDisposable
{
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<RouteFallbackEvent> _items = new();
    [ObservableProperty] private RouteFallbackSummary _summary = new();
    [ObservableProperty] private string _modelKeyword = string.Empty;
    [ObservableProperty] private string _reasonKeyword = string.Empty;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _sampleLogLimit;
    [ObservableProperty] private bool _sampleTruncated;
    [ObservableProperty] private string? _sampleOldestRequestedAt;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public RouteFallbackViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !IsLoading && !HasError && !HasItems;
    public bool CanRetry => !IsLoading;
    public bool CanPrevious => Page > 1 && !IsLoading;
    public bool CanNext => Page < TotalPages && !IsLoading;
    public string PageText => TotalPages == 0 ? "第 0 / 0 页" : $"第 {Page} / {TotalPages} 页";
    public string PaginationText => TotalCount == 0
        ? "共 0 条记录"
        : $"第 {(Page - 1) * 20 + 1:N0}-{Math.Min(Page * 20, TotalCount):N0} 条，共 {TotalCount:N0} 条";
    public string SampleDescription
    {
        get
        {
            var limit = SampleLogLimit > 0 ? $"最近 {SampleLogLimit:N0} 条 UsageLogs" : "近期 UsageLogs";
            var oldest = string.IsNullOrWhiteSpace(SampleOldestRequestedAt)
                ? string.Empty
                : $"，最早采样至 {FormatDateTime(SampleOldestRequestedAt)}";
            var truncated = SampleTruncated ? "，已达到采样上限，并非完整历史统计" : string.Empty;
            return $"基于{limit}重建{oldest}{truncated}。摘要与表格均按当前筛选条件统计。";
        }
    }

    public async Task LoadAsync(int? targetPage = null)
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
            if (!IsCurrentLoad(generation, localCancellation)) return;

            IsLoading = true;
            ErrorMessage = string.Empty;
            var page = targetPage ?? Page;
            var query = BuildQuery(page);
            var response = await _apiService.SendAsync<RouteFallbackListResponse>(
                HttpMethod.Get,
                $"/api/admin/route-fallback/list?{query}",
                null,
                true,
                localCancellation.Token);

            if (!IsCurrentLoad(generation, localCancellation)) return;
            Items = new ObservableCollection<RouteFallbackEvent>(response.Items);
            Summary = response.Summary;
            Page = response.Page;
            TotalPages = response.TotalPages;
            TotalCount = response.TotalCount;
            SampleLogLimit = response.SampleLogLimit;
            SampleTruncated = response.IsTruncated;
            SampleOldestRequestedAt = response.SampleOldestRequestedAt;
            NotifyStateProperties();
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 切换筛选条件或页面销毁时取消旧请求，不显示为普通错误。
        }
        catch (Exception exception) when (IsCurrentLoad(generation, localCancellation))
        {
            ErrorMessage = exception.Message;
            Items = new ObservableCollection<RouteFallbackEvent>();
            Summary = new RouteFallbackSummary();
            TotalPages = 0;
            TotalCount = 0;
            NotifyStateProperties();
        }
        finally
        {
            if (IsCurrentLoad(generation, localCancellation))
            {
                IsLoading = false;
                Interlocked.CompareExchange(ref _loadCancellation, null, localCancellation);
                NotifyStateProperties();
            }

            if (lockAcquired) _loadLock.Release();
            localCancellation.Dispose();
        }
    }

    private bool IsCurrentLoad(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _loadGeneration)
            && ReferenceEquals(_loadCancellation, localCancellation);

    private string BuildQuery(int page)
    {
        var values = new List<string>
        {
            $"page={page}",
            "pageSize=20"
        };
        if (!string.IsNullOrWhiteSpace(ModelKeyword))
        {
            values.Add($"modelKeyword={Uri.EscapeDataString(ModelKeyword.Trim())}");
        }
        if (!string.IsNullOrWhiteSpace(ReasonKeyword))
        {
            values.Add($"reasonKeyword={Uri.EscapeDataString(ReasonKeyword.Trim())}");
        }

        return string.Join('&', values);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private Task RetryAsync() => LoadAsync(1);

    [RelayCommand]
    private Task SearchAsync() => LoadAsync(1);

    [RelayCommand]
    private Task ResetAsync()
    {
        ModelKeyword = string.Empty;
        ReasonKeyword = string.Empty;
        return LoadAsync(1);
    }

    [RelayCommand]
    private Task PreviousPageAsync() => CanPrevious ? LoadAsync(Page - 1) : Task.CompletedTask;

    [RelayCommand]
    private Task NextPageAsync() => CanNext ? LoadAsync(Page + 1) : Task.CompletedTask;

    private void NotifyStateProperties()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PaginationText));
        OnPropertyChanged(nameof(SampleDescription));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasNoItems));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoItems));
        NotifyStateProperties();
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
    }

    private static string FormatDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }
}
