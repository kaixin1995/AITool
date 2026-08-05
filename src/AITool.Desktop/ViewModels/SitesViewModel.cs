using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class SitesViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<SiteListItem> _sites = new();

    [ObservableProperty]
    private SiteEditForm _form = new();

    [ObservableProperty]
    private SiteListItem? _editingSite;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public SitesViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool IsEditMode => EditingSite is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsListVisible => !IsLoading && !HasError;
    public bool HasSites => Sites.Count > 0;
    public bool NoSites => !HasSites;
    public bool CanSave => !IsSaving;
    public string EditorTitle => IsEditMode ? "编辑站点" : "新增站点";

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var items = await _apiService.SendAsync<List<SiteListItem>>(HttpMethod.Get, "/api/admin/sites", null);
            Sites = new ObservableCollection<SiteListItem>(items);
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
    private Task RefreshAsync()
    {
        return LoadAsync();
    }

    [RelayCommand]
    private void OpenCreate()
    {
        EditingSite = null;
        Form.Reset();
        IsEditorOpen = true;
    }

    [RelayCommand]
    private async Task OpenEditAsync(SiteListItem? site)
    {
        if (site is null) return;

        IsLoading = true;
        try
        {
            var detail = await _apiService.SendAsync<SiteDetail>(HttpMethod.Get, $"/api/admin/sites/{site.Id}", null);
            EditingSite = site;
            Form = new SiteEditForm
            {
                Name = detail.Name,
                BaseUrl = detail.BaseUrl,
                EndpointPathMode = detail.EndpointPathMode,
                SupportsOpenAi = detail.SupportsOpenAi,
                SupportsAnthropic = detail.SupportsAnthropic,
                IsEnabled = detail.IsEnabled
            };
            IsEditorOpen = true;
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
    private void CloseEditor()
    {
        IsEditorOpen = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Form.Name) || string.IsNullOrWhiteSpace(Form.BaseUrl))
        {
            ErrorMessage = "站点名称和地址不能为空";
            return;
        }

        if (!IsEditMode && string.IsNullOrWhiteSpace(Form.ApiKey))
        {
            ErrorMessage = "新建站点必须填写 API 密钥";
            return;
        }

        IsSaving = true;
        try
        {
            var payload = new SitePayload
            {
                Name = Form.Name.Trim(),
                BaseUrl = Form.BaseUrl.Trim(),
                EndpointPathMode = Form.EndpointPathMode,
                ApiKey = Form.ApiKey,
                SupportsOpenAi = Form.SupportsOpenAi,
                SupportsAnthropic = Form.SupportsAnthropic,
                IsEnabled = Form.IsEnabled
            };
            if (EditingSite is null)
            {
                await _apiService.SendAsync<object>(HttpMethod.Post, "/api/admin/sites", payload);
            }
            else
            {
                await _apiService.SendAsync<object>(HttpMethod.Put, $"/api/admin/sites/{EditingSite.Id}", payload);
            }

            IsEditorOpen = false;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(SiteListItem? site)
    {
        if (site is null) return;
        try
        {
            var result = await _apiService.SendAsync<ToggleResult>(HttpMethod.Post, $"/api/admin/sites/{site.Id}/toggle", null);
            site.IsEnabled = result.IsEnabled;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SiteListItem? site)
    {
        if (site is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Delete, $"/api/admin/sites/{site.Id}", null);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    partial void OnEditingSiteChanged(SiteListItem? value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(EditorTitle));
    }

    partial void OnSitesChanged(ObservableCollection<SiteListItem> value)
    {
        OnPropertyChanged(nameof(HasSites));
        OnPropertyChanged(nameof(NoSites));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListVisible));
    }

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsListVisible));
    }

    private sealed class ToggleResult
    {
        public bool IsEnabled { get; set; }
    }
}
