using System.Collections.ObjectModel;
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

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasSelectedEntry => SelectedEntry is not null;
    public bool HasNoSelectedEntry => !HasSelectedEntry;
    public bool CanCreate => !IsCreating;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var entries = await _apiService.SendAsync<List<RouteEntry>>(HttpMethod.Get, "/api/admin/route-rules/entries", null);
            Entries = new ObservableCollection<RouteEntry>(entries);
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

    [RelayCommand]
    private async Task SelectEntryAsync(RouteEntry? entry)
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
            return;
        }

        var rules = await _apiService.SendAsync<List<RouteRuleItem>>(
            HttpMethod.Get,
            $"/api/admin/route-rules/list?modelName={Uri.EscapeDataString(SelectedEntry.EntryName)}",
            null);
        Rules = new ObservableCollection<RouteRuleItem>(rules.OrderBy(rule => rule.Priority));
    }

    partial void OnSelectedEntryChanged(RouteEntry? value)
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = ReferenceEquals(entry, value);
        }

        OnPropertyChanged(nameof(HasSelectedEntry));
        OnPropertyChanged(nameof(HasNoSelectedEntry));
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsCreatingChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
}
