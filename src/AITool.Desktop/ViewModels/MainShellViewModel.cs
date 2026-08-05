using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(string key, string label, string icon)
    {
        Key = key;
        Label = label;
        Icon = icon;
    }

    public string Key { get; }
    public string Label { get; }
    public string Icon { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class NavigationGroupViewModel
{
    public NavigationGroupViewModel(string title, IEnumerable<NavigationItemViewModel> items)
    {
        Title = title;
        Items = new ObservableCollection<NavigationItemViewModel>(items);
    }

    public string Title { get; }
    public ObservableCollection<NavigationItemViewModel> Items { get; }
}

public partial class MainShellViewModel : ViewModelBase
{
    private readonly ApiService _apiService;
    private readonly SseClient _sseClient;
    private readonly NavigationService _navigationService;
    private int _navigationVersion;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private NavigationItemViewModel? _selectedItem;

    public MainShellViewModel(ApiService apiService, SseClient sseClient, NavigationService navigationService, AuthStatus status)
    {
        _apiService = apiService;
        _sseClient = sseClient;
        _navigationService = navigationService;
        NavigationGroups = BuildNavigationGroups(status.Features);
        SelectedItem = NavigationGroups.SelectMany(group => group.Items).FirstOrDefault();
        if (SelectedItem is not null)
        {
            SelectedItem.IsSelected = true;
            CurrentPage = new PlaceholderPageViewModel(SelectedItem.Label, "正在加载页面...");
        }

        _navigationService.Navigated += OnNavigated;
    }

    public ObservableCollection<NavigationGroupViewModel> NavigationGroups { get; }

    public event EventHandler? LogoutCompleted;

    public async Task InitializeAsync()
    {
        if (SelectedItem is not null)
        {
            await NavigateAsync(SelectedItem);
        }
    }

    [RelayCommand]
    private async Task NavigateAsync(NavigationItemViewModel? item)
    {
        if (item is null) return;
        if (ReferenceEquals(SelectedItem, item) && CurrentPage is not PlaceholderPageViewModel) return;

        foreach (var navigationItem in NavigationGroups.SelectMany(group => group.Items))
        {
            navigationItem.IsSelected = ReferenceEquals(navigationItem, item);
        }

        SelectedItem = item;
        var navigationVersion = Interlocked.Increment(ref _navigationVersion);
        var page = await LoadPageAsync(item);

        // 只提交最后一次导航结果，避免快速点击时旧页面覆盖新页面。
        if (navigationVersion != Volatile.Read(ref _navigationVersion) || !ReferenceEquals(SelectedItem, item))
        {
            (page as IDisposable)?.Dispose();
            return;
        }

        if (CurrentPage is IDisposable disposablePage)
        {
            disposablePage.Dispose();
        }

        CurrentPage = page;
        if (page is PlaceholderPageViewModel)
        {
            _navigationService.Navigate(page);
        }
    }

    private async Task<object> LoadPageAsync(NavigationItemViewModel item)
    {
        switch (item.Key)
        {
            case "dashboard":
            {
                var page = new DashboardViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "sites":
            {
                var page = new SitesViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "models":
            {
                var page = new ModelsViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "access-keys":
            {
                var page = new AccessKeysViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "routes":
            {
                var page = new RoutesViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "detection":
            {
                var page = new DetectionViewModel(_apiService);
                // 先显示页面，再在后台加载检测矩阵，避免切换导航时等待接口返回。
                _ = page.LoadAsync();
                return page;
            }
            case "detection-tasks":
            {
                var page = new DetectionTasksViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "model-health":
            {
                var page = new ModelHealthViewModel(_apiService);
                _ = page.LoadAsync();
                return page;
            }
            case "usage-logs":
            {
                var page = new UsageLogsViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "system-settings":
            {
                var page = new SystemSettingsViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "codex":
            {
                var page = new CodexViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "developer-invocations":
            {
                var page = new DeveloperInvocationsViewModel(_apiService);
                await page.LoadAsync();
                return page;
            }
            case "chat":
            {
                var page = new ChatViewModel(_apiService, _sseClient);
                await page.LoadAsync();
                return page;
            }
            default:
            {
                var description = item.Key switch
                {
                    "analytics" => "图表页面将在桌面端后续接入专用图表组件。",
                    "model-health" => "模型健康时间线将在桌面端后续接入专用图表组件。",
                    _ => $"{item.Label}页面正在迁移到 Avalonia 桌面端。"
                };
                return new PlaceholderPageViewModel(item.Label, description);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshCurrentPageAsync()
    {
        switch (CurrentPage)
        {
            case DashboardViewModel dashboard:
                await dashboard.LoadAsync();
                break;
            case SitesViewModel sites:
                await sites.LoadAsync();
                break;
            case ModelsViewModel models:
                await models.LoadAsync();
                break;
            case AccessKeysViewModel accessKeys:
                await accessKeys.LoadAsync();
                break;
            case RoutesViewModel routes:
                await routes.LoadAsync();
                break;
            case DetectionViewModel detection:
                await detection.LoadAsync();
                break;
            case DetectionTasksViewModel detectionTasks:
                await detectionTasks.LoadAsync();
                break;
            case ModelHealthViewModel modelHealth:
                await modelHealth.LoadAsync();
                break;
            case UsageLogsViewModel usageLogs:
                await usageLogs.LoadAsync();
                break;
            case SystemSettingsViewModel systemSettings:
                await systemSettings.LoadAsync();
                break;
            case CodexViewModel codex:
                await codex.LoadAsync();
                break;
            case DeveloperInvocationsViewModel developer:
                await developer.LoadAsync();
                break;
            case ChatViewModel chat:
                await chat.LoadAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            await _apiService.LogoutAsync();
        }
        catch
        {
            // 登出失败时仍然回到认证页面，避免本地会话残留。
        }

        LogoutCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnNavigated(object? sender, EventArgs args)
    {
        if (_navigationService.CurrentViewModel is not null)
        {
            CurrentPage = _navigationService.CurrentViewModel;
        }
    }

    private static ObservableCollection<NavigationGroupViewModel> BuildNavigationGroups(AuthFeatures features)
    {
        var groups = new ObservableCollection<NavigationGroupViewModel>
        {
            new(
                "概览",
                new[]
                {
                    new NavigationItemViewModel("dashboard", "仪表盘", "📊"),
                    new NavigationItemViewModel("analytics", "可视化分析", "🛰️"),
                    new NavigationItemViewModel("chat", "对话", "💬")
                }),
            new(
                "资源管理",
                new[]
                {
                    new NavigationItemViewModel("sites", "站点管理", "🌐"),
                    new NavigationItemViewModel("models", "模型库", "🧠")
                }),
            new(
                "代理配置",
                new[]
                {
                    new NavigationItemViewModel("routes", "路由管理", "🔀"),
                    new NavigationItemViewModel("access-keys", "访问密钥", "🔑")
                }),
            new(
                "监控运维",
                new[]
                {
                    new NavigationItemViewModel("detection", "模型检测", "🔍"),
                    new NavigationItemViewModel("detection-tasks", "检测任务", "⏰"),
                    new NavigationItemViewModel("model-health", "模型健康", "💊"),
                    new NavigationItemViewModel("usage-logs", "使用日志", "📋"),
                    new NavigationItemViewModel("system-settings", "系统设置", "⚙️")
                })
        };

        if (features.CodexEnabled)
        {
            groups[1].Items.Insert(1, new NavigationItemViewModel("codex", "OAuth 管理", "🔐"));
        }

        if (features.DeveloperEnabled)
        {
            groups[3].Items.Insert(3, new NavigationItemViewModel("developer-invocations", "调试工具", "🛠️"));
        }

        return groups;
    }
}
