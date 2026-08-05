using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class ModelsViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<ModelVendorGroup> _vendorGroups = new();

    [ObservableProperty]
    private ObservableCollection<ModelVendorGroup> _filteredVendorGroups = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private ModelEditForm _form = new();

    [ObservableProperty]
    private ObservableCollection<CompatibilityProfileItem> _profileOptions = new();

    private List<CompatibilityProfileItem> _allProfileOptions = new();

    [ObservableProperty]
    private CompatibilityProfileItem? _selectedCompatibilityProfile;

    [ObservableProperty]
    private ModelListItem? _editingModel;

    [ObservableProperty]
    private ModelListItem? _mappingModel;

    [ObservableProperty]
    private ModelDetail? _mappingDetail;

    [ObservableProperty]
    private ModelAvailableSite? _selectedMappingSite;

    [ObservableProperty]
    private string _newMappingRemoteName = string.Empty;

    [ObservableProperty]
    private bool _newMappingEnabled = true;

    [ObservableProperty]
    private bool _isMappingOpen;

    [ObservableProperty]
    private bool _isMappingLoading;

    [ObservableProperty]
    private bool _isMappingSaving;

    [ObservableProperty]
    private string _mappingErrorMessage = string.Empty;

    [ObservableProperty]
    private string _mappingMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ModelsViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool IsEditMode => EditingModel is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsListVisible => !IsLoading && !HasError;
    public bool HasModels => ModelCount > 0;
    public bool HasFilteredModels => FilteredModelCount > 0;
    public bool ShowNoModels => !IsLoading && !HasError && !HasModels;
    public bool ShowNoSearchResults => !IsLoading && !HasError && HasModels && !HasFilteredModels;
    public bool CanSave => !IsSaving;
    public bool CanClearAll => !IsLoading && !IsSaving && !IsMappingSaving;
    public string EditorTitle => IsEditMode ? "编辑模型" : "新增模型";
    public int ModelCount => VendorGroups.Sum(group => group.Models.Count);
    public int FilteredModelCount => FilteredVendorGroups.Sum(group => group.Models.Count);
    public string MappingTitle => MappingModel is null ? "站点映射" : $"站点映射 - {MappingModel.DisplayName}";
    public bool HasMappingError => !string.IsNullOrWhiteSpace(MappingErrorMessage);
    public bool HasMappingMessage => !string.IsNullOrWhiteSpace(MappingMessage);
    public bool HasMappingRows => MappingDetail?.SiteMappings.Count > 0;
    public bool HasNoMappingRows => !HasMappingRows;
    public bool ShowNoMappingRows => !IsMappingLoading && !HasMappingError && HasNoMappingRows;
    public bool HasAvailableSites => MappingDetail?.AvailableSites.Count > 0;
    public bool ShowNoAvailableSites => !IsMappingLoading && !HasMappingError && MappingDetail is not null && !HasAvailableSites;
    public string NoAvailableSitesMessage => MappingDetail?.SiteMappings.Count > 0 ? "所有启用站点都已关联" : "没有启用站点";
    public bool CanOpenMapping => !IsLoading && !IsMappingLoading;
    public bool CanEditMapping => !IsMappingLoading && !IsMappingSaving && MappingDetail is not null;
    public bool CanAddMapping => !IsMappingLoading
        && !IsMappingSaving
        && MappingDetail is not null
        && HasAvailableSites
        && SelectedMappingSite is not null
        && !string.IsNullOrWhiteSpace(NewMappingRemoteName);
    public bool CanSaveMapping => !IsMappingLoading && !IsMappingSaving && MappingDetail is not null;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<ModelListResponse>(HttpMethod.Get, "/api/admin/models", null);
            VendorGroups = new ObservableCollection<ModelVendorGroup>(result.VendorGroups);
            UpdateFilteredVendorGroups();
            await LoadCompatibilityProfilesAsync();
            OnPropertyChanged(nameof(ModelCount));
            OnPropertyChanged(nameof(HasModels));
            OnPropertyChanged(nameof(CanClearAll));
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
        EditingModel = null;
        Form.Reset();
        ProfileOptions = new ObservableCollection<CompatibilityProfileItem>(_allProfileOptions.Where(profile => profile.IsEnabled));
        SelectedCompatibilityProfile = null;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void OpenEdit(ModelListItem? model)
    {
        if (model is null) return;
        EditingModel = model;
        Form = new ModelEditForm
        {
            ModelName = model.ModelName,
            DisplayName = model.DisplayName,
            IsEnabled = model.IsEnabled,
            OverrideReasoningEffort = model.OverrideReasoningEffort,
            CompatibilityProfileId = model.CompatibilityProfileId
        };

        // 编辑时保留当前停用规则集，避免仅因下拉选项过滤而丢失关联。
        ProfileOptions = new ObservableCollection<CompatibilityProfileItem>(_allProfileOptions.Where(profile => profile.IsEnabled));
        var currentProfile = _allProfileOptions.FirstOrDefault(profile =>
            string.Equals(profile.Id, model.CompatibilityProfileId, StringComparison.OrdinalIgnoreCase));
        if (currentProfile is not null && !currentProfile.IsEnabled &&
            !ProfileOptions.Any(profile => string.Equals(profile.Id, currentProfile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            ProfileOptions = new ObservableCollection<CompatibilityProfileItem>(
                ProfileOptions.Append(currentProfile));
        }

        SelectedCompatibilityProfile = currentProfile ?? ProfileOptions.FirstOrDefault(profile =>
            string.Equals(profile.Id, model.CompatibilityProfileId, StringComparison.OrdinalIgnoreCase));
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
    }

    private async Task LoadCompatibilityProfilesAsync()
    {
        try
        {
            var profiles = await _apiService.SendAsync<List<CompatibilityProfileItem>>(
                HttpMethod.Get,
                "/api/admin/compatibility-profiles",
                null);
            _allProfileOptions = profiles;
            ProfileOptions = new ObservableCollection<CompatibilityProfileItem>(profiles.Where(profile => profile.IsEnabled));
        }
        catch (Exception exception)
        {
            ProfileOptions = new ObservableCollection<CompatibilityProfileItem>();
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    [RelayCommand]
    private async Task OpenMappingAsync(ModelListItem? model)
    {
        if (model is null || !CanOpenMapping) return;

        MappingModel = model;
        MappingDetail = null;
        SelectedMappingSite = null;
        NewMappingRemoteName = model.ModelName;
        NewMappingEnabled = true;
        MappingErrorMessage = string.Empty;
        MappingMessage = string.Empty;
        IsMappingOpen = true;
        await LoadMappingDetailAsync();
    }

    [RelayCommand]
    private void CloseMapping()
    {
        IsMappingOpen = false;
        MappingModel = null;
        MappingDetail = null;
        SelectedMappingSite = null;
        MappingErrorMessage = string.Empty;
        MappingMessage = string.Empty;
    }

    [RelayCommand]
    private async Task AddMappingAsync()
    {
        if (!CanAddMapping || MappingModel is null || SelectedMappingSite is null) return;
        if (!Guid.TryParse(SelectedMappingSite.Id, out var siteId))
        {
            MappingErrorMessage = "站点标识无效，请刷新后重试。";
            return;
        }

        IsMappingSaving = true;
        MappingErrorMessage = string.Empty;
        MappingMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                $"/api/admin/models/{MappingModel.Id}/mappings",
                new
                {
                    siteId,
                    remoteModelName = NewMappingRemoteName.Trim(),
                    isEnabled = NewMappingEnabled
                });

            MappingMessage = "站点映射已添加";
            SelectedMappingSite = null;
            NewMappingRemoteName = MappingModel.ModelName;
            NewMappingEnabled = true;
            await LoadMappingDetailAsync();
            await LoadAsync();
        }
        catch (Exception exception)
        {
            MappingErrorMessage = exception.Message;
        }
        finally
        {
            IsMappingSaving = false;
        }
    }

    [RelayCommand]
    private async Task UpdateMappingConcurrencyAsync(ModelSiteMapping? mapping)
    {
        if (mapping is null || !CanSaveMapping) return;
        IsMappingSaving = true;
        MappingErrorMessage = string.Empty;
        MappingMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<ConcurrencyResult>(
                HttpMethod.Put,
                $"/api/admin/models/mappings/{mapping.MappingId}/concurrency",
                new { maxConcurrency = Math.Max(0, mapping.MaxConcurrency) });
            mapping.MaxConcurrency = result.MaxConcurrency;
            MappingMessage = "最大并发已更新";
        }
        catch (Exception exception)
        {
            MappingErrorMessage = exception.Message;
        }
        finally
        {
            IsMappingSaving = false;
        }
    }

    [RelayCommand]
    private async Task DeleteMappingAsync(ModelSiteMapping? mapping)
    {
        if (mapping is null || MappingModel is null || !CanSaveMapping) return;
        IsMappingSaving = true;
        MappingErrorMessage = string.Empty;
        MappingMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Delete,
                $"/api/admin/models/{MappingModel.Id}/mappings/{mapping.MappingId}",
                null);
            MappingMessage = "站点映射已删除，相关路由规则已同步清理";
            await LoadMappingDetailAsync();
            await LoadAsync();
        }
        catch (Exception exception)
        {
            MappingErrorMessage = exception.Message;
        }
        finally
        {
            IsMappingSaving = false;
        }
    }

    private async Task LoadMappingDetailAsync()
    {
        if (MappingModel is null) return;
        IsMappingLoading = true;
        MappingErrorMessage = string.Empty;
        try
        {
            MappingDetail = await _apiService.SendAsync<ModelDetail>(
                HttpMethod.Get,
                $"/api/admin/models/{MappingModel.Id}",
                null);
        }
        catch (Exception exception)
        {
            MappingDetail = null;
            MappingErrorMessage = exception.Message;
        }
        finally
        {
            IsMappingLoading = false;
            NotifyMappingProperties();
        }
    }

    [RelayCommand]
    private void ClearCompatibilityProfile()
    {
        // 只有用户明确点击清除时才移除模型当前关联的规则集。
        Form.CompatibilityProfileId = null;
        SelectedCompatibilityProfile = null;
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        if (!CanClearAll) return;

        IsLoading = true;
        ErrorMessage = string.Empty;
        Message = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<ClearAllResult>(
                HttpMethod.Post,
                "/api/admin/models/clear-all",
                null);
            Message = $"已清空 {result.DeletedModels} 个模型、{result.DeletedMappings} 个站点映射和 {result.DeletedMonitors} 条健康监控。";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanClearAll));
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Form.ModelName))
        {
            ErrorMessage = "模型名称不能为空";
            return;
        }

        IsSaving = true;
        try
        {
            var payload = new ModelPayload
            {
                ModelName = Form.ModelName.Trim(),
                DisplayName = Form.DisplayName.Trim(),
                IsEnabled = Form.IsEnabled,
                OverrideReasoningEffort = Form.OverrideReasoningEffort.Trim(),
                CompatibilityProfileId = string.IsNullOrWhiteSpace(Form.CompatibilityProfileId)
                    ? null
                    : Form.CompatibilityProfileId
            };
            if (EditingModel is null)
            {
                await _apiService.SendAsync<object>(HttpMethod.Post, "/api/admin/models", payload);
            }
            else
            {
                await _apiService.SendAsync<object>(HttpMethod.Put, $"/api/admin/models/{EditingModel.Id}", payload);
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
    private async Task ToggleAsync(ModelListItem? model)
    {
        if (model is null) return;
        try
        {
            var result = await _apiService.SendAsync<ToggleResult>(HttpMethod.Post, $"/api/admin/models/{model.Id}/toggle", null);
            model.IsEnabled = result.IsEnabled;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ModelListItem? model)
    {
        if (model is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Delete, $"/api/admin/models/{model.Id}", null);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    partial void OnVendorGroupsChanged(ObservableCollection<ModelVendorGroup> value)
    {
        UpdateFilteredVendorGroups();
        OnPropertyChanged(nameof(ModelCount));
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(CanClearAll));
    }

    partial void OnSearchTextChanged(string value)
    {
        UpdateFilteredVendorGroups();
    }

    partial void OnFilteredVendorGroupsChanged(ObservableCollection<ModelVendorGroup> value)
    {
        OnPropertyChanged(nameof(FilteredModelCount));
        OnPropertyChanged(nameof(HasFilteredModels));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    partial void OnEditingModelChanged(ModelListItem? value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(EditorTitle));
    }

    partial void OnMappingModelChanged(ModelListItem? value)
    {
        OnPropertyChanged(nameof(MappingTitle));
        OnPropertyChanged(nameof(CanAddMapping));
    }

    partial void OnSelectedCompatibilityProfileChanged(CompatibilityProfileItem? value)
    {
        // 下拉选项刷新或编辑回填为空时，不应意外清空已有规则集关联。
        if (value is not null)
        {
            Form.CompatibilityProfileId = value.Id;
        }
    }

    partial void OnMappingDetailChanged(ModelDetail? value)
    {
        OnPropertyChanged(nameof(HasMappingRows));
        OnPropertyChanged(nameof(HasNoMappingRows));
        OnPropertyChanged(nameof(ShowNoMappingRows));
        OnPropertyChanged(nameof(HasAvailableSites));
        OnPropertyChanged(nameof(ShowNoAvailableSites));
        OnPropertyChanged(nameof(NoAvailableSitesMessage));
        OnPropertyChanged(nameof(CanEditMapping));
        OnPropertyChanged(nameof(CanAddMapping));
        OnPropertyChanged(nameof(CanSaveMapping));
    }

    partial void OnSelectedMappingSiteChanged(ModelAvailableSite? value) => OnPropertyChanged(nameof(CanAddMapping));
    partial void OnNewMappingRemoteNameChanged(string value) => OnPropertyChanged(nameof(CanAddMapping));
    partial void OnIsMappingLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoMappingRows));
        OnPropertyChanged(nameof(ShowNoAvailableSites));
        OnPropertyChanged(nameof(CanOpenMapping));
        OnPropertyChanged(nameof(CanEditMapping));
        OnPropertyChanged(nameof(CanAddMapping));
        OnPropertyChanged(nameof(CanSaveMapping));
    }
    partial void OnIsMappingSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAddMapping));
        OnPropertyChanged(nameof(CanEditMapping));
        OnPropertyChanged(nameof(CanSaveMapping));
        OnPropertyChanged(nameof(CanClearAll));
    }
    partial void OnMappingErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasMappingError));
        OnPropertyChanged(nameof(ShowNoAvailableSites));
    }
    partial void OnMappingMessageChanged(string value) => OnPropertyChanged(nameof(HasMappingMessage));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListVisible));
        OnPropertyChanged(nameof(ShowNoModels));
        OnPropertyChanged(nameof(ShowNoSearchResults));
        OnPropertyChanged(nameof(CanOpenMapping));
        OnPropertyChanged(nameof(CanClearAll));
    }

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanClearAll));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsListVisible));
        OnPropertyChanged(nameof(ShowNoModels));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    private void UpdateFilteredVendorGroups()
    {
        var query = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            FilteredVendorGroups = VendorGroups;
            return;
        }

        var filteredGroups = VendorGroups
            .Select(group => new ModelVendorGroup
            {
                VendorName = group.VendorName,
                IconSvgBody = group.IconSvgBody,
                HeaderBackground = group.HeaderBackground,
                Models = new ObservableCollection<ModelListItem>(group.Models.Where(model =>
                    group.VendorName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    model.ModelName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            })
            .Where(group => group.Models.Count > 0)
            .ToList();

        FilteredVendorGroups = new ObservableCollection<ModelVendorGroup>(filteredGroups);
    }

    private void NotifyMappingProperties()
    {
        OnPropertyChanged(nameof(HasMappingRows));
        OnPropertyChanged(nameof(HasAvailableSites));
        OnPropertyChanged(nameof(CanOpenMapping));
        OnPropertyChanged(nameof(CanEditMapping));
        OnPropertyChanged(nameof(CanAddMapping));
        OnPropertyChanged(nameof(CanSaveMapping));
    }

    private sealed class ToggleResult
    {
        public bool IsEnabled { get; set; }
    }

    private sealed class ClearAllResult
    {
        public int DeletedModels { get; set; }
        public int DeletedMappings { get; set; }
        public int DeletedMonitors { get; set; }
    }

    private sealed class ConcurrencyResult
    {
        public int MaxConcurrency { get; set; }
    }
}
