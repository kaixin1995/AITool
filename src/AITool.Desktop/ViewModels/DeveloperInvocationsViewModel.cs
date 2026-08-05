using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class DeveloperInvocationsViewModel : ViewModelBase, IDisposable
{
    private readonly ApiService _apiService;
    private readonly SemaphoreSlim _listLoadLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _simulatorCancellationLock = new();
    private readonly Dictionary<DeveloperSimulatorTab, CancellationTokenSource> _simulatorCancellations = new();
    private Timer? _refreshTimer;
    private CancellationTokenSource? _initializationCancellation;
    private CancellationTokenSource? _listCancellation;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _concurrencyCancellation;
    private CancellationTokenSource? _circuitCancellation;
    private int _initializationGeneration;
    private int _listGeneration;
    private int _detailGeneration;
    private int _concurrencyGeneration;
    private int _circuitGeneration;
    private int _refreshInFlight;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<DeveloperInvocationSummary> _items = new();
    [ObservableProperty] private DeveloperInvocationDetail? _selectedDetail;
    [ObservableProperty] private ObservableCollection<DeveloperConcurrencyItem> _concurrency = new();
    [ObservableProperty] private ObservableCollection<CircuitBreakerRoute> _circuitRoutes = new();
    [ObservableProperty] private ObservableCollection<DeveloperSimulatorModel> _models = new();
    [ObservableProperty] private ObservableCollection<string> _modelNames = new();
    [ObservableProperty] private ObservableCollection<DeveloperSimulatorTab> _simulatorTabs = new();
    [ObservableProperty] private DeveloperSimulatorTab? _selectedSimulatorTab;
    [ObservableProperty] private string _baseUrl = string.Empty;
    [ObservableProperty] private string _accessKey = string.Empty;
    [ObservableProperty] private string _selectedModel = string.Empty;
    [ObservableProperty] private string _inputText = "你好，请简单介绍一下你自己。";
    [ObservableProperty] private string _supportHint = "请选择支持当前协议的模型。";
    [ObservableProperty] private string _concurrencyError = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isConcurrencyLoading;
    [ObservableProperty] private bool _isCircuitLoading;
    [ObservableProperty] private bool _isDetailLoading;
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private bool _summarizeDetail = true;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _pendingCount;

    public DeveloperInvocationsViewModel(ApiService apiService)
    {
        _apiService = apiService;
        SimulatorTabs = new ObservableCollection<DeveloperSimulatorTab>
        {
            new("models", "模型列表", "/v1/models", "GET", false),
            new("openai", "OpenAI 聊天", "/v1/chat/completions", "POST", true),
            new("anthropic", "Anthropic 聊天", "/v1/messages", "POST", true),
            new("responses", "Responses", "/v1/responses", "POST", true),
            new("completions", "Completions", "/v1/completions", "POST", true),
            new("embeddings", "Embeddings", "/v1/embeddings", "POST", false),
            new("countTokens", "Count Tokens", "/v1/messages/count_tokens", "POST", false),
            new("responsesCompact", "Responses Compact", "/v1/responses/compact", "POST", false)
        };
        SimulatorTabs[0].IsSelected = true;
        SelectedSimulatorTab = SimulatorTabs[0];
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasConcurrencyError => !string.IsNullOrWhiteSpace(ConcurrencyError);
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !HasItems;
    public bool HasDetail => SelectedDetail is not null;
    public bool HasConcurrency => Concurrency.Count > 0;
    public bool HasNoConcurrency => !HasConcurrency;
    public bool HasCircuitRoutes => CircuitRoutes.Count > 0;
    public bool HasNoCircuitRoutes => !HasCircuitRoutes;
    public bool HasBlockedCircuits => CircuitRoutes.Any(x => x.IsBlocked);
    public bool CanPrevious => Page > 1 && !IsLoading;
    public bool CanNext => Page < TotalPages && !IsLoading;
    public string PageText => TotalPages == 0 ? "第 0 / 0 页" : $"第 {Page} / {TotalPages} 页";
    public string PaginationSummary => TotalCount == 0
        ? "共 0 条记录"
        : $"显示第 {(Page - 1) * 40 + 1:N0} - {Math.Min(Page * 40, TotalCount):N0} 条，共 {TotalCount:N0} 条";
    public string SelectedTabTitle => SelectedTabIndex switch
    {
        1 => "客户端模拟",
        2 => "当前模型并发数检测",
        3 => "熔断监控",
        _ => "调用调试"
    };

    public async Task LoadAsync()
    {
        if (_disposed) return;

        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _initializationCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _initializationGeneration);
        CancelCurrentListRequest();
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var init = await _apiService.SendAsync<DeveloperInitResponse>(
                HttpMethod.Get,
                "/api/admin/developer/invocations/init",
                null,
                true,
                localCancellation.Token);
            if (!IsCurrentInitialization(generation, localCancellation)) return;

            BaseUrl = init.DefaultBaseUrl;
            AccessKey = init.DefaultAccessKey;
            Models = new ObservableCollection<DeveloperSimulatorModel>(init.Models);
            ModelNames = new ObservableCollection<string>(Models.Select(model => model.ModelName));
            SelectedModel = init.DefaultOpenAiModel;
            if (string.IsNullOrWhiteSpace(SelectedModel))
            {
                SelectedModel = Models.FirstOrDefault()?.ModelName ?? string.Empty;
            }

            UpdateSimulatorExamples();
            UpdateSupportHint();
            await LoadInvocationsAsync(null, localCancellation.Token);
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 页面销毁或新一轮加载会取消旧请求，不显示为普通错误。
        }
        catch (Exception exception)
        {
            if (IsCurrentInitialization(generation, localCancellation))
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (IsCurrentInitialization(generation, localCancellation))
            {
                if (_listCancellation is null) IsLoading = false;
                Interlocked.CompareExchange(ref _initializationCancellation, null, localCancellation);
                UpdatePaging();
                ConfigureAutoRefresh();
            }

            localCancellation.Dispose();
        }
    }

    private async Task LoadInvocationsAsync(int? requestedPage = null, CancellationToken parentCancellation = default)
    {
        if (_disposed) return;

        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            parentCancellation);
        var previousCancellation = Interlocked.Exchange(ref _listCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _listGeneration);
        var lockAcquired = false;
        IsLoading = true;
        ErrorMessage = string.Empty;
        UpdatePaging();

        try
        {
            await _listLoadLock.WaitAsync(localCancellation.Token);
            lockAcquired = true;

            var response = await _apiService.SendAsync<DeveloperListResponse>(
                HttpMethod.Get,
                $"/api/admin/developer/invocations/list?page={requestedPage ?? Page}&pageSize=40",
                null,
                true,
                localCancellation.Token);
            if (!IsCurrentListRequest(generation, localCancellation)) return;

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
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 页面销毁或新一轮列表请求会取消旧请求。
        }
        catch (Exception exception)
        {
            if (IsCurrentListRequest(generation, localCancellation))
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (IsCurrentListRequest(generation, localCancellation))
            {
                IsLoading = false;
                Interlocked.CompareExchange(ref _listCancellation, null, localCancellation);
                UpdatePaging();
            }

            if (lockAcquired) _listLoadLock.Release();
            localCancellation.Dispose();
        }
    }

    private bool IsCurrentInitialization(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _initializationGeneration)
            && ReferenceEquals(_initializationCancellation, localCancellation);

    private bool IsCurrentListRequest(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _listGeneration)
            && ReferenceEquals(_listCancellation, localCancellation);

    private void CancelCurrentListRequest()
    {
        Interlocked.Increment(ref _listGeneration);
        var cancellation = Interlocked.Exchange(ref _listCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task LoadConcurrencyAsync()
    {
        if (_disposed) return;

        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _concurrencyCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _concurrencyGeneration);
        IsConcurrencyLoading = true;
        ConcurrencyError = string.Empty;

        try
        {
            var response = await _apiService.SendAsync<DeveloperConcurrencyResponse>(
                HttpMethod.Get,
                "/api/admin/developer/invocations/concurrency",
                null,
                true,
                localCancellation.Token);
            if (!IsCurrentConcurrencyRequest(generation, localCancellation)) return;

            Concurrency = new ObservableCollection<DeveloperConcurrencyItem>(response.Items);
            NotifyConcurrencyProperties();
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 页面销毁或新一轮并发请求会取消旧请求。
        }
        catch (ApiException exception) when (exception.StatusCode == 404 && IsCurrentConcurrencyRequest(generation, localCancellation))
        {
            Concurrency = new ObservableCollection<DeveloperConcurrencyItem>();
            ConcurrencyError = string.Empty;
            NotifyConcurrencyProperties();
        }
        catch (Exception exception)
        {
            if (IsCurrentConcurrencyRequest(generation, localCancellation))
            {
                ConcurrencyError = exception.Message;
            }
        }
        finally
        {
            if (IsCurrentConcurrencyRequest(generation, localCancellation))
            {
                IsConcurrencyLoading = false;
                Interlocked.CompareExchange(ref _concurrencyCancellation, null, localCancellation);
            }

            localCancellation.Dispose();
        }
    }

    private async Task LoadCircuitAsync()
    {
        if (_disposed) return;

        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _circuitCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _circuitGeneration);
        IsCircuitLoading = true;

        try
        {
            var response = await _apiService.SendAsync<CircuitBreakerResponse>(
                HttpMethod.Get,
                "/api/admin/developer/invocations/circuit-breaker",
                null,
                true,
                localCancellation.Token);
            if (!IsCurrentCircuitRequest(generation, localCancellation)) return;

            CircuitRoutes = new ObservableCollection<CircuitBreakerRoute>(response.Routes);
            NotifyCircuitProperties();
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 页面销毁或新一轮熔断请求会取消旧请求。
        }
        catch (ApiException exception) when (exception.StatusCode == 404 && IsCurrentCircuitRequest(generation, localCancellation))
        {
            CircuitRoutes = new ObservableCollection<CircuitBreakerRoute>();
            ErrorMessage = string.Empty;
            NotifyCircuitProperties();
        }
        catch (Exception exception)
        {
            if (IsCurrentCircuitRequest(generation, localCancellation))
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (IsCurrentCircuitRequest(generation, localCancellation))
            {
                IsCircuitLoading = false;
                Interlocked.CompareExchange(ref _circuitCancellation, null, localCancellation);
            }

            localCancellation.Dispose();
        }
    }

    private bool IsCurrentConcurrencyRequest(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _concurrencyGeneration)
            && ReferenceEquals(_concurrencyCancellation, localCancellation);

    private bool IsCurrentCircuitRequest(int generation, CancellationTokenSource localCancellation)
        => !_disposed
            && generation == Volatile.Read(ref _circuitGeneration)
            && ReferenceEquals(_circuitCancellation, localCancellation);

    private void NotifyConcurrencyProperties()
    {
        OnPropertyChanged(nameof(HasConcurrency));
        OnPropertyChanged(nameof(HasNoConcurrency));
    }

    private void NotifyCircuitProperties()
    {
        OnPropertyChanged(nameof(HasCircuitRoutes));
        OnPropertyChanged(nameof(HasNoCircuitRoutes));
        OnPropertyChanged(nameof(HasBlockedCircuits));
    }

    private async Task LoadActiveTabAsync()
    {
        if (_disposed) return;

        if (SelectedTabIndex == 0)
        {
            try { await LoadInvocationsAsync(); }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                // 页面销毁时取消列表请求，不显示为普通错误。
            }
            catch (Exception exception) when (!_disposed)
            {
                ErrorMessage = exception.Message;
            }
        }
        else if (SelectedTabIndex == 2)
        {
            await LoadConcurrencyAsync();
        }
        else if (SelectedTabIndex == 3)
        {
            await LoadCircuitAsync();
        }

        ConfigureAutoRefresh();
    }

    private void ConfigureAutoRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        if (_disposed || (!AutoRefresh && SelectedTabIndex != 2 && SelectedTabIndex != 3)) return;

        _refreshTimer = new Timer(
            _ => Dispatcher.UIThread.Post(() => _ = RefreshActiveTabAsync()),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
    }

    private async Task RefreshActiveTabAsync()
    {
        if (_disposed
            || IsLoading
            || IsDetailLoading
            || (SelectedTabIndex == 2 && IsConcurrencyLoading)
            || (SelectedTabIndex == 3 && IsCircuitLoading)
            || Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            if (SelectedTabIndex == 0 && AutoRefresh)
            {
                await LoadInvocationsAsync();
            }
            else if (SelectedTabIndex == 2)
            {
                await LoadConcurrencyAsync();
            }
            else if (SelectedTabIndex == 3)
            {
                await LoadCircuitAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 页面销毁时取消自动刷新，不显示为普通错误。
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadActiveTabAsync();

    public Task RefreshCurrentTabAsync() => LoadActiveTabAsync();

    [RelayCommand]
    private void SelectSimulatorTab(DeveloperSimulatorTab? tab)
    {
        if (tab is null) return;
        foreach (var simulatorTab in SimulatorTabs)
        {
            simulatorTab.IsSelected = ReferenceEquals(simulatorTab, tab);
        }
        SelectedSimulatorTab = tab;
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (!CanPrevious) return;
        await LoadInvocationsAsync(Page - 1);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanNext) return;
        await LoadInvocationsAsync(Page + 1);
    }

    [RelayCommand]
    private async Task OpenDetailAsync(DeveloperInvocationSummary? item)
    {
        if (item is null || _disposed) return;

        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _detailCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        var generation = Interlocked.Increment(ref _detailGeneration);
        IsDetailLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var detail = await _apiService.SendAsync<DeveloperInvocationDetail>(
                HttpMethod.Get,
                $"/api/admin/developer/invocations/{Uri.EscapeDataString(item.TraceId)}?summarize={SummarizeDetail.ToString().ToLowerInvariant()}",
                null,
                true,
                localCancellation.Token);
            if (!IsCurrentDetailRequest(generation, localCancellation)) return;

            SelectedDetail = detail;
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            // 切换详情或页面销毁时取消旧详情请求。
        }
        catch (Exception exception)
        {
            if (IsCurrentDetailRequest(generation, localCancellation))
            {
                ErrorMessage = exception.Message;
            }
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
    private void CloseDetail()
    {
        // 关闭详情时取消未完成请求，避免响应返回后重新打开旧详情。
        Interlocked.Increment(ref _detailGeneration);
        var cancellation = Interlocked.Exchange(ref _detailCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        IsDetailLoading = false;
        SelectedDetail = null;
    }

    [RelayCommand]
    private async Task ResetCircuitAsync(CircuitBreakerRoute? route)
    {
        if (route is null || _disposed) return;
        try
        {
            await _apiService.SendAsync<JsonElement>(
                HttpMethod.Post,
                $"/api/admin/developer/invocations/circuit-breaker/{Uri.EscapeDataString(route.RouteId)}/reset",
                null,
                true,
                _lifetimeCancellation.Token);
            await LoadCircuitAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 页面销毁时取消熔断操作。
        }
        catch (Exception exception)
        {
            if (!_disposed) ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task ResetAllCircuitsAsync()
    {
        if (_disposed) return;
        try
        {
            await _apiService.SendAsync<JsonElement>(
                HttpMethod.Post,
                "/api/admin/developer/invocations/circuit-breaker/reset-all",
                null,
                true,
                _lifetimeCancellation.Token);
            await LoadCircuitAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 页面销毁时取消熔断操作。
        }
        catch (Exception exception)
        {
            if (!_disposed) ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task SendSimulatorAsync(DeveloperSimulatorTab? tab)
    {
        if (tab is null || tab.IsRunning || _disposed) return;
        if (!Uri.TryCreate(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            tab.Response = "请求失败：代理根地址无效";
            return;
        }
        if (string.IsNullOrWhiteSpace(AccessKey))
        {
            tab.Response = "请求失败：请先填写访问密钥";
            return;
        }
        if (tab.Method == "POST" && string.IsNullOrWhiteSpace(SelectedModel))
        {
            tab.Response = "请求失败：请先选择模型";
            return;
        }
        if (tab.Method == "POST" && string.IsNullOrWhiteSpace(InputText))
        {
            tab.Response = "请求失败：请输入测试消息";
            return;
        }
        if (!EnsureProtocolModel(tab)) return;

        var requestBody = BuildSimulatorBody(tab);
        var headers = BuildSimulatorHeaders(tab);
        var requestUri = new Uri(baseUri, tab.Endpoint.TrimStart('/'));
        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        lock (_simulatorCancellationLock)
        {
            if (_simulatorCancellations.ContainsKey(tab))
            {
                localCancellation.Dispose();
                return;
            }

            _simulatorCancellations[tab] = localCancellation;
        }

        tab.IsRunning = true;
        tab.Response = tab.StreamEnabled ? "正在接收流式响应..." : "请求中...";
        try
        {
            DeveloperRawResponse response;
            if (tab.StreamEnabled)
            {
                response = await _apiService.SendRawStreamingAsync(
                    new HttpMethod(tab.Method),
                    requestUri,
                    headers,
                    requestBody,
                    chunk => Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!_disposed) tab.Response += chunk;
                    }).GetTask(),
                    localCancellation.Token);

                if (!_disposed && response.StatusCode >= 400)
                {
                    tab.Response = FormatSimulatorResponse(response);
                }
                else if (!_disposed && string.IsNullOrEmpty(response.Body))
                {
                    tab.Response = "流式响应为空";
                }
            }
            else
            {
                response = await _apiService.SendRawAsync(
                    new HttpMethod(tab.Method),
                    requestUri,
                    headers,
                    requestBody,
                    localCancellation.Token);
                if (!_disposed) tab.Response = FormatSimulatorResponse(response);
            }
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            if (!_disposed) tab.Response = "请求已取消";
        }
        catch (Exception exception)
        {
            if (!_disposed) tab.Response = $"请求失败：{exception.Message}";
        }
        finally
        {
            var isCurrentRequest = false;
            lock (_simulatorCancellationLock)
            {
                if (_simulatorCancellations.TryGetValue(tab, out var currentCancellation)
                    && ReferenceEquals(currentCancellation, localCancellation))
                {
                    _simulatorCancellations.Remove(tab);
                    isCurrentRequest = true;
                }
            }

            if (isCurrentRequest)
            {
                if (!_disposed) tab.IsRunning = false;
                localCancellation.Dispose();
            }
        }
    }

    [RelayCommand]
    private void StopSimulator(DeveloperSimulatorTab? tab)
    {
        if (tab?.IsRunning != true) return;

        lock (_simulatorCancellationLock)
        {
            if (_simulatorCancellations.TryGetValue(tab, out var cancellation))
            {
                cancellation.Cancel();
            }
        }

        if (!_disposed) tab.Response = "请求已取消";
    }

    private bool EnsureProtocolModel(DeveloperSimulatorTab tab)
    {
        var model = Models.FirstOrDefault(x => string.Equals(x.ModelName, SelectedModel, StringComparison.OrdinalIgnoreCase));
        if (model is null || tab.Method == "GET") return true;
        var anthropic = tab.Key is "anthropic" or "countTokens";
        var supported = anthropic ? model.CanUseAnthropic : model.CanUseOpenAi;
        if (supported)
        {
            return true;
        }

        var fallback = Models.FirstOrDefault(x => anthropic ? x.CanUseAnthropic : x.CanUseOpenAi);
        if (fallback is null)
        {
            ErrorMessage = "当前没有支持该协议的模型";
            return false;
        }

        SelectedModel = fallback.ModelName;
        UpdateSimulatorExamples();
        UpdateSupportHint();
        return true;
    }

    private string? BuildSimulatorBody(DeveloperSimulatorTab tab)
    {
        if (tab.Method == "GET") return null;
        object body = tab.Key switch
        {
            "openai" => new { model = SelectedModel, messages = new[] { new { role = "user", content = InputText } }, stream = tab.StreamEnabled },
            "anthropic" => new { model = SelectedModel, max_tokens = 1024, messages = new[] { new { role = "user", content = InputText } }, stream = tab.StreamEnabled },
            "responses" => new { model = SelectedModel, input = InputText, stream = tab.StreamEnabled },
            "completions" => new { model = SelectedModel, prompt = InputText, max_tokens = 256, stream = tab.StreamEnabled },
            "embeddings" => new { model = SelectedModel, input = InputText },
            "countTokens" => new { model = SelectedModel, messages = new[] { new { role = "user", content = InputText } } },
            "responsesCompact" => new { model = SelectedModel, input = InputText },
            _ => new { model = SelectedModel, input = InputText }
        };
        return JsonSerializer.Serialize(body);
    }

    private Dictionary<string, string> BuildSimulatorHeaders(DeveloperSimulatorTab tab)
    {
        if (tab.Key is "anthropic" or "countTokens")
        {
            return new Dictionary<string, string>
            {
                ["x-api-key"] = AccessKey,
                ["anthropic-version"] = "2023-06-01",
                ["Content-Type"] = "application/json"
            };
        }

        return new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {AccessKey}",
            ["Content-Type"] = "application/json"
        };
    }

    private string FormatSimulatorResponse(DeveloperRawResponse response)
    {
        var body = response.Body;
        try
        {
            using var document = JsonDocument.Parse(body);
            body = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // SSE 或纯文本响应保持原样显示。
        }

        return $"HTTP {response.StatusCode}{Environment.NewLine}{body}";
    }

    private void UpdateSimulatorExamples()
    {
        foreach (var tab in SimulatorTabs)
        {
            tab.EndpointUrl = BuildEndpointUrl(tab);
            tab.RequestExample = BuildRequestExample(tab);
        }
    }

    private string BuildEndpointUrl(DeveloperSimulatorTab tab)
        => $"{BaseUrl.TrimEnd('/')}{tab.Endpoint}";

    private string BuildRequestExample(DeveloperSimulatorTab tab)
    {
        var headers = BuildSimulatorHeaders(tab);
        foreach (var key in headers.Keys.ToList()) headers[key] = "***";
        var example = new
        {
            method = tab.Method,
            url = BuildEndpointUrl(tab),
            headers,
            body = tab.Method == "POST" ? JsonSerializer.Deserialize<JsonElement>(BuildSimulatorBody(tab) ?? "{}") : (JsonElement?)null
        };
        return JsonSerializer.Serialize(example, new JsonSerializerOptions { WriteIndented = true });
    }

    private void UpdateSupportHint()
    {
        var model = Models.FirstOrDefault(x => string.Equals(x.ModelName, SelectedModel, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            SupportHint = "请选择支持当前协议的模型。";
            return;
        }

        var labels = new List<string>();
        if (model.CanUseOpenAi) labels.Add("OpenAI");
        if (model.CanUseAnthropic) labels.Add("Anthropic");
        SupportHint = labels.Count == 0 ? "当前模型暂无可用协议。" : $"当前模型支持：{string.Join(" / ", labels)}";
    }

    private void UpdatePaging()
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PaginationSummary));
        OnPropertyChanged(nameof(CanPrevious));
        OnPropertyChanged(nameof(CanNext));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        CancelCurrentListRequest();

        var initializationCancellation = Interlocked.Exchange(ref _initializationCancellation, null);
        initializationCancellation?.Cancel();
        initializationCancellation?.Dispose();
        var detailCancellation = Interlocked.Exchange(ref _detailCancellation, null);
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        var concurrencyCancellation = Interlocked.Exchange(ref _concurrencyCancellation, null);
        concurrencyCancellation?.Cancel();
        concurrencyCancellation?.Dispose();
        var circuitCancellation = Interlocked.Exchange(ref _circuitCancellation, null);
        circuitCancellation?.Cancel();
        circuitCancellation?.Dispose();

        _lifetimeCancellation.Cancel();
        lock (_simulatorCancellationLock)
        {
            foreach (var cancellation in _simulatorCancellations.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }

            _simulatorCancellations.Clear();
        }

        // 取消后仍可能有请求进入 finally 释放分页锁，因此不在这里销毁 SemaphoreSlim。
        _lifetimeCancellation.Dispose();
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnConcurrencyErrorChanged(string value) => OnPropertyChanged(nameof(HasConcurrencyError));
    partial void OnSelectedDetailChanged(DeveloperInvocationDetail? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnIsLoadingChanged(bool value) => UpdatePaging();
    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedTabTitle));
        ConfigureAutoRefresh();
        _ = LoadActiveTabAsync();
    }
    partial void OnAutoRefreshChanged(bool value) => ConfigureAutoRefresh();
    partial void OnSummarizeDetailChanged(bool value)
    {
        if (SelectedDetail is not null)
        {
            var item = Items.FirstOrDefault(x => x.TraceId == SelectedDetail.TraceId);
            if (item is not null) _ = OpenDetailAsync(item);
        }
    }
    partial void OnBaseUrlChanged(string value) => UpdateSimulatorExamples();
    partial void OnAccessKeyChanged(string value) => UpdateSimulatorExamples();
    partial void OnSelectedModelChanged(string value)
    {
        UpdateSupportHint();
        UpdateSimulatorExamples();
    }
    partial void OnInputTextChanged(string value) => UpdateSimulatorExamples();
}
