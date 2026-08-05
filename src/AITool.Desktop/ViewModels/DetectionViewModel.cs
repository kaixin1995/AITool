using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class DetectionViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty] private ObservableCollection<DetectionModelGroup> _groups = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _searchKeyword = string.Empty;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private int _progressTotal;
    [ObservableProperty] private int _progressCompleted;
    [ObservableProperty] private int _successCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private bool _hasProgress;

    public DetectionViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasGroups => Groups.Count > 0;
    public bool HasNoGroups => !HasGroups;
    public bool ShowEmptyState => !IsLoading && !HasError && HasNoGroups;
    public bool ShowNoMatchState => !IsLoading && !HasError && HasGroups && !FilteredGroups.Any();
    public bool CanProbe => !IsBusy && !IsLoading && HasGroups;

    // 模型名称和站点名称共用一个搜索入口，站点匹配时只显示匹配的站点行。
    public IEnumerable<DetectionModelGroup> FilteredGroups
    {
        get
        {
            var keyword = SearchKeyword.Trim();
            if (string.IsNullOrWhiteSpace(keyword)) return Groups;

            return Groups
                .Select(group =>
                {
                    var modelMatches = ContainsKeyword(group.DisplayName, keyword)
                        || ContainsKeyword(group.ModelName, keyword);
                    var matchedSites = group.Sites
                        .Where(site => ContainsKeyword(site.SiteName, keyword)
                            || ContainsKeyword(site.RemoteModelName, keyword))
                        .ToList();

                    if (modelMatches) return group;
                    if (matchedSites.Count == 0) return null;

                    return new DetectionModelGroup
                    {
                        ModelLibraryItemId = group.ModelLibraryItemId,
                        ModelName = group.ModelName,
                        DisplayName = group.DisplayName,
                        Sites = matchedSites
                    };
                })
                .Where(group => group is not null)
                .Select(group => group!)
                .ToList();
        }
    }

    public int VisibleGroupCount => FilteredGroups.Count();

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var response = await _apiService.SendAsync<DetectionMatrix>(HttpMethod.Get, "/api/admin/detection/matrix", null);
            Groups = new ObservableCollection<DetectionModelGroup>(response.ModelGroups);
        }
        catch (Exception exception)
        {
            ErrorMessage = FormatError(exception);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task ProbeMappingAsync(DetectionSiteStatus? site)
    {
        if (site is null || IsBusy) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<ProbeResultItem>(
                HttpMethod.Post,
                $"/api/admin/detection/probe/{site.MappingId}",
                null);
            ApplyProbeResult(result);
            if (!string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = result.Error ?? "检测失败";
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = FormatError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task ProbeModelAsync(DetectionModelGroup? group)
    {
        return group is null
            ? Task.CompletedTask
            : StartBatchAsync($"/api/admin/detection/probe-model/{group.ModelLibraryItemId}");
    }

    [RelayCommand]
    private Task ProbeAllAsync() => StartBatchAsync("/api/admin/detection/probe-all");

    private async Task StartBatchAsync(string path)
    {
        if (IsBusy || IsLoading || !HasGroups) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        ProgressPercent = 0;
        ProgressTotal = 0;
        ProgressCompleted = 0;
        SuccessCount = 0;
        FailedCount = 0;
        HasProgress = true;
        ProgressText = "正在提交检测任务...";
        var results = new List<ProbeResultItem>();

        try
        {
            var created = await _apiService.SendAsync<Dictionary<string, string>>(HttpMethod.Post, path, null);
            if (!created.TryGetValue("taskId", out var taskId) || string.IsNullOrWhiteSpace(taskId))
            {
                throw new ApiException("检测任务标识无效", string.Empty, 200);
            }

            var completed = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(1200);
                var progress = await _apiService.SendAsync<ProbeProgress>(
                    HttpMethod.Get,
                    $"/api/admin/detection/progress/{taskId}",
                    null);

                foreach (var result in progress.NewResults)
                {
                    results.Add(result);
                    ApplyProbeResult(result);
                    if (string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase))
                    {
                        SuccessCount++;
                    }
                    else
                    {
                        FailedCount++;
                    }
                }

                ProgressTotal = progress.Total;
                ProgressCompleted = progress.Completed;
                ProgressPercent = progress.Total == 0
                    ? (progress.IsCompleted ? 100 : 0)
                    : Math.Clamp((int)Math.Round(progress.Completed * 100d / progress.Total), 0, 100);
                ProgressText = progress.IsCompleted
                    ? $"检测完成：{SuccessCount} 成功，{FailedCount} 失败"
                    : $"检测进度：{progress.Completed} / {progress.Total}";

                if (progress.IsCompleted)
                {
                    completed = true;
                    ProgressPercent = 100;
                    break;
                }
            }

            if (!completed)
            {
                ErrorMessage = $"检测任务超时：已完成 {ProgressCompleted} / {ProgressTotal}";
                ProgressText = "检测任务超时";
                return;
            }

            // 完成后同步矩阵，并重新应用结果以保留单项错误信息。
            await LoadAsync();
            foreach (var result in results) ApplyProbeResult(result);
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            ErrorMessage = "检测任务已过期，请重新发起检测";
            ProgressText = "检测任务已过期";
        }
        catch (HttpRequestException exception)
        {
            ErrorMessage = $"网络错误：{exception.Message}";
            ProgressText = "检测请求失败";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "网络错误：请求超时";
            ProgressText = "检测请求超时";
        }
        catch (Exception exception)
        {
            ErrorMessage = FormatError(exception);
            ProgressText = "检测请求失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyProbeResult(ProbeResultItem result)
    {
        var site = Groups
            .SelectMany(group => group.Sites)
            .FirstOrDefault(item => item.MappingId == result.MappingId);
        if (site is null) return;

        site.LastStatus = result.Status;
        site.LastDurationMs = result.DurationMs;
        site.LastError = result.Error ?? string.Empty;
        site.LastCheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static bool ContainsKeyword(string value, string keyword) =>
        value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string FormatError(Exception exception) => exception switch
    {
        HttpRequestException requestException => $"网络错误：{requestException.Message}",
        TaskCanceledException => "网络错误：请求超时",
        _ => string.IsNullOrWhiteSpace(exception.Message) ? "请求失败" : exception.Message
    };

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoMatchState));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanProbe));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoMatchState));
        OnPropertyChanged(nameof(CanProbe));
    }

    partial void OnGroupsChanged(ObservableCollection<DetectionModelGroup> value)
    {
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(FilteredGroups));
        OnPropertyChanged(nameof(VisibleGroupCount));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoMatchState));
        OnPropertyChanged(nameof(CanProbe));
    }

    partial void OnSearchKeywordChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredGroups));
        OnPropertyChanged(nameof(VisibleGroupCount));
        OnPropertyChanged(nameof(ShowNoMatchState));
    }
}
