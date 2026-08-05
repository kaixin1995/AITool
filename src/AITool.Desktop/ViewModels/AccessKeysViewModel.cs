using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class AccessKeysViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<AccessKeyItem> _items = new();

    [ObservableProperty]
    private ObservableCollection<RouteEntry> _routeEntries = new();

    [ObservableProperty]
    private AccessKeyItem? _editingRoutesKey;

    [ObservableProperty]
    private bool _isRoutesEditorOpen;

    [ObservableProperty]
    private bool _isRouteLoading;

    [ObservableProperty]
    private bool _isRouteSaving;

    [ObservableProperty]
    private string _routeErrorMessage = string.Empty;

    [ObservableProperty]
    private string _newKeyName = string.Empty;

    [ObservableProperty]
    private string _createdPlainKey = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    public AccessKeysViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasCreatedKey => !string.IsNullOrWhiteSpace(CreatedPlainKey);
    public bool IsListVisible => !IsLoading && !HasError;
    public bool IsCreateFormVisible => !HasCreatedKey;
    public bool HasRouteError => !string.IsNullOrWhiteSpace(RouteErrorMessage);
    public bool HasRouteEntries => RouteEntries.Count > 0;
    public bool CanOpenEditor => !IsLoading && !IsRouteLoading && !HasRouteError;
    public bool CanCreate => !IsCreating && CanOpenEditor;
    public bool CanSaveRoutes => !IsRouteSaving && !IsRouteLoading && !HasRouteError && EditingRoutesKey is not null;
    public string RoutesEditorTitle => EditingRoutesKey is null ? "编辑路由权限" : $"编辑路由权限 - {EditingRoutesKey.KeyName}";

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var keysTask = _apiService.SendAsync<List<AccessKeyItem>>(HttpMethod.Get, "/api/admin/access-keys", null);
            await LoadRouteEntriesAsync();
            Items = new ObservableCollection<AccessKeyItem>(await keysTask);
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
    private void OpenCreate()
    {
        if (!CanOpenEditor) return;
        ErrorMessage = string.Empty;
        NewKeyName = string.Empty;
        CreatedPlainKey = string.Empty;
        EditingRoutesKey = null;
        SetSelectedRoutes(Array.Empty<string>());
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewKeyName))
        {
            ErrorMessage = "请输入密钥名称";
            return;
        }

        IsCreating = true;
        try
        {
            var result = await _apiService.SendAsync<CreateAccessKeyResult>(
                HttpMethod.Post,
                "/api/admin/access-keys/create",
                new
                {
                    keyName = NewKeyName.Trim(),
                    allowedRouteNames = SelectedRouteNames()
                });
            CreatedPlainKey = result.PlainKey;
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
    private void OpenRoutesEditor(AccessKeyItem? item)
    {
        if (item is null || !CanOpenEditor) return;
        EditingRoutesKey = item;
        SetSelectedRoutes(item.AllowedRouteNames);
        IsRoutesEditorOpen = true;
    }

    [RelayCommand]
    private void CloseRoutesEditor()
    {
        IsRoutesEditorOpen = false;
        EditingRoutesKey = null;
    }

    [RelayCommand]
    private void SelectAllRoutes()
    {
        foreach (var route in RouteEntries)
        {
            route.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelectedRoutes()
    {
        SetSelectedRoutes(Array.Empty<string>());
    }

    [RelayCommand]
    private async Task SaveRoutesAsync()
    {
        if (!CanSaveRoutes || EditingRoutesKey is null) return;
        IsRouteSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/access-keys/update-routes/{EditingRoutesKey.Id}",
                new { allowedRouteNames = SelectedRouteNames() });
            Message = "路由权限已更新";
            IsRoutesEditorOpen = false;
            EditingRoutesKey = null;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsRouteSaving = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(AccessKeyItem? item)
    {
        if (item is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/access-keys/toggle/{item.Id}", null);
            item.IsEnabled = !item.IsEnabled;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(AccessKeyItem? item)
    {
        if (item is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Post, $"/api/admin/access-keys/delete/{item.Id}", null);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task LoadRouteEntriesAsync()
    {
        IsRouteLoading = true;
        RouteErrorMessage = string.Empty;
        try
        {
            var entries = await _apiService.SendAsync<List<RouteEntry>>(
                HttpMethod.Get,
                "/api/admin/route-rules/entries",
                null);
            RouteEntries = new ObservableCollection<RouteEntry>(entries);
        }
        catch (Exception exception)
        {
            RouteEntries = new ObservableCollection<RouteEntry>();
            RouteErrorMessage = "路由入口加载失败，创建和编辑密钥权限已暂时禁用。";
            System.Diagnostics.Debug.WriteLine(exception);
        }
        finally
        {
            IsRouteLoading = false;
            NotifyRouteProperties();
        }
    }

    private List<string> SelectedRouteNames()
        => RouteEntries.Where(route => route.IsSelected).Select(route => route.EntryName).ToList();

    private void SetSelectedRoutes(IEnumerable<string> selectedRoutes)
    {
        var selected = selectedRoutes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in RouteEntries)
        {
            route.IsSelected = selected.Contains(route.EntryName);
        }
    }

    private void NotifyRouteProperties()
    {
        OnPropertyChanged(nameof(HasRouteEntries));
        OnPropertyChanged(nameof(CanOpenEditor));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanSaveRoutes));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsListVisible));
    }

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnCreatedPlainKeyChanged(string value)
    {
        OnPropertyChanged(nameof(HasCreatedKey));
        OnPropertyChanged(nameof(IsCreateFormVisible));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListVisible));
        NotifyRouteProperties();
    }

    partial void OnIsCreatingChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnIsRouteLoadingChanged(bool value) => NotifyRouteProperties();
    partial void OnIsRouteSavingChanged(bool value) => OnPropertyChanged(nameof(CanSaveRoutes));
    partial void OnRouteErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasRouteError));
        NotifyRouteProperties();
    }
    partial void OnEditingRoutesKeyChanged(AccessKeyItem? value)
    {
        OnPropertyChanged(nameof(CanSaveRoutes));
        OnPropertyChanged(nameof(RoutesEditorTitle));
    }
}
