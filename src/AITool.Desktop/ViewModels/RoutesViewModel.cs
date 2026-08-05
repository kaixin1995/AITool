using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class RoutesViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<RouteEntry> _entries = new();

    [ObservableProperty]
    private ObservableCollection<RouteRuleItem> _rules = new();

    [ObservableProperty]
    private ObservableCollection<SiteInstanceItem> _siteInstances = new();

    [ObservableProperty]
    private SiteInstanceItem? _selectedSiteInstance;

    [ObservableProperty]
    private string _candidateSearch = string.Empty;

    [ObservableProperty]
    private bool _isRefreshingPool;

    [ObservableProperty]
    private bool _isSavingRules;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private RouteEntry? _selectedEntry;

    [ObservableProperty]
    private string _newEntryName = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public RoutesViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public IReadOnlyList<RouteAvailabilityOption> AvailabilityOptions { get; } =
        new[]
        {
            new RouteAvailabilityOption { Value = "AllDay", Label = "全天" },
            new RouteAvailabilityOption { Value = "AvailableOnly", Label = "仅可用" },
            new RouteAvailabilityOption { Value = "Unavailable", Label = "不可用" }
        };

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasSelectedEntry => SelectedEntry is not null;
    public bool HasNoSelectedEntry => !HasSelectedEntry;
    public bool HasRules => Rules.Count > 0;
    public bool HasNoRules => !HasRules;
    public bool HasSiteInstances => FilteredSiteInstances.Any();
    public bool HasNoSiteInstances => !HasSiteInstances;
    public bool CanCreate => !IsCreating && !IsSavingRules;
    public bool CanEdit => HasSelectedEntry && !IsLoading && !IsSavingRules;
    public bool CanAddCandidate => CanEdit && SelectedSiteInstance is not null;
    public bool CanSaveRules => CanEdit && IsDirty;
    public bool CanRefreshPool => !IsRefreshingPool && !IsSavingRules;
    public IEnumerable<SiteInstanceItem> FilteredSiteInstances => SiteInstances
        .Where(instance => string.IsNullOrWhiteSpace(CandidateSearch)
            || instance.SiteName.Contains(CandidateSearch.Trim(), StringComparison.OrdinalIgnoreCase)
            || instance.SiteModelName.Contains(CandidateSearch.Trim(), StringComparison.OrdinalIgnoreCase))
        .ToList();

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var entriesTask = _apiService.SendAsync<List<RouteEntry>>(
                HttpMethod.Get,
                "/api/admin/route-rules/entries",
                null);
            var instancesTask = _apiService.SendAsync<List<SiteInstanceItem>>(
                HttpMethod.Get,
                "/api/admin/route-rules/site-instances",
                null);

            Entries = new ObservableCollection<RouteEntry>(await entriesTask);
            SiteInstances = new ObservableCollection<SiteInstanceItem>(await instancesTask);
            if (SelectedEntry is not null && Entries.All(entry => entry.EntryName != SelectedEntry.EntryName))
            {
                SelectedEntry = null;
            }

            SelectedEntry ??= Entries.FirstOrDefault();
            await LoadRulesAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    public async Task SelectEntryAsync(RouteEntry? entry)
    {
        if (entry is null || SelectedEntry?.EntryName == entry.EntryName) return;
        SelectedEntry = entry;
        await LoadRulesAsync();
    }

    [RelayCommand]
    private async Task CreateEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEntryName))
        {
            ErrorMessage = "请输入路由入口名称";
            return;
        }

        IsCreating = true;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, "/api/admin/route-rules/entries", new { entryName = NewEntryName.Trim() });
            NewEntryName = string.Empty;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsCreating = false;
        }
    }

    [RelayCommand]
    private async Task DeleteEntryAsync()
    {
        if (!CanEdit || SelectedEntry is null) return;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                "/api/admin/route-rules/entries/delete",
                new { entryName = SelectedEntry.EntryName });
            IsDirty = false;
            SelectedEntry = null;
            Message = "路由入口已删除";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshSiteInstancesAsync()
    {
        if (!CanRefreshPool) return;
        IsRefreshingPool = true;
        ErrorMessage = string.Empty;
        try
        {
            var instances = await _apiService.SendAsync<List<SiteInstanceItem>>(
                HttpMethod.Get,
                "/api/admin/route-rules/site-instances",
                null);
            SiteInstances = new ObservableCollection<SiteInstanceItem>(instances);
            Message = "实例池已刷新";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsRefreshingPool = false;
            NotifyPoolProperties();
        }
    }

    [RelayCommand]
    private void AddSelectedCandidate()
    {
        if (!CanEdit || SelectedEntry is null || SelectedSiteInstance is null) return;
        if (Rules.Any(rule => rule.SiteId == SelectedSiteInstance.SiteId
            && rule.SiteModelName == SelectedSiteInstance.SiteModelName))
        {
            ErrorMessage = "该站点实例已在当前候选队列中";
            return;
        }

        var newRule = new RouteRuleItem
        {
            SiteId = SelectedSiteInstance.SiteId,
            SiteName = SelectedSiteInstance.SiteName,
            SiteEnabled = SelectedSiteInstance.SiteEnabled,
            UpstreamModelName = SelectedSiteInstance.SiteModelName,
            SiteModelName = SelectedSiteInstance.SiteModelName,
            Priority = Rules.Count,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true,
            AvailabilityMode = "AllDay"
        };
        newRule.PropertyChanged += OnRulePropertyChanged;
        Rules.Add(newRule);
        SelectedSiteInstance = null;
        MarkDirty();
        UpdateRulePositions();
    }

    [RelayCommand]
    private void MoveRuleUp(RouteRuleItem? rule)
    {
        MoveRule(rule, -1);
    }

    [RelayCommand]
    private void MoveRuleDown(RouteRuleItem? rule)
    {
        MoveRule(rule, 1);
    }

    private void MoveRule(RouteRuleItem? rule, int direction)
    {
        if (!CanEdit || rule is null) return;
        var index = Rules.IndexOf(rule);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Rules.Count) return;

        Rules.Move(index, target);
        MarkDirty();
        UpdateRulePositions();
    }

    [RelayCommand]
    private void RemoveCandidate(RouteRuleItem? rule)
    {
        if (!CanEdit || rule is null) return;
        rule.PropertyChanged -= OnRulePropertyChanged;
        Rules.Remove(rule);
        MarkDirty();
        UpdateRulePositions();
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        if (!CanSaveRules || SelectedEntry is null) return;
        IsSavingRules = true;
        ErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                "/api/admin/route-rules/save",
                new
                {
                    externalModelName = SelectedEntry.EntryName,
                    rules = Rules.Select(rule => new
                    {
                        siteId = rule.SiteId,
                        siteModelName = rule.SiteModelName,
                        upstreamModelName = rule.UpstreamModelName,
                        isEnabled = rule.IsEnabled,
                        availabilityMode = rule.AvailabilityMode,
                        timeRangesJson = rule.TimeRangesJson
                    }).ToList()
                });
            IsDirty = false;
            Message = "路由候选队列已保存";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsSavingRules = false;
            NotifyEditProperties();
        }
    }

    [RelayCommand]
    private async Task ToggleRuleAsync(RouteRuleItem? rule)
    {
        if (rule is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/route-rules/toggle/{rule.RuleId}", null);
            rule.IsEnabled = !rule.IsEnabled;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(RouteRuleItem? rule)
    {
        if (rule is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/route-rules/delete/{rule.RuleId}", null);
            await LoadRulesAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task LoadRulesAsync()
    {
        if (SelectedEntry is null)
        {
            Rules = new ObservableCollection<RouteRuleItem>();
            IsDirty = false;
            UpdateRulePositions();
            NotifyEditProperties();
            return;
        }

        try
        {
            var rules = await _apiService.SendAsync<List<RouteRuleItem>>(
                HttpMethod.Get,
                $"/api/admin/route-rules/list?modelName={Uri.EscapeDataString(SelectedEntry.EntryName)}",
                null);
            Rules = new ObservableCollection<RouteRuleItem>(rules.OrderBy(rule => rule.Priority));
            IsDirty = false;
            UpdateRulePositions();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            NotifyEditProperties();
        }
    }

    partial void OnSelectedEntryChanged(RouteEntry? value)
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = ReferenceEquals(entry, value);
        }

        SelectedSiteInstance = null;
        OnPropertyChanged(nameof(HasSelectedEntry));
        OnPropertyChanged(nameof(HasNoSelectedEntry));
        NotifyEditProperties();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        Message = string.Empty;
        ErrorMessage = string.Empty;
        NotifyEditProperties();
    }

    private void UpdateRulePositions()
    {
        if (SelectedEntry is not null)
        {
            SelectedEntry.CandidateCount = Rules.Count;
        }

        for (var index = 0; index < Rules.Count; index++)
        {
            Rules[index].SetPriority(index);
            Rules[index].CanMoveUp = index > 0;
            Rules[index].CanMoveDown = index < Rules.Count - 1;
        }

        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
    }

    private void NotifyPoolProperties()
    {
        OnPropertyChanged(nameof(FilteredSiteInstances));
        OnPropertyChanged(nameof(HasSiteInstances));
        OnPropertyChanged(nameof(HasNoSiteInstances));
        OnPropertyChanged(nameof(CanRefreshPool));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAddCandidate));
    }

    private void NotifyEditProperties()
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAddCandidate));
        OnPropertyChanged(nameof(CanSaveRules));
        OnPropertyChanged(nameof(CanRefreshPool));
    }

    partial void OnRulesChanged(ObservableCollection<RouteRuleItem> value)
    {
        foreach (var rule in value)
        {
            rule.PropertyChanged += OnRulePropertyChanged;
        }

        UpdateRulePositions();
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(RouteRuleItem.AvailabilityMode)
            or nameof(RouteRuleItem.TimeRangeStart)
            or nameof(RouteRuleItem.TimeRangeEnd))
        {
            MarkDirty();
        }
    }

    partial void OnSiteInstancesChanged(ObservableCollection<SiteInstanceItem> value) => NotifyPoolProperties();
    partial void OnCandidateSearchChanged(string value) => NotifyPoolProperties();
    partial void OnSelectedSiteInstanceChanged(SiteInstanceItem? value) => NotifyEditProperties();
    partial void OnIsLoadingChanged(bool value) => NotifyEditProperties();
    partial void OnIsSavingRulesChanged(bool value) => NotifyEditProperties();
    partial void OnIsRefreshingPoolChanged(bool value) => NotifyPoolProperties();
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(CanSaveRules));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnIsCreatingChanged(bool value) => NotifyEditProperties();
}
