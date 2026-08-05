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
    private Timer? _refreshTimer;
    private CancellationTokenSource? _simulatorCancellation;
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
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var init = await _apiService.SendAsync<DeveloperInitResponse>(
                HttpMethod.Get,
                "/api/admin/developer/invocations/init",
                null);
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
            await LoadInvocationsAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
            UpdatePaging();
            ConfigureAutoRefresh();
        }
    }

    private async Task LoadInvocationsAsync()
    {
        var response = await _apiService.SendAsync<DeveloperListResponse>(
            HttpMethod.Get,
            $"/api/admin/developer/invocations/list?page={Page}&pageSize=40",
            null);
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

    private async Task LoadConcurrencyAsync()
    {
        IsConcurrencyLoading = true;
        ConcurrencyError = string.Empty;
        try
        {
            var response = await _apiService.SendAsync<DeveloperConcurrencyResponse>(
                HttpMethod.Get,
                "/api/admin/developer/invocations/concurrency",
                null);
            Concurrency = new ObservableCollection<DeveloperConcurrencyItem>(response.Items);
            OnPropertyChanged(nameof(HasConcurrency));
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            Concurrency.Clear();
            OnPropertyChanged(nameof(HasConcurrency));
            OnPropertyChanged(nameof(HasNoConcurrency));
            ConcurrencyError = string.Empty;
        }
        catch (Exception exception)
        {
            ConcurrencyError = exception.Message;
        }
        finally
        {
            IsConcurrencyLoading = false;
        }
    }

    private async Task LoadCircuitAsync()
    {
        IsCircuitLoading = true;
        try
        {
            var response = await _apiService.SendAsync<CircuitBreakerResponse>(
                HttpMethod.Get,
                "/api/admin/developer/invocations/circuit-breaker",
                null);
            CircuitRoutes = new ObservableCollection<CircuitBreakerRoute>(response.Routes);
            OnPropertyChanged(nameof(HasCircuitRoutes));
            OnPropertyChanged(nameof(HasBlockedCircuits));
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            CircuitRoutes.Clear();
            OnPropertyChanged(nameof(HasCircuitRoutes));
            OnPropertyChanged(nameof(HasBlockedCircuits));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsCircuitLoading = false;
        }
    }

    private async Task LoadActiveTabAsync()
    {
        if (SelectedTabIndex == 0)
        {
            try { await LoadInvocationsAsync(); }
            catch (Exception exception) { ErrorMessage = exception.Message; }
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
        if (_disposed || IsLoading || IsDetailLoading) return;
        if (SelectedTabIndex == 0 && AutoRefresh)
        {
            try { await LoadInvocationsAsync(); } catch { /* 自动刷新失败时保留当前页面 */ }
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

    [RelayCommand]
    private Task RefreshAsync() => LoadActiveTabAsync();

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
        Page--;
        await LoadInvocationsAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanNext) return;
        Page++;
        await LoadInvocationsAsync();
    }

    [RelayCommand]
    private async Task OpenDetailAsync(DeveloperInvocationSummary? item)
    {
        if (item is null) return;
        IsDetailLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            SelectedDetail = await _apiService.SendAsync<DeveloperInvocationDetail>(
                HttpMethod.Get,
                $"/api/admin/developer/invocations/{Uri.EscapeDataString(item.TraceId)}?summarize={SummarizeDetail.ToString().ToLowerInvariant()}",
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

    [RelayCommand]
    private async Task ResetCircuitAsync(CircuitBreakerRoute? route)
    {
        if (route is null) return;
        try
        {
            await _apiService.SendAsync<JsonElement>(
                HttpMethod.Post,
                $"/api/admin/developer/invocations/circuit-breaker/{Uri.EscapeDataString(route.RouteId)}/reset",
                null);
            await LoadCircuitAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task ResetAllCircuitsAsync()
    {
        try
        {
            await _apiService.SendAsync<JsonElement>(
                HttpMethod.Post,
                "/api/admin/developer/invocations/circuit-breaker/reset-all",
                null);
            await LoadCircuitAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task SendSimulatorAsync(DeveloperSimulatorTab? tab)
    {
        if (tab is null || tab.IsRunning) return;
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
        _simulatorCancellation?.Cancel();
        _simulatorCancellation?.Dispose();
        _simulatorCancellation = new CancellationTokenSource();
        tab.IsRunning = true;
        tab.Response = tab.StreamEnabled ? "正在接收流式响应..." : "请求中...";
        try
        {
            var response = await _apiService.SendRawAsync(
                new HttpMethod(tab.Method),
                requestUri,
                headers,
                requestBody,
                _simulatorCancellation.Token);
            tab.Response = FormatSimulatorResponse(response);
        }
        catch (OperationCanceledException)
        {
            tab.Response = "请求已取消";
        }
        catch (Exception exception)
        {
            tab.Response = $"请求失败：{exception.Message}";
        }
        finally
        {
            tab.IsRunning = false;
            _simulatorCancellation?.Dispose();
            _simulatorCancellation = null;
        }
    }

    [RelayCommand]
    private void StopSimulator(DeveloperSimulatorTab? tab)
    {
        if (tab?.IsRunning == true)
        {
            _simulatorCancellation?.Cancel();
            tab.Response = "请求已取消";
        }
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
        _simulatorCancellation?.Cancel();
        _simulatorCancellation?.Dispose();
        _simulatorCancellation = null;
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnConcurrencyErrorChanged(string value) => OnPropertyChanged(nameof(HasConcurrencyError));
    partial void OnSelectedDetailChanged(DeveloperInvocationDetail? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedTabTitle));
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
