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
    private ModelEditForm _form = new();

    [ObservableProperty]
    private ModelListItem? _editingModel;

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
    public bool IsListVisible => !IsLoading;
    public bool HasModels => ModelCount > 0;
    public bool CanSave => !IsSaving;
    public string EditorTitle => IsEditMode ? "编辑模型" : "新增模型";
    public int ModelCount => VendorGroups.Sum(group => group.Models.Count);

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<ModelListResponse>(HttpMethod.Get, "/api/admin/models", null);
            VendorGroups = new ObservableCollection<ModelVendorGroup>(result.VendorGroups);
            OnPropertyChanged(nameof(ModelCount));
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
            OverrideReasoningEffort = model.OverrideReasoningEffort
        };
        IsEditorOpen = true;
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
                OverrideReasoningEffort = Form.OverrideReasoningEffort.Trim()
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
        OnPropertyChanged(nameof(ModelCount));
        OnPropertyChanged(nameof(HasModels));
    }

    partial void OnEditingModelChanged(ModelListItem? value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(EditorTitle));
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
    }

    private sealed class ToggleResult
    {
        public bool IsEnabled { get; set; }
    }
}
