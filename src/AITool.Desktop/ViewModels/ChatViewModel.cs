using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class ChatViewModel : ViewModelBase, IDisposable
{
    private readonly ApiService _apiService;
    private readonly SseClient _sseClient;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _streamCancellation;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<ChatModelTarget> _targets = new();
    [ObservableProperty] private ChatModelTarget? _selectedTarget;
    [ObservableProperty] private string _modelSearchText = string.Empty;
    [ObservableProperty] private ObservableCollection<ChatMessage> _messages = new();
    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private bool _enableStreaming;
    [ObservableProperty] private bool _enableReasoning;
    [ObservableProperty] private string _reasoningEffort = "high";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public ChatViewModel(ApiService apiService, SseClient sseClient)
    {
        _apiService = apiService;
        _sseClient = sseClient;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessages => Messages.Count > 0;
    public bool HasNoMessages => !HasMessages;
    public ChatMessage? LatestAssistantMessage => Messages.LastOrDefault(message => !message.IsUser);
    public string CurrentReasoning => LatestAssistantMessage?.Reasoning ?? string.Empty;
    public bool HasCurrentReasoning => !string.IsNullOrWhiteSpace(CurrentReasoning);
    public bool HasNoCurrentReasoning => !HasCurrentReasoning;
    public IEnumerable<ChatAttemptResult> LastAttempts => LatestAssistantMessage?.Attempts ?? Enumerable.Empty<ChatAttemptResult>();
    public bool HasLastAttempts => LastAttempts.Any();
    public bool HasNoLastAttempts => !HasLastAttempts;
    public bool CanSend => !IsSending && SelectedTarget is not null && !string.IsNullOrWhiteSpace(Input);
    public bool CanClear => !IsSending && HasMessages;
    public IEnumerable<ChatModelTarget> FilteredTargets
        => Targets.Where(target => string.IsNullOrWhiteSpace(ModelSearchText)
            || $"{target.ModelDisplayName} {target.SiteName} {target.SiteModelName}"
                .Contains(ModelSearchText.Trim(), StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> ReasoningEffortOptions { get; } = ["low", "medium", "high", "xhigh", "max"];

    public async Task LoadAsync()
    {
        try
        {
            var targets = await _apiService.SendAsync<List<ChatModelTarget>>(HttpMethod.Get, "/api/admin/chat/targets", null);
            Targets = new ObservableCollection<ChatModelTarget>(targets);
            SelectedTarget ??= Targets.FirstOrDefault();
            OnPropertyChanged(nameof(FilteredTargets));
        }
        catch (Exception exception) { ErrorMessage = exception.Message; }
    }

    [RelayCommand] private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSend || SelectedTarget is null) return;
        var text = Input.Trim();
        Input = string.Empty;
        ErrorMessage = string.Empty;
        var user = new ChatMessage { IsUser = true, Content = text, CreatedAt = DateTime.Now.ToString("HH:mm:ss") };
        var assistant = new ChatMessage { IsUser = false, IsStreaming = EnableStreaming, CreatedAt = DateTime.Now.ToString("HH:mm:ss") };
        Messages.Add(user);
        Messages.Add(assistant);
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasNoMessages));
        OnPropertyChanged(nameof(CanClear));
        NotifySidePanel();
        IsSending = true;
        var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previousCancellation = Interlocked.Exchange(ref _streamCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        try
        {
            object body = new { modelId = SelectedTarget.ModelId, mappingId = SelectedTarget.MappingId, message = text, enableReasoning = EnableReasoning, enableStreaming = false, reasoningEffort = ReasoningEffort };
            if (EnableStreaming)
            {
                body = new { modelId = SelectedTarget.ModelId, mappingId = SelectedTarget.MappingId, message = text, enableReasoning = EnableReasoning, enableStreaming = true, reasoningEffort = ReasoningEffort };
                await foreach (var item in _sseClient.StreamAsync("/api/admin/chat/send-stream", body, localCancellation.Token))
                {
                    HandleStreamEvent(item, assistant);
                    NotifySidePanel();
                }
            }
            else
            {
                var result = await _apiService.SendAsync<ChatSendResult>(
                    HttpMethod.Post,
                    "/api/admin/chat/send",
                    body,
                    true,
                    localCancellation.Token);
                assistant.Content = result.Success ? result.Content : $"错误：{result.Error ?? "未知错误"}";
                assistant.Reasoning = result.ReasoningContent ?? string.Empty;
                assistant.Attempts = new ObservableCollection<ChatAttemptResult>(result.Attempts);
                assistant.TotalDurationMs = result.TotalDurationMs;
                assistant.IsError = !result.Success;
                NotifySidePanel();
            }
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            assistant.Content = "（已停止）";
            NotifySidePanel();
        }
        catch (Exception exception) { assistant.Content = $"错误：{exception.Message}"; assistant.IsError = true; ErrorMessage = exception.Message; NotifySidePanel(); }
        finally
        {
            if (ReferenceEquals(_streamCancellation, localCancellation))
            {
                Interlocked.CompareExchange(ref _streamCancellation, null, localCancellation);
                IsSending = false;
                OnPropertyChanged(nameof(CanSend));
            }

            localCancellation.Dispose();
        }
    }

    private static void HandleStreamEvent(SseEvent item, ChatMessage assistant)
    {
        try
        {
            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            if (item.EventType == "token" && root.TryGetProperty("content", out var content))
            {
                assistant.Content += content.GetString() ?? string.Empty;
            }
            else if (item.EventType == "reasoning" && root.TryGetProperty("content", out var reasoning))
            {
                assistant.Reasoning += reasoning.GetString() ?? string.Empty;
            }
            else if (item.EventType == "meta")
            {
                ApplyStreamMetadata(root, assistant);
            }
            else if (item.EventType == "error")
            {
                ApplyStreamMetadata(root, assistant);
                if (root.TryGetProperty("message", out var error))
                {
                    assistant.Content = $"错误：{error.GetString()}";
                }

                assistant.IsError = true;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 的 SSE 数据不影响已接收的对话内容。
        }
    }

    private static void ApplyStreamMetadata(JsonElement root, ChatMessage assistant)
    {
        if (root.TryGetProperty("attempts", out var attempts)
            && attempts.ValueKind == JsonValueKind.Array)
        {
            var values = JsonSerializer.Deserialize<List<ChatAttemptResult>>(
                attempts.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            assistant.Attempts = new ObservableCollection<ChatAttemptResult>(values ?? []);
        }

        if (root.TryGetProperty("totalDurationMs", out var duration)
            && duration.TryGetInt32(out var durationMs))
        {
            assistant.TotalDurationMs = durationMs;
        }
    }

    [RelayCommand]
    private void Stop() => _streamCancellation?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        if (!CanClear) return;
        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasNoMessages));
        OnPropertyChanged(nameof(CanClear));
        NotifySidePanel();
    }

    private void NotifySidePanel()
    {
        OnPropertyChanged(nameof(LatestAssistantMessage));
        OnPropertyChanged(nameof(CurrentReasoning));
        OnPropertyChanged(nameof(HasCurrentReasoning));
        OnPropertyChanged(nameof(HasNoCurrentReasoning));
        OnPropertyChanged(nameof(LastAttempts));
        OnPropertyChanged(nameof(HasLastAttempts));
        OnPropertyChanged(nameof(HasNoLastAttempts));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnModelSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredTargets));
    partial void OnInputChanged(string value) => OnPropertyChanged(nameof(CanSend));
    partial void OnSelectedTargetChanged(ChatModelTarget? value) => OnPropertyChanged(nameof(CanSend));
    partial void OnIsSendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanClear));
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        var streamCancellation = Interlocked.Exchange(ref _streamCancellation, null);
        streamCancellation?.Cancel();
        streamCancellation?.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
