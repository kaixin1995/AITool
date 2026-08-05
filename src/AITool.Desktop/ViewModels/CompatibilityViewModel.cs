using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class CompatibilityViewModel : ViewModelBase
{
    private readonly ApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<CompatibilityProfileItem> _items = new();

    [ObservableProperty]
    private CompatibilityProfileEditForm _form = new();

    [ObservableProperty]
    private CompatibilityProfileItem? _editingProfile;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    public CompatibilityViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public IReadOnlyList<string> OperationOptions { get; } = new[] { "strip", "rename", "default" };
    public IReadOnlyList<string> ScopeOptions { get; } = new[] { "all", "passthrough", "bridge" };
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !IsLoading && !HasItems && !HasError;
    public bool HasRules => Form.Rules.Count > 0;
    public bool HasNoRules => !HasRules;
    public bool ShowEditorPlaceholder => !IsEditorOpen;
    public bool IsEditMode => EditingProfile is not null;
    public bool CanSave => !IsSaving;
    public string EditorTitle => IsEditMode ? $"编辑规则集 - {EditingProfile!.Name}" : "新建规则集";

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var profiles = await _apiService.SendAsync<List<CompatibilityProfileItem>>(
                HttpMethod.Get,
                "/api/admin/compatibility-profiles",
                null);
            Items = new ObservableCollection<CompatibilityProfileItem>(profiles);
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
        EditingProfile = null;
        Form = new CompatibilityProfileEditForm();
        IsEditorOpen = true;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task OpenEditAsync(CompatibilityProfileItem? item)
    {
        if (item is null) return;
        ErrorMessage = string.Empty;
        try
        {
            var detail = await _apiService.SendAsync<CompatibilityProfileDetail>(
                HttpMethod.Get,
                $"/api/admin/compatibility-profiles/{item.Id}",
                null);
            EditingProfile = item;
            Form = CreateForm(detail);
            IsEditorOpen = true;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
        EditingProfile = null;
    }

    [RelayCommand]
    private void AddRule()
    {
        Form.Rules.Add(new CompatibilityRuleForm());
        NotifyRuleProperties();
    }

    [RelayCommand]
    private void RemoveRule(CompatibilityRuleForm? rule)
    {
        if (rule is null) return;
        Form.Rules.Remove(rule);
        NotifyRuleProperties();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Form.Name))
        {
            ErrorMessage = "规则集名称不能为空";
            return;
        }

        IsSaving = true;
        try
        {
            var payload = new CompatibilityProfilePayload
            {
                Name = Form.Name.Trim(),
                Description = Form.Description.Trim(),
                IsEnabled = Form.IsEnabled,
                RulesJson = JsonSerializer.Serialize(Form.Rules)
            };
            if (EditingProfile is null)
            {
                await _apiService.SendAsync<object>(
                    HttpMethod.Post,
                    "/api/admin/compatibility-profiles",
                    payload);
                Message = "规则集已创建";
            }
            else
            {
                await _apiService.SendAsync<object>(
                    HttpMethod.Put,
                    $"/api/admin/compatibility-profiles/{EditingProfile.Id}",
                    payload);
                Message = "规则集已更新";
            }

            IsEditorOpen = false;
            EditingProfile = null;
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
    private async Task ToggleAsync(CompatibilityProfileItem? item)
    {
        if (item is null) return;
        try
        {
            var result = await _apiService.SendAsync<CompatibilityToggleResult>(
                HttpMethod.Post,
                $"/api/admin/compatibility-profiles/{item.Id}/toggle",
                null);
            item.IsEnabled = result.IsEnabled;
            Message = item.IsEnabled ? "规则集已启用" : "规则集已停用";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(CompatibilityProfileItem? item)
    {
        if (item is null) return;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Delete,
                $"/api/admin/compatibility-profiles/{item.Id}",
                null);
            if (ReferenceEquals(EditingProfile, item))
            {
                IsEditorOpen = false;
                EditingProfile = null;
            }
            Message = "规则集已删除，引用它的模型将不再应用该规则集";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private static CompatibilityProfileEditForm CreateForm(CompatibilityProfileDetail detail)
    {
        var form = new CompatibilityProfileEditForm
        {
            Name = detail.Name,
            Description = detail.Description,
            IsEnabled = detail.IsEnabled
        };
        try
        {
            var rules = JsonSerializer.Deserialize<List<CompatibilityRuleForm>>(detail.RulesJson) ?? new();
            foreach (var rule in rules) form.Rules.Add(rule);
        }
        catch (JsonException)
        {
            // 服务端会校验规则 JSON；异常数据按空规则显示，避免编辑页面无法打开。
        }

        return form;
    }

    partial void OnItemsChanged(ObservableCollection<CompatibilityProfileItem> value)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoItems));
    partial void OnIsEditorOpenChanged(bool value) => OnPropertyChanged(nameof(ShowEditorPlaceholder));
    partial void OnFormChanged(CompatibilityProfileEditForm value) => NotifyRuleProperties();
    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasNoItems));
    }
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnEditingProfileChanged(CompatibilityProfileItem? value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(EditorTitle));
    }
    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    private void NotifyRuleProperties()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
    }

    private sealed class CompatibilityToggleResult
    {
        public bool IsEnabled { get; set; }
    }
}
