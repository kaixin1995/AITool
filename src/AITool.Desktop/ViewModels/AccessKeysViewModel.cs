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

    public AccessKeysViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasCreatedKey => !string.IsNullOrWhiteSpace(CreatedPlainKey);
    public bool IsListVisible => !IsLoading && !HasError;
    public bool IsCreateFormVisible => !HasCreatedKey;
    public bool CanCreate => !IsCreating;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var keys = await _apiService.SendAsync<List<AccessKeyItem>>(HttpMethod.Get, "/api/admin/access-keys", null);
            Items = new ObservableCollection<AccessKeyItem>(keys);
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
        ErrorMessage = string.Empty;
        NewKeyName = string.Empty;
        CreatedPlainKey = string.Empty;
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
                new { keyName = NewKeyName.Trim(), allowedRouteNames = Array.Empty<string>() });
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

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsListVisible));
    }

    partial void OnCreatedPlainKeyChanged(string value)
    {
        OnPropertyChanged(nameof(HasCreatedKey));
        OnPropertyChanged(nameof(IsCreateFormVisible));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsListVisible));
    partial void OnIsCreatingChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
}
