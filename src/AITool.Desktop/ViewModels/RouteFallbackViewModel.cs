using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class RouteFallbackViewModel : ViewModelBase
{
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

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
        await _loadLock.WaitAsync();
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var page = targetPage ?? Page;
            var query = BuildQuery(page);
            var response = await _apiService.SendAsync<RouteFallbackListResponse>(
                HttpMethod.Get,
                $"/api/admin/route-fallback/list?{query}",
                null);

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
        catch (Exception exception)
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
            IsLoading = false;
            _loadLock.Release();
            NotifyStateProperties();
        }
    }

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

    private static string FormatDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }
}
