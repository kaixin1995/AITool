using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

/// <summary>
/// 配置方案管理页：请求头模板（HeaderProfile）与网络代理池（ProxyProfile）的增删改查与启停。
/// 与网页端「调试工具 → 请求头模板库 / 网络代理池」两个页签对应。
/// </summary>
public partial class ProfilesViewModel : ViewModelBase
{
    private const string HeaderPath = "/api/admin/developer/header-profiles";
    private const string ProxyPath = "/api/admin/developer/proxy-profiles";

    private readonly ApiService _apiService;

    [ObservableProperty] private ObservableCollection<HeaderProfileItem> _headerProfiles = new();
    [ObservableProperty] private ObservableCollection<ProxyProfileItemUi> _proxyProfiles = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _selectedTab = "headers";

    // —— Header 编辑 ——
    [ObservableProperty] private HeaderProfileItem? _editingHeader;
    [ObservableProperty] private bool _isHeaderEditorOpen;
    [ObservableProperty] private string _headerKey = string.Empty;
    [ObservableProperty] private string _headerName = string.Empty;
    [ObservableProperty] private string _headerDescription = string.Empty;
    [ObservableProperty] private string _headerHeadersJson = string.Empty;
    [ObservableProperty] private bool _headerEnabled = true;

    // —— Proxy 编辑 ——
    [ObservableProperty] private ProxyProfileItemUi? _editingProxy;
    [ObservableProperty] private bool _isProxyEditorOpen;
    [ObservableProperty] private string _proxyKey = string.Empty;
    [ObservableProperty] private string _proxyName = string.Empty;
    [ObservableProperty] private string _proxyUrl = string.Empty;
    [ObservableProperty] private string _proxyDescription = string.Empty;
    [ObservableProperty] private bool _proxyEnabled = true;

    public ProfilesViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public bool IsHeadersTab => SelectedTab == "headers";
    public bool IsProxiesTab => SelectedTab == "proxies";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasHeaderProfiles => HeaderProfiles.Count > 0;
    public bool HasProxyProfiles => ProxyProfiles.Count > 0;
    public bool IsNewHeader => EditingHeader is null;
    public bool IsNewProxy => EditingProxy is null;
    public string HeaderEditorTitle => IsNewHeader ? "新建请求头模板" : "编辑请求头模板";
    public string ProxyEditorTitle => IsNewProxy ? "新建代理方案" : "编辑代理方案";
    public bool CanSaveHeader => !IsSaving
        && !string.IsNullOrWhiteSpace(HeaderKey)
        && !string.IsNullOrWhiteSpace(HeaderName);
    public bool CanSaveProxy => !IsSaving
        && !string.IsNullOrWhiteSpace(ProxyKey)
        && !string.IsNullOrWhiteSpace(ProxyName)
        && !string.IsNullOrWhiteSpace(ProxyUrl);

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsHeadersTab));
        OnPropertyChanged(nameof(IsProxiesTab));
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var headerTask = _apiService.SendAsync<List<HeaderProfileItem>>(HttpMethod.Get, HeaderPath, null);
            var proxyTask = _apiService.SendAsync<List<ProxyProfileItemUi>>(HttpMethod.Get, ProxyPath, null);
            await Task.WhenAll(headerTask, proxyTask);

            HeaderProfiles = new ObservableCollection<HeaderProfileItem>(headerTask.Result ?? []);
            ProxyProfiles = new ObservableCollection<ProxyProfileItemUi>(proxyTask.Result ?? []);
            OnPropertyChanged(nameof(HasHeaderProfiles));
            OnPropertyChanged(nameof(HasProxyProfiles));
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
    private void SelectTab(string? tab)
    {
        if (string.IsNullOrWhiteSpace(tab)) return;
        SelectedTab = tab;
    }

    // ============ Header 模板 ============

    [RelayCommand]
    private void OpenHeaderEditor(HeaderProfileItem? item)
    {
        EditingHeader = item;
        HeaderKey = item?.Key ?? string.Empty;
        HeaderName = item?.Name ?? string.Empty;
        HeaderDescription = item?.Description ?? string.Empty;
        HeaderHeadersJson = item?.HeadersJson ?? string.Empty;
        HeaderEnabled = item?.IsEnabled ?? true;
        IsHeaderEditorOpen = true;
        OnPropertyChanged(nameof(IsNewHeader));
        OnPropertyChanged(nameof(HeaderEditorTitle));
        OnPropertyChanged(nameof(CanSaveHeader));
    }

    [RelayCommand]
    private void CloseHeaderEditor()
    {
        IsHeaderEditorOpen = false;
        EditingHeader = null;
    }

    [RelayCommand]
    private async Task SaveHeaderAsync()
    {
        if (!CanSaveHeader) return;

        var headersJson = HeaderHeadersJson.Trim();
        if (!string.IsNullOrWhiteSpace(headersJson))
        {
            try
            {
                using var document = JsonDocument.Parse(headersJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    ErrorMessage = "请求头模板必须是 JSON 对象，例如 {\"User-Agent\": \"...\"}";
                    return;
                }
            }
            catch (JsonException)
            {
                ErrorMessage = "请求头模板 JSON 格式无效，请检查";
                return;
            }
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var payload = new HeaderProfilePayload
            {
                Key = HeaderKey.Trim(),
                Name = HeaderName.Trim(),
                Description = string.IsNullOrWhiteSpace(HeaderDescription) ? null : HeaderDescription.Trim(),
                HeadersJson = string.IsNullOrWhiteSpace(headersJson) ? null : headersJson,
                IsEnabled = HeaderEnabled,
                SortOrder = EditingHeader?.SortOrder ?? 100
            };

            // 内置方案 Key 不可改：编辑时保持原 Key，避免后端 400。
            if (EditingHeader is { IsBuiltIn: true })
            {
                payload.Key = EditingHeader.Key;
            }

            if (EditingHeader is null)
            {
                await _apiService.SendAsync<object>(HttpMethod.Post, HeaderPath, payload);
            }
            else
            {
                await _apiService.SendAsync<object>(HttpMethod.Put, $"{HeaderPath}/{EditingHeader.Id}", payload);
            }

            Message = $"请求头模板“{payload.Name}”已保存";
            CloseHeaderEditor();
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(CanSaveHeader));
        }
    }

    [RelayCommand]
    private async Task ToggleHeaderAsync(HeaderProfileItem? item)
    {
        if (item is null) return;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Put,
                $"{HeaderPath}/{item.Id}",
                new HeaderProfilePayload
                {
                    Key = item.Key,
                    Name = item.Name,
                    Description = item.Description,
                    HeadersJson = item.HeadersJson,
                    IsEnabled = !item.IsEnabled,
                    SortOrder = item.SortOrder
                });
            item.IsEnabled = !item.IsEnabled;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteHeaderAsync(HeaderProfileItem? item)
    {
        if (item is null || item.IsBuiltIn) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Delete, $"{HeaderPath}/{item.Id}", null);
            Message = $"请求头模板“{item.Name}”已删除";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    // ============ Proxy 代理池 ============

    [RelayCommand]
    private void OpenProxyEditor(ProxyProfileItemUi? item)
    {
        EditingProxy = item;
        ProxyKey = item?.Key ?? string.Empty;
        ProxyName = item?.Name ?? string.Empty;
        ProxyUrl = item?.ProxyUrl ?? string.Empty;
        ProxyDescription = item?.Description ?? string.Empty;
        ProxyEnabled = item?.IsEnabled ?? true;
        IsProxyEditorOpen = true;
        OnPropertyChanged(nameof(IsNewProxy));
        OnPropertyChanged(nameof(ProxyEditorTitle));
        OnPropertyChanged(nameof(CanSaveProxy));
    }

    [RelayCommand]
    private void CloseProxyEditor()
    {
        IsProxyEditorOpen = false;
        EditingProxy = null;
    }

    [RelayCommand]
    private async Task SaveProxyAsync()
    {
        if (!CanSaveProxy) return;

        var proxyUrl = ProxyUrl.Trim();
        if (!proxyUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !proxyUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !proxyUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            && !proxyUrl.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "代理地址必须以 http://、https:// 或 socks5:// 开头";
            return;
        }

        IsSaving = true;
        ErrorMessage = string.Empty;
        try
        {
            var payload = new ProxyProfilePayload
            {
                Key = ProxyKey.Trim(),
                Name = ProxyName.Trim(),
                ProxyUrl = proxyUrl,
                Description = string.IsNullOrWhiteSpace(ProxyDescription) ? null : ProxyDescription.Trim(),
                IsEnabled = ProxyEnabled,
                SortOrder = EditingProxy?.SortOrder ?? 100
            };

            if (EditingProxy is null)
            {
                await _apiService.SendAsync<object>(HttpMethod.Post, ProxyPath, payload);
            }
            else
            {
                await _apiService.SendAsync<object>(HttpMethod.Put, $"{ProxyPath}/{EditingProxy.Id}", payload);
            }

            Message = $"代理方案“{payload.Name}”已保存";
            CloseProxyEditor();
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(CanSaveProxy));
        }
    }

    [RelayCommand]
    private async Task ToggleProxyAsync(ProxyProfileItemUi? item)
    {
        if (item is null) return;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Put,
                $"{ProxyPath}/{item.Id}",
                new ProxyProfilePayload
                {
                    Key = item.Key,
                    Name = item.Name,
                    ProxyUrl = item.ProxyUrl,
                    Description = item.Description,
                    IsEnabled = !item.IsEnabled,
                    SortOrder = item.SortOrder
                });
            item.IsEnabled = !item.IsEnabled;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteProxyAsync(ProxyProfileItemUi? item)
    {
        if (item is null) return;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Delete, $"{ProxyPath}/{item.Id}", null);
            Message = $"代理方案“{item.Name}”已删除";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }
}
