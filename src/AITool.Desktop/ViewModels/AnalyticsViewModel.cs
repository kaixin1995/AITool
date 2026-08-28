using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class AnalyticsViewModel : ViewModelBase
{
    private const int MaxRetryAttempts = 5;
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private CancellationTokenSource? _queryCancellation;
    private bool _suppressFilterReload;
    private bool _optionsLoaded;

    [ObservableProperty] private AnalyticsDashboard? _dashboard;
    [ObservableProperty] private AnalyticsFilterOptions _filterOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _rangeOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _bucketOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _protocolOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _sourceOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _modelOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _siteOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _accessKeyOptions = new();
    [ObservableProperty] private ObservableCollection<AnalyticsSelectOption> _analysisOptions = new();
    [ObservableProperty] private AnalyticsSelectOption? _selectedRange;
    [ObservableProperty] private AnalyticsSelectOption? _selectedBucket;
    [ObservableProperty] private AnalyticsSelectOption? _selectedProtocol;
    [ObservableProperty] private AnalyticsSelectOption? _selectedModel;
    [ObservableProperty] private AnalyticsSelectOption? _selectedSource;
    [ObservableProperty] private AnalyticsSelectOption? _selectedSite;
    [ObservableProperty] private AnalyticsSelectOption? _selectedAccessKey;
    [ObservableProperty] private AnalyticsSelectOption? _selectedAnalysis;
    [ObservableProperty] private string _customStartTime = string.Empty;
    [ObservableProperty] private string _customEndTime = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isWaitingForResult;
    [ObservableProperty] private string _waitingMessage = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public AnalyticsViewModel(ApiService apiService)
    {
        _apiService = apiService;
        RangeOptions = new ObservableCollection<AnalyticsSelectOption>
        {
            new("day", "按天"),
            new("week", "按周"),
            new("month", "按月"),
            new("custom", "指定时间范围")
        };
        BucketOptions = new ObservableCollection<AnalyticsSelectOption>
        {
            new("auto", "自动"),
            new("hour", "按小时"),
            new("day", "按天"),
            new("week", "按周"),
            new("month", "按月")
        };
        ProtocolOptions = new ObservableCollection<AnalyticsSelectOption>
        {
            new("all", "全部"),
            new("OpenAI", "OpenAI"),
            new("Anthropic", "Anthropic"),
            new("Responses", "Responses")
        };
        SourceOptions = new ObservableCollection<AnalyticsSelectOption>
        {
            new(string.Empty, "全部来源"),
            new("proxy", "代理"),
            new("chat", "对话测试"),
            new("claude-code", "Claude Code"),
            new("codex", "Codex"),
            new("open-code", "Open Code"),
            new("zcode", "ZCode"),
            new("deepseek-harness", "DeepSeek Harness"),
            new("detection-manual", "手动检测"),
            new("detection-task", "定时检测")
        };
        AnalysisOptions = new ObservableCollection<AnalyticsSelectOption>
        {
            new("source", "来源"),
            new("accessKey", "Access Key"),
            new("protocol", "协议"),
            new("failureReason", "失败原因"),
            new("statusCode", "HTTP 状态码"),
            new("fallbackChain", "回退链路"),
            new("latencyPercentiles", "延迟分位数")
        };

        _suppressFilterReload = true;
        try
        {
            SelectedRange = RangeOptions[1];
            SelectedBucket = BucketOptions[0];
            SelectedProtocol = ProtocolOptions[0];
            SelectedSource = SourceOptions[0];
            SelectedAnalysis = AnalysisOptions[0];
        }
        finally
        {
            _suppressFilterReload = false;
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasDashboard => Dashboard is not null;
    public bool HasNoDashboard => Dashboard is null && !IsLoading && !HasError;
    public bool CanCancel => IsLoading;
    public bool CanRefresh => !IsLoading;
    public bool CanContinueWaiting => IsWaitingForResult && !IsLoading;
    public bool CanApplyCustomRange => IsCustomRange && !IsLoading && TryParseDate(CustomStartTime, out _) && TryParseDate(CustomEndTime, out _);
    public bool HasQueryStatus => IsLoading || IsWaitingForResult;
    public bool IsCustomRange => string.Equals(SelectedRange?.Value, "custom", StringComparison.OrdinalIgnoreCase);
    public bool HasRequestTrend => Dashboard?.RequestTrend.Count > 0;
    public bool HasResultTrend => Dashboard?.ResultTrend.Count > 0;
    public bool HasTokenTrend => Dashboard?.TokenTrend.Count > 0;
    public bool HasDurationTrend => Dashboard?.DurationTrend.Count > 0;
    public bool HasFallbackTrend => Dashboard?.FallbackTrend.Count > 0;
    public bool HasCacheRatio => Dashboard?.ModelCacheRatioDistribution.Count > 0;
    public bool HasSiteDistribution => Dashboard?.SiteDistribution.Count > 0;
    public bool HasModelDistribution => Dashboard?.ModelDistribution.Count > 0;
    public bool HasAnalysisBreakdown => AnalysisBreakdown.Any();
    public bool HasNoAnalysisBreakdown => !HasAnalysisBreakdown;
    public bool HasFallbackChains => Dashboard?.FallbackChainDistribution.Count > 0;
    public bool HasNoFallbackChains => !HasFallbackChains;
    public bool HasLatencyPercentiles => Dashboard?.LatencyPercentiles is not null;
    public bool IsBreakdownAnalysis => SelectedAnalysis?.Value is not "fallbackChain" and not "latencyPercentiles";
    public bool IsFallbackChainAnalysis => string.Equals(SelectedAnalysis?.Value, "fallbackChain", StringComparison.OrdinalIgnoreCase);
    public bool IsLatencyAnalysis => string.Equals(SelectedAnalysis?.Value, "latencyPercentiles", StringComparison.OrdinalIgnoreCase);

    public string FilterSummary
    {
        get
        {
            var values = new List<string>();
            if (SelectedRange is not null) values.Add(SelectedRange.Label);
            if (SelectedBucket is not null) values.Add(SelectedBucket.Label);
            AddSelected(values, SelectedProtocol, "all");
            AddSelected(values, SelectedModel, "all");
            AddSelected(values, SelectedSource, string.Empty);
            AddSelected(values, SelectedSite, string.Empty);
            AddSelected(values, SelectedAccessKey, string.Empty);
            return string.Join(" · ", values);
        }
    }

    public IEnumerable<AnalyticsBreakdownPoint> AnalysisBreakdown
    {
        get
        {
            if (Dashboard is null) return Array.Empty<AnalyticsBreakdownPoint>();
            return SelectedAnalysis?.Value switch
            {
                "accessKey" => Dashboard.AccessKeyBreakdown,
                "protocol" => Dashboard.ProtocolBreakdown,
                "failureReason" => Dashboard.FailureReasonBreakdown,
                "statusCode" => Dashboard.StatusCodeBreakdown,
                _ => Dashboard.SourceBreakdown
            };
        }
    }

    public IEnumerable<AnalyticsFallbackChainPoint> AnalysisFallbackChains =>
        Dashboard?.FallbackChainDistribution ?? Enumerable.Empty<AnalyticsFallbackChainPoint>();

    public AnalyticsLatencyPercentiles? AnalysisLatencyPercentiles => Dashboard?.LatencyPercentiles;

    public async Task LoadAsync()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _queryCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        await _loadLock.WaitAsync();
        try
        {
            IsLoading = true;
            IsWaitingForResult = false;
            WaitingMessage = string.Empty;
            ErrorMessage = string.Empty;

            if (!_optionsLoaded)
            {
                FilterOptions = await _apiService.SendAsync<AnalyticsFilterOptions>(
                    HttpMethod.Get,
                    "/api/admin/analytics/options",
                    null,
                    cancellationToken: cancellation.Token);
                ApplyFilterOptions();
                _optionsLoaded = true;
            }

            await LoadDashboardAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 用户切换筛选条件或点击取消时，忽略被主动取消的请求。
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_queryCancellation, cancellation))
            {
                _queryCancellation = null;
                IsLoading = false;
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanContinueWaiting));
            }

            cancellation.Dispose();
            _loadLock.Release();
            NotifyDashboardProperties();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void CancelQuery()
    {
        if (!IsLoading) return;
        _queryCancellation?.Cancel();
        IsWaitingForResult = false;
        WaitingMessage = "统计查询已取消";
    }

    [RelayCommand]
    private Task ContinueWaitingAsync() => LoadAsync();

    [RelayCommand]
    private async Task ApplyCustomRangeAsync()
    {
        if (!IsCustomRange) return;
        if (!TryParseDate(CustomStartTime, out _) || !TryParseDate(CustomEndTime, out _))
        {
            ErrorMessage = "请输入有效的开始时间和结束时间，例如 2026-08-01 00:00。";
            return;
        }

        await LoadAsync();
    }

    private async Task LoadDashboardAsync(CancellationToken cancellationToken)
    {
        var attempts = 0;
        var query = BuildQuery();

        while (true)
        {
            AnalyticsDashboardResponse response;
            try
            {
                response = await _apiService.SendAsync<AnalyticsDashboardResponse>(
                    HttpMethod.Get,
                    $"/api/admin/analytics/dashboard?{query}",
                    null,
                    cancellationToken: cancellationToken);
            }
            catch (ApiException exception) when (exception.StatusCode == (int)HttpStatusCode.TooManyRequests)
            {
                attempts++;
                if (attempts >= MaxRetryAttempts)
                {
                    SetWaitingState(exception.Message);
                    return;
                }

                await WaitForRetryAsync(exception.RetryAfterMs ?? 2000, cancellationToken);
                continue;
            }

            if (response.IsReady)
            {
                var dashboard = response.ToDashboard();
                dashboard.PrepareDisplayValues();
                Dashboard = dashboard;
                return;
            }

            attempts++;
            var retryAfterMs = response.RetryAfterMs ?? (response.IsBusy ? 2000 : 1200);
            if (attempts >= MaxRetryAttempts)
            {
                SetWaitingState(response.Message ?? "统计任务仍在后台处理中");
                return;
            }

            await WaitForRetryAsync(retryAfterMs, cancellationToken);
        }
    }

    private async Task WaitForRetryAsync(int retryAfterMs, CancellationToken cancellationToken)
    {
        WaitingMessage = "统计数据正在后台计算，稍后自动重试...";
        OnPropertyChanged(nameof(CanCancel));
        await Task.Delay(Math.Clamp(retryAfterMs, 250, 10_000), cancellationToken);
    }

    private void SetWaitingState(string message)
    {
        IsWaitingForResult = true;
        WaitingMessage = message;
    }

    private string BuildQuery()
    {
        var values = new List<string>
        {
            QueryValue("rangeType", SelectedRange?.Value ?? "week"),
            QueryValue("bucketType", SelectedBucket?.Value ?? "auto"),
            QueryValue("protocolType", SelectedProtocol?.Value ?? "all"),
            QueryValue("modelName", SelectedModel?.Value ?? "all")
        };

        if (!string.IsNullOrWhiteSpace(SelectedSource?.Value)) values.Add(QueryValue("source", SelectedSource.Value));
        if (!string.IsNullOrWhiteSpace(SelectedSite?.Value)) values.Add(QueryValue("siteId", SelectedSite.Value));
        if (!string.IsNullOrWhiteSpace(SelectedAccessKey?.Value)) values.Add(QueryValue("accessKeyId", SelectedAccessKey.Value));

        if (IsCustomRange)
        {
            if (TryParseDate(CustomStartTime, out var start)) values.Add(QueryValue("startTime", start.ToString("O", CultureInfo.InvariantCulture)));
            if (TryParseDate(CustomEndTime, out var end)) values.Add(QueryValue("endTime", end.ToString("O", CultureInfo.InvariantCulture)));
        }

        return string.Join('&', values);
    }

    private static string QueryValue(string key, string value)
        => $"{key}={Uri.EscapeDataString(value)}";

    private static bool TryParseDate(string value, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.CurrentCulture,
            DateTimeStyles.AssumeLocal,
            out date);
    }

    private void ApplyFilterOptions()
    {
        _suppressFilterReload = true;
        try
        {
            ModelOptions = new ObservableCollection<AnalyticsSelectOption>(
                new[] { new AnalyticsSelectOption("all", "全部模型") }
                    .Concat(FilterOptions.Models.Select(x => new AnalyticsSelectOption(x.ModelName, x.ModelName))));
            SiteOptions = new ObservableCollection<AnalyticsSelectOption>(
                new[] { new AnalyticsSelectOption(string.Empty, "全部站点") }
                    .Concat(FilterOptions.Sites.Select(x => new AnalyticsSelectOption(x.SiteId, x.SiteName))));
            AccessKeyOptions = new ObservableCollection<AnalyticsSelectOption>(
                new[] { new AnalyticsSelectOption(string.Empty, "全部 Access Key") }
                    .Concat(FilterOptions.AccessKeys.Select(x => new AnalyticsSelectOption(x.AccessKeyId, x.AccessKeyLabel))));

            SelectedModel = ModelOptions[0];
            SelectedSite = SiteOptions[0];
            SelectedAccessKey = AccessKeyOptions[0];
        }
        finally
        {
            _suppressFilterReload = false;
        }
    }

    private static void AddSelected(List<string> values, AnalyticsSelectOption? option, string ignoredValue)
    {
        if (option is not null && !string.Equals(option.Value, ignoredValue, StringComparison.OrdinalIgnoreCase))
        {
            values.Add(option.Label);
        }
    }

    private void NotifyDashboardProperties()
    {
        OnPropertyChanged(nameof(HasDashboard));
        OnPropertyChanged(nameof(HasNoDashboard));
        OnPropertyChanged(nameof(HasRequestTrend));
        OnPropertyChanged(nameof(HasResultTrend));
        OnPropertyChanged(nameof(HasTokenTrend));
        OnPropertyChanged(nameof(HasDurationTrend));
        OnPropertyChanged(nameof(HasFallbackTrend));
        OnPropertyChanged(nameof(HasCacheRatio));
        OnPropertyChanged(nameof(HasSiteDistribution));
        OnPropertyChanged(nameof(HasModelDistribution));
        OnPropertyChanged(nameof(HasAnalysisBreakdown));
        OnPropertyChanged(nameof(HasNoAnalysisBreakdown));
        OnPropertyChanged(nameof(HasFallbackChains));
        OnPropertyChanged(nameof(HasNoFallbackChains));
        OnPropertyChanged(nameof(HasLatencyPercentiles));
        OnPropertyChanged(nameof(IsBreakdownAnalysis));
        OnPropertyChanged(nameof(IsFallbackChainAnalysis));
        OnPropertyChanged(nameof(IsLatencyAnalysis));
        OnPropertyChanged(nameof(AnalysisBreakdown));
        OnPropertyChanged(nameof(AnalysisFallbackChains));
        OnPropertyChanged(nameof(AnalysisLatencyPercentiles));
        OnPropertyChanged(nameof(FilterSummary));
    }

    private void NotifyFilterProperties()
    {
        OnPropertyChanged(nameof(IsCustomRange));
        OnPropertyChanged(nameof(FilterSummary));
        if (Dashboard is not null)
        {
            OnPropertyChanged(nameof(AnalysisBreakdown));
            OnPropertyChanged(nameof(AnalysisFallbackChains));
            OnPropertyChanged(nameof(AnalysisLatencyPercentiles));
        }
    }

    partial void OnDashboardChanged(AnalyticsDashboard? value) => NotifyDashboardProperties();
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanContinueWaiting));
        OnPropertyChanged(nameof(CanApplyCustomRange));
        OnPropertyChanged(nameof(HasQueryStatus));
        OnPropertyChanged(nameof(HasNoDashboard));
    }
    partial void OnIsWaitingForResultChanged(bool value)
    {
        OnPropertyChanged(nameof(CanContinueWaiting));
        OnPropertyChanged(nameof(HasQueryStatus));
    }
    partial void OnCustomStartTimeChanged(string value) => OnPropertyChanged(nameof(CanApplyCustomRange));
    partial void OnCustomEndTimeChanged(string value) => OnPropertyChanged(nameof(CanApplyCustomRange));

    partial void OnSelectedAnalysisChanged(AnalyticsSelectOption? value)
    {
        OnPropertyChanged(nameof(AnalysisBreakdown));
        OnPropertyChanged(nameof(HasAnalysisBreakdown));
        OnPropertyChanged(nameof(HasNoAnalysisBreakdown));
        OnPropertyChanged(nameof(HasNoFallbackChains));
        OnPropertyChanged(nameof(IsBreakdownAnalysis));
        OnPropertyChanged(nameof(IsFallbackChainAnalysis));
        OnPropertyChanged(nameof(IsLatencyAnalysis));
    }
    partial void OnSelectedRangeChanged(AnalyticsSelectOption? value)
    {
        NotifyFilterProperties();
        if (!_suppressFilterReload && value is not null) _ = LoadAsync();
    }
    partial void OnSelectedBucketChanged(AnalyticsSelectOption? value)
    {
        NotifyFilterProperties();
        if (!_suppressFilterReload && value is not null) _ = LoadAsync();
    }
    partial void OnSelectedProtocolChanged(AnalyticsSelectOption? value)
    {
        NotifyFilterProperties();
        if (!_suppressFilterReload && value is not null) _ = LoadAsync();
    }
    partial void OnSelectedModelChanged(AnalyticsSelectOption? value)
    {
        NotifyFilterProperties();
        if (!_suppressFilterReload && value is not null && _optionsLoaded) _ = LoadAsync();
    }
    partial void OnSelectedSourceChanged(AnalyticsSelectOption? value)
    {
        NotifyFilterProperties();
        if (!_suppressFilterReload && value is not null && _optionsLoaded) _ = LoadAsync();
    }
    partial void OnSelectedSiteChanged(AnalyticsSelectOption? value)
    {
        NotifyFilterProperties();
        if (!_suppressFilterReload && value is not null && _optionsLoaded) _ = LoadAsync();
    }
    partial void OnSelectedAccessKeyChanged(AnalyticsSelectOption? value)
    {
        NotifyFilterProperties();
        if (!_suppressFilterReload && value is not null && _optionsLoaded) _ = LoadAsync();
    }
}
