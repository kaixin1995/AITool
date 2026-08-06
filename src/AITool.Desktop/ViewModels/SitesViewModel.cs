using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITool.Desktop.Models;
using AITool.Desktop.Services;

namespace AITool.Desktop.ViewModels;

public partial class SitesViewModel : ViewModelBase, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly ApiService _apiService;
    private CancellationTokenSource? _catalogCancellation;
    private readonly HashSet<SiteListItem> _siteSelectionSubscriptions = new();
    private readonly HashSet<SiteCatalogModelItem> _catalogSelectionSubscriptions = new();
    private readonly HashSet<SiteImportPreviewItem> _importPreviewSubscriptions = new();
    private readonly HashSet<SiteExportItem> _exportPreviewSubscriptions = new();

    [ObservableProperty]
    private ObservableCollection<SiteListItem> _sites = new();

    [ObservableProperty]
    private int _page = 1;

    public const int PageSize = 20;

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
    private bool _isEditorLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _editorErrorMessage = string.Empty;

    [ObservableProperty]
    private string _operationErrorMessage = string.Empty;

    [ObservableProperty]
    private string _operationMessage = string.Empty;

    [ObservableProperty]
    private bool _isImportPreviewOpen;

    [ObservableProperty]
    private ObservableCollection<SiteImportPreviewItem> _importPreviewItems = new();

    [ObservableProperty]
    private string _importJsonText = string.Empty;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private bool _isExportPreviewOpen;

    [ObservableProperty]
    private ObservableCollection<SiteExportItem> _exportPreviewItems = new();

    [ObservableProperty]
    private bool _isExportLoading;

    [ObservableProperty]
    private ObservableCollection<SiteCatalogSiteItem> _catalogSites = new();

    [ObservableProperty]
    private bool _catalogVisible;

    [ObservableProperty]
    private bool _catalogLoading;

    [ObservableProperty]
    private bool _catalogImporting;

    [ObservableProperty]
    private string _catalogErrorMessage = string.Empty;

    [ObservableProperty]
    private string _catalogSearch = string.Empty;

    [ObservableProperty]
    private string _catalogTaskId = string.Empty;

    [ObservableProperty]
    private int _catalogTotalSites;

    [ObservableProperty]
    private int _catalogCompletedSites;

    public SitesViewModel(ApiService apiService)
    {
        _apiService = apiService;
    }

    public IReadOnlyList<EndpointPathModeOption> EndpointPathModeOptions { get; } =
    [
        new("standard-root", "标准根地址（自动补 /v1）"),
        new("versioned-base", "已含版本路径（直接追加）")
    ];

    public bool IsEditMode => EditingSite is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasEditorError => !string.IsNullOrWhiteSpace(EditorErrorMessage);
    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationErrorMessage);
    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);
    public bool HasCatalogError => !string.IsNullOrWhiteSpace(CatalogErrorMessage);
    public bool IsListVisible => !IsLoading && (Sites.Count > 0 || !HasError);
    public bool HasSites => Sites.Count > 0;
    public bool NoSites => !HasSites;
    public IEnumerable<SiteListItem> PagedSites => Sites.Skip((Page - 1) * PageSize).Take(PageSize);
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Sites.Count / (double)PageSize));
    public string PageText => $"第 {Page} / {TotalPages} 页";
    public bool CanPreviousPage => Page > 1 && !IsLoading;
    public bool CanNextPage => Page < TotalPages && !IsLoading;
    public bool CanSave => !IsSaving && !IsEditorLoading;
    public string EditorTitle => IsEditMode ? "编辑站点" : "新增站点";
    public int SelectedSiteCount => Sites.Count(site => site.IsSelected);
    public bool HasSelectedSites => SelectedSiteCount > 0;
    public bool CanBulkDelete => HasSelectedSites && !IsLoading;
    public bool CanFetchModels => !CatalogLoading;
    public bool HasCatalogSites => CatalogSites.Count > 0;
    public bool NoCatalogSites => !HasCatalogSites;
    public IEnumerable<SiteCatalogSiteItem> FilteredCatalogSites
    {
        get
        {
            var keyword = CatalogSearch.Trim();
            if (string.IsNullOrWhiteSpace(keyword)) return CatalogSites;

            return CatalogSites.Where(site =>
                site.SiteName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || site.Models.Any(model =>
                    model.RemoteModelName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || model.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public int CatalogModelCount => CatalogSites.Sum(site => site.Models.Count);
    public int SelectedCatalogCount => CatalogSites.Sum(site => site.Models.Count(model => model.IsSelected));
    public bool HasCatalogModels => CatalogModelCount > 0;
    public bool HasCatalogProgress => CatalogTotalSites > 0;
    public bool CanImportCatalog => SelectedCatalogCount > 0 && !CatalogImporting;
    public int CatalogProgressPercent => CatalogTotalSites <= 0
        ? 0
        : Math.Clamp((int)Math.Round(CatalogCompletedSites * 100d / CatalogTotalSites), 0, 100);
    public string CatalogProgressText => CatalogTotalSites <= 0
        ? string.Empty
        : $"拉取进度：{CatalogCompletedSites} / {CatalogTotalSites}";
    public bool HasImportPreviewItems => ImportPreviewItems.Count > 0;
    public bool HasNoImportPreviewItems => !HasImportPreviewItems;
    public int SelectedImportCount => ImportPreviewItems.Count(item => item.IsSelected);
    public bool CanConfirmImport => !IsImporting && SelectedImportCount > 0;
    public bool AllImportItemsSelected => HasImportPreviewItems
        && ImportPreviewItems.All(item => item.IsSelected);
    public bool HasExportPreviewItems => ExportPreviewItems.Count > 0;
    public int SelectedExportCount => ExportPreviewItems.Count(item => item.IsSelected);
    public bool CanExportPreview => !IsExportLoading && SelectedExportCount > 0;
    public bool AllExportItemsSelected => HasExportPreviewItems
        && ExportPreviewItems.All(item => item.IsSelected);
    public string ExportPreviewJson => JsonSerializer.Serialize(
        ExportPreviewItems.Where(item => item.IsSelected).Select(item => new SiteExportItem
        {
            Id = item.Id,
            Name = item.Name,
            BaseUrl = item.BaseUrl,
            EndpointPathMode = item.EndpointPathMode,
            ApiKey = item.ApiKey,
            SupportsOpenAi = item.SupportsOpenAi,
            SupportsAnthropic = item.SupportsAnthropic,
            IsEnabled = item.IsEnabled
        }).ToList(),
        JsonOptions);

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var items = await _apiService.SendAsync<List<SiteListItem>>(HttpMethod.Get, "/api/admin/sites", null);
            Sites = new ObservableCollection<SiteListItem>(items ?? []);
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
    private void PreviousPage()
    {
        if (CanPreviousPage) Page--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CanNextPage) Page++;
    }

    [RelayCommand]
    private void OpenCreate()
    {
        ClearTransientErrors();
        EditingSite = null;
        Form.Reset();
        IsEditorOpen = true;
    }

    [RelayCommand]
    private async Task OpenEditAsync(SiteListItem? site)
    {
        if (site is null) return;

        EditorErrorMessage = string.Empty;
        IsEditorLoading = true;
        try
        {
            var detail = await _apiService.SendAsync<SiteDetail>(HttpMethod.Get, $"/api/admin/sites/{site.Id}", null);
            EditingSite = site;
            Form = new SiteEditForm
            {
                Name = detail.Name,
                BaseUrl = detail.BaseUrl,
                EndpointPathMode = NormalizeEndpointPathMode(detail.EndpointPathMode),
                SupportsOpenAi = detail.SupportsOpenAi,
                SupportsAnthropic = detail.SupportsAnthropic,
                IsEnabled = detail.IsEnabled
            };
            IsEditorOpen = true;
        }
        catch (Exception exception)
        {
            // 详情失败只显示局部错误，保留已经加载的站点列表。
            EditorErrorMessage = exception.Message;
            IsEditorOpen = false;
        }
        finally
        {
            IsEditorLoading = false;
        }
    }

    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
        EditorErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearTransientErrors();
        if (string.IsNullOrWhiteSpace(Form.Name) || string.IsNullOrWhiteSpace(Form.BaseUrl))
        {
            EditorErrorMessage = "站点名称和地址不能为空";
            return;
        }

        if (!IsEditMode && string.IsNullOrWhiteSpace(Form.ApiKey))
        {
            EditorErrorMessage = "新建站点必须填写 API 密钥";
            return;
        }

        IsSaving = true;
        try
        {
            var payload = new SitePayload
            {
                Name = Form.Name.Trim(),
                BaseUrl = Form.BaseUrl.Trim(),
                EndpointPathMode = NormalizeEndpointPathMode(Form.EndpointPathMode),
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
            EditorErrorMessage = exception.Message;
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
        OperationErrorMessage = string.Empty;
        try
        {
            var result = await _apiService.SendAsync<ToggleResult>(HttpMethod.Post, $"/api/admin/sites/{site.Id}/toggle", null);
            site.IsEnabled = result.IsEnabled;
        }
        catch (Exception exception)
        {
            OperationErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SiteListItem? site)
    {
        if (site is null) return;
        OperationErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(HttpMethod.Delete, $"/api/admin/sites/{site.Id}", null);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            OperationErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task BulkDeleteAsync()
    {
        var siteIds = Sites.Where(site => site.IsSelected).Select(site => site.Id).ToList();
        if (siteIds.Count == 0) return;

        OperationErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                "/api/admin/sites/bulk-delete",
                new { siteIds });
            await LoadAsync();
        }
        catch (Exception exception)
        {
            OperationErrorMessage = exception.Message;
        }
    }

    [RelayCommand]
    private void SelectAllSites()
    {
        foreach (var site in Sites) site.IsSelected = true;
        OnPropertyChanged(nameof(SelectedSiteCount));
        OnPropertyChanged(nameof(HasSelectedSites));
        OnPropertyChanged(nameof(CanBulkDelete));
    }

    [RelayCommand]
    private void ClearSiteSelection()
    {
        foreach (var site in Sites) site.IsSelected = false;
        OnPropertyChanged(nameof(SelectedSiteCount));
        OnPropertyChanged(nameof(HasSelectedSites));
        OnPropertyChanged(nameof(CanBulkDelete));
    }

    public async Task<string> ExportJsonAsync()
    {
        OperationErrorMessage = string.Empty;
        try
        {
            var items = await _apiService.SendAsync<List<SiteExportItem>>(HttpMethod.Get, "/api/admin/sites/export", null);
            return JsonSerializer.Serialize(items ?? [], JsonOptions);
        }
        catch (Exception exception)
        {
            OperationErrorMessage = exception.Message;
            return string.Empty;
        }
    }

    public async Task LoadExportPreviewAsync()
    {
        IsExportPreviewOpen = true;
        IsExportLoading = true;
        OperationErrorMessage = string.Empty;
        try
        {
            var items = await _apiService.SendAsync<List<SiteExportItem>>(
                HttpMethod.Get,
                "/api/admin/sites/export",
                null);
            SetExportPreviewItems(items ?? []);
        }
        catch (Exception exception)
        {
            OperationErrorMessage = exception.Message;
            SetExportPreviewItems([]);
        }
        finally
        {
            IsExportLoading = false;
            NotifyPreviewProperties();
        }
    }

    public void CloseExportPreview() => IsExportPreviewOpen = false;

    public void OpenImportPreview()
    {
        OperationErrorMessage = string.Empty;
        ImportJsonText = string.Empty;
        SetImportPreviewItems([]);
        IsImportPreviewOpen = true;
    }

    public void CloseImportPreview()
    {
        IsImportPreviewOpen = false;
        ImportJsonText = string.Empty;
        SetImportPreviewItems([]);
    }

    public bool ParseImportPreview(string json)
    {
        OperationErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            OperationErrorMessage = "导入文件为空";
            return false;
        }

        try
        {
            var importedItems = JsonSerializer.Deserialize<List<SiteImportPreviewItem>>(json, JsonOptions) ?? [];
            foreach (var item in importedItems)
            {
                item.IsSelected = !string.IsNullOrWhiteSpace(item.Name)
                    && !string.IsNullOrWhiteSpace(item.BaseUrl)
                    && !string.IsNullOrWhiteSpace(item.ApiKey);
            }

            SetImportPreviewItems(importedItems);
            if (!HasImportPreviewItems)
            {
                OperationErrorMessage = "JSON 中没有站点记录";
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            OperationErrorMessage = $"JSON 解析失败：{exception.Message}";
            SetImportPreviewItems([]);
            return false;
        }
    }

    [RelayCommand]
    private void SelectAllImport()
    {
        foreach (var item in ImportPreviewItems) item.IsSelected = true;
        NotifyPreviewProperties();
    }

    [RelayCommand]
    private void ClearImportSelection()
    {
        foreach (var item in ImportPreviewItems) item.IsSelected = false;
        NotifyPreviewProperties();
    }

    [RelayCommand]
    private void SelectAllExport()
    {
        foreach (var item in ExportPreviewItems) item.IsSelected = true;
        NotifyPreviewProperties();
    }

    [RelayCommand]
    private void ClearExportSelection()
    {
        foreach (var item in ExportPreviewItems) item.IsSelected = false;
        NotifyPreviewProperties();
    }

    [RelayCommand]
    private async Task ImportSelectedSitesAsync()
    {
        if (!CanConfirmImport) return;

        IsImporting = true;
        OperationMessage = string.Empty;
        OperationErrorMessage = string.Empty;
        try
        {
            var payloads = ImportPreviewItems
                .Where(item => item.IsSelected
                    && !string.IsNullOrWhiteSpace(item.Name)
                    && !string.IsNullOrWhiteSpace(item.BaseUrl)
                    && !string.IsNullOrWhiteSpace(item.ApiKey))
                .Select(item => new SitePayload
                {
                    Name = item.Name.Trim(),
                    BaseUrl = item.BaseUrl.Trim(),
                    EndpointPathMode = NormalizeEndpointPathMode(item.EndpointPathMode),
                    ApiKey = item.ApiKey.Trim(),
                    SupportsOpenAi = item.SupportsOpenAi,
                    SupportsAnthropic = item.SupportsAnthropic,
                    IsEnabled = item.IsEnabled
                })
                .ToList();

            if (payloads.Count == 0)
            {
                OperationErrorMessage = "请至少选择一条有效站点记录";
                return;
            }

            var result = await _apiService.SendAsync<ImportSitesResult>(
                HttpMethod.Post,
                "/api/admin/sites/import",
                payloads);
            OperationErrorMessage = string.Empty;
            IsImportPreviewOpen = false;
            await LoadAsync();
            OperationMessage = $"已导入 {result.ImportedCount} 个站点";
        }
        catch (Exception exception)
        {
            OperationErrorMessage = exception.Message;
        }
        finally
        {
            IsImporting = false;
            NotifyPreviewProperties();
        }
    }

    public async Task ImportJsonAsync(string json)
    {
        OpenImportPreview();
        ImportJsonText = json;
        ParseImportPreview(json);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task FetchModelsAsync(SiteListItem? site)
    {
        if (site is null) return;
        var localCancellation = BeginCatalogRequest();
        var cancellationToken = localCancellation.Token;
        CatalogVisible = true;
        CatalogLoading = true;
        CatalogErrorMessage = string.Empty;
        CatalogTaskId = string.Empty;
        CatalogTotalSites = 1;
        CatalogCompletedSites = 0;
        ClearCatalogSites();

        try
        {
            var models = await _apiService.SendAsync<List<RemoteModelInfo>>(
                HttpMethod.Get,
                $"/api/admin/site-catalog/fetch-models/{Uri.EscapeDataString(site.Id)}",
                null,
                cancellationToken: cancellationToken);
            if (!IsCurrentCatalogRequest(localCancellation)) return;
            ApplyCatalogResults(
            [
                new SiteFetchResult
                {
                    SiteId = site.Id,
                    SiteName = site.Name,
                    Status = "success",
                    Models = models ?? []
                }
            ]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭目录窗口或重新发起拉取时，忽略已取消的单站点请求。
        }
        catch (Exception exception) when (IsCurrentCatalogRequest(localCancellation))
        {
            ApplyCatalogResults(
            [
                new SiteFetchResult
                {
                    SiteId = site.Id,
                    SiteName = site.Name,
                    Status = "fail",
                    Error = exception.Message
                }
            ]);
            CatalogErrorMessage = "单站点模型目录拉取失败，请查看站点详情中的错误信息。";
        }
        finally
        {
            if (IsCurrentCatalogRequest(localCancellation))
            {
                CatalogCompletedSites = 1;
                CatalogLoading = false;
                Interlocked.CompareExchange(ref _catalogCancellation, null, localCancellation);
            }

            localCancellation.Dispose();
        }
    }

    [RelayCommand]
    private async Task FetchAllModelsAsync()
    {
        var localCancellation = BeginCatalogRequest();
        var cancellationToken = localCancellation.Token;
        CatalogVisible = true;
        CatalogLoading = true;
        CatalogErrorMessage = string.Empty;
        CatalogTaskId = string.Empty;
        CatalogTotalSites = 0;
        CatalogCompletedSites = 0;
        ClearCatalogSites();

        try
        {
            var start = await _apiService.SendAsync<FetchAllStartResponse>(
                HttpMethod.Post,
                "/api/admin/site-catalog/fetch-all-models",
                null,
                cancellationToken: cancellationToken);
            if (!IsCurrentCatalogRequest(localCancellation)) return;
            if (string.IsNullOrWhiteSpace(start.TaskId))
            {
                CatalogErrorMessage = start.Message ?? "没有可拉取的启用站点";
                return;
            }

            CatalogTaskId = start.TaskId;
            while (!cancellationToken.IsCancellationRequested)
            {
                var progress = await _apiService.SendAsync<FetchAllProgress>(
                    HttpMethod.Get,
                    $"/api/admin/site-catalog/fetch-all-progress/{Uri.EscapeDataString(CatalogTaskId)}",
                    null,
                    cancellationToken: cancellationToken);
                if (!IsCurrentCatalogRequest(localCancellation)) return;
                ApplyCatalogProgress(progress);
                if (progress.IsCompleted) break;
                await Task.Delay(1200, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭目录窗口或重新发起拉取时，安静地结束旧轮询。
        }
        catch (Exception exception) when (IsCurrentCatalogRequest(localCancellation))
        {
            CatalogErrorMessage = exception.Message;
        }
        finally
        {
            if (IsCurrentCatalogRequest(localCancellation))
            {
                CatalogLoading = false;
                Interlocked.CompareExchange(ref _catalogCancellation, null, localCancellation);
            }

            localCancellation.Dispose();
        }
    }

    [RelayCommand]
    private void CloseCatalog()
    {
        CancelCatalogRequest();
        CatalogVisible = false;
        CatalogLoading = false;
    }

    [RelayCommand]
    private void SelectAllCatalog()
    {
        foreach (var model in CatalogSites.SelectMany(site => site.Models)) model.IsSelected = true;
        NotifyCatalogSelectionStateChanged();
    }

    [RelayCommand]
    private void ClearCatalogSelection()
    {
        foreach (var model in CatalogSites.SelectMany(site => site.Models)) model.IsSelected = false;
        NotifyCatalogSelectionStateChanged();
    }

    [RelayCommand]
    private async Task ImportSelectedModelsAsync()
    {
        var selections = CatalogSites
            .SelectMany(site => site.Models)
            .Select(model => new ModelSelectionItem
            {
                SiteId = model.SiteId,
                RemoteModelName = model.RemoteModelName,
                DisplayName = model.DisplayName.Trim(),
                Selected = model.IsSelected
            })
            .ToList();
        if (selections.Count == 0 || selections.All(item => !item.Selected))
        {
            CatalogErrorMessage = "请至少选择一个模型";
            return;
        }

        CatalogImporting = true;
        CatalogErrorMessage = string.Empty;
        try
        {
            await _apiService.SendAsync<object>(
                HttpMethod.Post,
                "/api/admin/site-catalog/import-selected",
                new ImportSelectedModelsRequest { Selections = selections });
            CatalogVisible = false;
        }
        catch (Exception exception)
        {
            CatalogErrorMessage = exception.Message;
        }
        finally
        {
            CatalogImporting = false;
        }
    }

    private void SetImportPreviewItems(IEnumerable<SiteImportPreviewItem> items)
    {
        foreach (var item in _importPreviewSubscriptions)
        {
            item.PropertyChanged -= OnImportPreviewItemPropertyChanged;
        }
        _importPreviewSubscriptions.Clear();

        ImportPreviewItems = new ObservableCollection<SiteImportPreviewItem>(items);
        foreach (var item in ImportPreviewItems)
        {
            item.PropertyChanged += OnImportPreviewItemPropertyChanged;
            _importPreviewSubscriptions.Add(item);
        }
        NotifyPreviewProperties();
    }

    private void SetExportPreviewItems(IEnumerable<SiteExportItem> items)
    {
        foreach (var item in _exportPreviewSubscriptions)
        {
            item.PropertyChanged -= OnExportPreviewItemPropertyChanged;
        }
        _exportPreviewSubscriptions.Clear();

        ExportPreviewItems = new ObservableCollection<SiteExportItem>(items);
        foreach (var item in ExportPreviewItems)
        {
            item.PropertyChanged += OnExportPreviewItemPropertyChanged;
            _exportPreviewSubscriptions.Add(item);
        }
        NotifyPreviewProperties();
    }

    private void OnImportPreviewItemPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SiteImportPreviewItem.IsSelected))
        {
            NotifyPreviewProperties();
        }
    }

    private void OnExportPreviewItemPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SiteExportItem.IsSelected))
        {
            NotifyPreviewProperties();
        }
    }

    private void NotifyPreviewProperties()
    {
        OnPropertyChanged(nameof(HasImportPreviewItems));
        OnPropertyChanged(nameof(HasNoImportPreviewItems));
        OnPropertyChanged(nameof(SelectedImportCount));
        OnPropertyChanged(nameof(CanConfirmImport));
        OnPropertyChanged(nameof(AllImportItemsSelected));
        OnPropertyChanged(nameof(HasExportPreviewItems));
        OnPropertyChanged(nameof(SelectedExportCount));
        OnPropertyChanged(nameof(CanExportPreview));
        OnPropertyChanged(nameof(AllExportItemsSelected));
        OnPropertyChanged(nameof(ExportPreviewJson));
    }

    partial void OnEditingSiteChanged(SiteListItem? value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(EditorTitle));
    }

    partial void OnSitesChanged(ObservableCollection<SiteListItem> value)
    {
        foreach (var site in _siteSelectionSubscriptions) site.SelectionChanged -= OnSiteSelectionChanged;
        _siteSelectionSubscriptions.Clear();
        foreach (var site in value)
        {
            site.SelectionChanged += OnSiteSelectionChanged;
            _siteSelectionSubscriptions.Add(site);
        }

        Page = Math.Min(Page, TotalPages);
        OnPropertyChanged(nameof(HasSites));
        OnPropertyChanged(nameof(NoSites));
        OnPropertyChanged(nameof(PagedSites));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
        OnPropertyChanged(nameof(SelectedSiteCount));
        OnPropertyChanged(nameof(HasSelectedSites));
        OnPropertyChanged(nameof(CanBulkDelete));
        OnPropertyChanged(nameof(IsListVisible));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListVisible));
        OnPropertyChanged(nameof(CanBulkDelete));
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
    }

    partial void OnPageChanged(int value)
    {
        OnPropertyChanged(nameof(PagedSites));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
    }

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnIsEditorLoadingChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnCatalogLoadingChanged(bool value) => OnPropertyChanged(nameof(CanFetchModels));
    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsListVisible));
    }
    partial void OnEditorErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasEditorError));
    partial void OnOperationErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasOperationError));
    partial void OnOperationMessageChanged(string value) => OnPropertyChanged(nameof(HasOperationMessage));
    partial void OnImportPreviewItemsChanged(ObservableCollection<SiteImportPreviewItem> value) => NotifyPreviewProperties();
    partial void OnExportPreviewItemsChanged(ObservableCollection<SiteExportItem> value) => NotifyPreviewProperties();
    partial void OnIsImportingChanged(bool value) => OnPropertyChanged(nameof(CanConfirmImport));
    partial void OnIsExportLoadingChanged(bool value) => OnPropertyChanged(nameof(CanExportPreview));
    partial void OnCatalogErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasCatalogError));
    partial void OnCatalogSearchChanged(string value) => OnPropertyChanged(nameof(FilteredCatalogSites));
    partial void OnCatalogSitesChanged(ObservableCollection<SiteCatalogSiteItem> value)
    {
        OnPropertyChanged(nameof(FilteredCatalogSites));
        NotifyCatalogStateChanged();
    }
    partial void OnCatalogImportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportCatalog));
    }
    partial void OnCatalogTotalSitesChanged(int value)
    {
        OnPropertyChanged(nameof(CatalogProgressPercent));
        OnPropertyChanged(nameof(CatalogProgressText));
        OnPropertyChanged(nameof(HasCatalogProgress));
    }
    partial void OnCatalogCompletedSitesChanged(int value)
    {
        OnPropertyChanged(nameof(CatalogProgressPercent));
        OnPropertyChanged(nameof(CatalogProgressText));
    }

    private void OnSiteSelectionChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(SelectedSiteCount));
        OnPropertyChanged(nameof(HasSelectedSites));
        OnPropertyChanged(nameof(CanBulkDelete));
    }

    private void OnCatalogSelectionChanged(object? sender, EventArgs args) => NotifyCatalogSelectionStateChanged();

    private void NotifyCatalogSelectionStateChanged()
    {
        OnPropertyChanged(nameof(SelectedCatalogCount));
        OnPropertyChanged(nameof(CanImportCatalog));
    }

    private void NotifyCatalogStateChanged()
    {
        OnPropertyChanged(nameof(HasCatalogSites));
        OnPropertyChanged(nameof(NoCatalogSites));
        OnPropertyChanged(nameof(CatalogModelCount));
        OnPropertyChanged(nameof(HasCatalogModels));
        NotifyCatalogSelectionStateChanged();
    }

    private void ClearCatalogSites()
    {
        foreach (var model in _catalogSelectionSubscriptions) model.SelectionChanged -= OnCatalogSelectionChanged;
        _catalogSelectionSubscriptions.Clear();
        CatalogSites.Clear();
        OnPropertyChanged(nameof(FilteredCatalogSites));
        NotifyCatalogStateChanged();
    }

    private void ApplyCatalogProgress(FetchAllProgress progress)
    {
        CatalogTotalSites = progress.TotalSites;
        CatalogCompletedSites = progress.CompletedSites;
        ApplyCatalogResults(progress.Sites);
    }

    private void ApplyCatalogResults(IEnumerable<SiteFetchResult> results)
    {
        var existingSites = CatalogSites.ToDictionary(site => site.SiteId, StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            if (!existingSites.TryGetValue(result.SiteId, out var catalogSite))
            {
                catalogSite = new SiteCatalogSiteItem(result);
                CatalogSites.Add(catalogSite);
                existingSites[result.SiteId] = catalogSite;
            }
            else
            {
                catalogSite.Update(result);
            }

            // 轮询只同步远端状态，不覆盖用户已经修改的选择和显示名称。
            if (result.Status == "success")
            {
                var existingModels = catalogSite.Models.ToDictionary(model => model.RemoteModelName, StringComparer.OrdinalIgnoreCase);
                var receivedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var remoteModel in result.Models)
                {
                    receivedNames.Add(remoteModel.RemoteModelName);
                    if (existingModels.TryGetValue(remoteModel.RemoteModelName, out var catalogModel))
                    {
                        catalogModel.UpdateRemoteState(remoteModel);
                    }
                    else
                    {
                        catalogModel = new SiteCatalogModelItem(result.SiteId, remoteModel);
                        catalogModel.SelectionChanged += OnCatalogSelectionChanged;
                        _catalogSelectionSubscriptions.Add(catalogModel);
                        catalogSite.Models.Add(catalogModel);
                    }
                }

                for (var index = catalogSite.Models.Count - 1; index >= 0; index--)
                {
                    if (!receivedNames.Contains(catalogSite.Models[index].RemoteModelName))
                    {
                        var removed = catalogSite.Models[index];
                        removed.SelectionChanged -= OnCatalogSelectionChanged;
                        _catalogSelectionSubscriptions.Remove(removed);
                        catalogSite.Models.RemoveAt(index);
                    }
                }
                catalogSite.NotifyModelsChanged();
            }
        }

        OnPropertyChanged(nameof(FilteredCatalogSites));
        NotifyCatalogStateChanged();
    }

    private CancellationTokenSource BeginCatalogRequest()
    {
        var localCancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _catalogCancellation, localCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        return localCancellation;
    }

    private bool IsCurrentCatalogRequest(CancellationTokenSource localCancellation)
        => ReferenceEquals(_catalogCancellation, localCancellation);

    private void CancelCatalogRequest()
    {
        var cancellation = Interlocked.Exchange(ref _catalogCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void ClearTransientErrors()
    {
        ErrorMessage = string.Empty;
        EditorErrorMessage = string.Empty;
        OperationErrorMessage = string.Empty;
        OperationMessage = string.Empty;
    }

    public void Dispose()
    {
        CancelCatalogRequest();
    }

    private static string NormalizeEndpointPathMode(string? value)
        => string.Equals(value, "versioned-base", StringComparison.OrdinalIgnoreCase)
            ? "versioned-base"
            : "standard-root";

    private sealed class ToggleResult
    {
        public bool IsEnabled { get; set; }
    }
}
