using System.Text.Json;
using AITool.Application.Models;
using AITool.Domain.Models;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Pages.Admin.Models;

/// <summary>
/// 带站点数量的模型信息。
/// </summary>
public class ModelWithSiteCount
{
    /// <summary>
    /// 标识。
    /// </summary>
    public Guid Id { get; set; }
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>
    /// 站点数量。
    /// </summary>
    public int SiteCount { get; set; }
}

/// <summary>
/// 模型厂商分组。
/// </summary>
public class ModelVendorGroupViewModel
{
    /// <summary>
    /// 厂商名称。
    /// </summary>
    public string VendorName { get; set; } = string.Empty;
    /// <summary>
    /// 图标 SVG 内容。
    /// </summary>
    public string IconSvgBody { get; set; } = string.Empty;
    /// <summary>
    /// 头部背景色。
    /// </summary>
    public string HeaderBackground { get; set; } = string.Empty;
    /// <summary>
    /// 模型列表。
    /// </summary>
    public List<ModelWithSiteCount> Models { get; set; } = [];
}

/// <summary>
/// 模型管理页面模型。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 编辑器 JSON 序列化选项。
    /// </summary>
    private static readonly JsonSerializerOptions EditorJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache? _metadataCache;
    /// <summary>
    /// 模型厂商目录服务。
    /// </summary>
    private readonly ModelVendorCatalogService? _vendorCatalogService;

    /// <summary>
    /// 注入元数据缓存和厂商目录服务。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public IndexModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache, ModelVendorCatalogService vendorCatalogService)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _vendorCatalogService = vendorCatalogService;
    }

    /// <summary>
    /// 注入元数据缓存。
    /// </summary>
    public IndexModel(AppDbContext dbContext, ModelVendorCatalogService vendorCatalogService)
    {
        _dbContext = dbContext;
        _vendorCatalogService = vendorCatalogService;
    }

    /// <summary>
    /// Razor 页面模型绑定。
    /// </summary>
    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 厂商分组列表。
    /// </summary>
    public List<ModelVendorGroupViewModel> VendorGroups { get; set; } = [];

    /// <summary>
    /// 厂商规则编辑器数据。
    /// </summary>
    public string VendorCatalogEditorJson { get; set; } = string.Empty;

    /// <summary>
    /// 厂商规则提交数据。
    /// </summary>
    [BindProperty]
    public string VendorCatalogJson { get; set; } = string.Empty;

    /// <summary>
    /// 当前激活页签。
    /// </summary>
    [BindProperty]
    public string ActiveTab { get; set; } = "models";

    /// <summary>
    /// 状态提示。
    /// </summary>
    public string? StatusMessage { get; set; }
    /// <summary>
    /// 操作是否成功。
    /// </summary>
    public bool StatusSuccess { get; set; }

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPageDataAsync(cancellationToken);
    }

    /// <summary>
    /// 保存厂商规则配置。
    /// </summary>
    public async Task<IActionResult> OnPostSaveVendorCatalogAsync(CancellationToken cancellationToken)
    {
        ActiveTab = "rules";
        if (_vendorCatalogService is null)
        {
            StatusMessage = "当前环境未启用厂商规则保存服务";
            StatusSuccess = false;
            await LoadPageDataAsync(cancellationToken);
            return Page();
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<ModelVendorCatalog>(VendorCatalogJson, EditorJsonOptions)
                ?? throw new InvalidOperationException("厂商规则数据不能为空");
            var savedCatalog = await _vendorCatalogService.SaveAsync(catalog, cancellationToken);
            StatusMessage = "厂商规则已保存";
            StatusSuccess = true;
            await LoadPageDataAsync(savedCatalog, cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            StatusSuccess = false;
            var persistedCatalog = await GetVendorCatalogAsync(cancellationToken);
            await LoadModelGroupsAsync(persistedCatalog, cancellationToken);
            VendorCatalogEditorJson = string.IsNullOrWhiteSpace(VendorCatalogJson)
                ? JsonSerializer.Serialize(persistedCatalog, EditorJsonOptions)
                : VendorCatalogJson;
        }

        return Page();
    }

    /// <summary>
    /// 切换启用状态。
    /// </summary>
    public async Task<IActionResult> OnPostToggleAsync(Guid modelId, CancellationToken cancellationToken)
    {
        try
        {
            var model = await _dbContext.ModelLibraryItems.InSingleAsync(modelId);
            if (model is null) return RedirectToPage();
            model.IsEnabled = !model.IsEnabled;
            await _dbContext.UpdateAsync(model, cancellationToken);
            _metadataCache?.InvalidateModelMetadata();
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = "模型状态已切换";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }

        ActiveTab = "models";
        await LoadPageDataAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// 处理删除请求。
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var isAjaxRequest = IsAjaxRequest();
        try
        {
            var model = await _dbContext.ModelLibraryItems.InSingleAsync(modelId);
            if (model is null)
            {
                if (isAjaxRequest)
                {
                    return new JsonResult(new { success = false, message = "模型不存在或已被删除" }) { StatusCode = 404 };
                }

                return RedirectToPage();
            }

            var mappings = await _dbContext.SiteModelMappings
                .Where(x => x.ModelLibraryItemId == modelId)
                .ToListAsync(cancellationToken);
            var mappingPairs = mappings
                .Select(x => new { x.SiteId, x.RemoteModelName })
                .ToList();
            var mappingSiteIds = mappingPairs
                .Select(x => x.SiteId)
                .Distinct()
                .ToList();

            var candidateRules = await _dbContext.ProxyRouteRules
                .Where(x => x.ExternalModelName == model.ModelName || mappingSiteIds.Contains(x.SiteId))
                .ToListAsync(cancellationToken);
            var affectedRules = candidateRules
                .Where(x => x.ExternalModelName == model.ModelName || mappingPairs.Any(p => p.SiteId == x.SiteId && p.RemoteModelName == x.SiteModelName))
                .ToList();
            var affectedEntryNames = affectedRules
                .Select(x => x.ExternalModelName)
                .Append(model.ModelName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var affectedMonitors = await _dbContext.ModelHealthMonitors
                .Where(x => x.ModelLibraryItemId == modelId)
                .ToListAsync(cancellationToken);
            var affectedDetectionTasks = await _dbContext.DetectionTasks
                .Where(x => x.ModelLibraryItemId == modelId)
                .ToListAsync(cancellationToken);

            if (mappings.Count > 0)
            {
                _dbContext.SiteModelMappings.RemoveRange(mappings);
            }
            if (affectedRules.Count > 0)
            {
                _dbContext.ProxyRouteRules.RemoveRange(affectedRules);
            }
            if (affectedMonitors.Count > 0)
            {
                _dbContext.ModelHealthMonitors.RemoveRange(affectedMonitors);
            }
            foreach (var task in affectedDetectionTasks)
            {
                // 删除模型后清空检测任务的模型绑定，避免后台继续出现已删除模型。
                task.ModelLibraryItemId = null;
                await _dbContext.UpdateAsync(task, cancellationToken);
            }
            _dbContext.ModelLibraryItems.Remove(model);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await CleanupEmptyRouteEntriesAsync(affectedEntryNames, cancellationToken);

            _metadataCache?.InvalidateModelMetadata();
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = $"模型已删除，并清理了 {mappings.Count} 条站点关联、{affectedRules.Count} 条相关路由规则、{affectedMonitors.Count} 条健康监控，并解绑了 {affectedDetectionTasks.Count} 个检测任务";
            StatusSuccess = true;

            if (isAjaxRequest)
            {
                return new JsonResult(new { success = true, message = StatusMessage });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
            if (isAjaxRequest)
            {
                return new JsonResult(new { success = false, message = StatusMessage }) { StatusCode = 400 };
            }
        }

        ActiveTab = "models";
        await LoadPageDataAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// 加载页面所需数据。
    /// </summary>
    private async Task LoadPageDataAsync(CancellationToken cancellationToken)
    {
        var catalog = await GetVendorCatalogAsync(cancellationToken);
        await LoadPageDataAsync(catalog, cancellationToken);
    }

    /// <summary>
    /// 加载页面所需数据。
    /// </summary>
    private async Task LoadPageDataAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken)
    {
        VendorCatalogEditorJson = JsonSerializer.Serialize(catalog, EditorJsonOptions);
        VendorCatalogJson = VendorCatalogEditorJson;
        await LoadModelGroupsAsync(catalog, cancellationToken);
    }

    /// <summary>
    /// 获取厂商规则配置。
    /// </summary>
    private async Task<ModelVendorCatalog> GetVendorCatalogAsync(CancellationToken cancellationToken)
    {
        return _vendorCatalogService is null
            ? new ModelVendorCatalog()
            : await _vendorCatalogService.GetOrCreateAsync(cancellationToken);
    }

    /// <summary>
    /// 加载模型厂商分组。
    /// </summary>
    private async Task LoadModelGroupsAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken)
    {
        var enabledSiteIds = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var siteCounts = (await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled && enabledSiteIds.Contains(m.SiteId))
            .ToListAsync(cancellationToken))
            .GroupBy(m => m.ModelLibraryItemId)
            .Select(g => new { ModelId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.ModelId, x => x.Count);

        var models = await _dbContext.ModelLibraryItems
            .OrderBy(x => x.ModelName)
            .Select(x => new ModelWithSiteCount
            {
                Id = x.Id,
                ModelName = x.ModelName,
                DisplayName = x.DisplayName,
                IsEnabled = x.IsEnabled,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        foreach (var model in models)
        {
            model.SiteCount = siteCounts.GetValueOrDefault(model.Id);
        }

        VendorGroups = models
            .GroupBy(x => ModelVendorCatalogService.ResolveVendor(catalog, x.ModelName), ModelVendorDefinitionComparer.Instance)
            .OrderBy(g => g.Key.SortOrder)
            .ThenBy(g => g.Key.VendorName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelVendorGroupViewModel
            {
                VendorName = g.Key.VendorName,
                IconSvgBody = g.Key.IconSvgBody,
                HeaderBackground = g.Key.HeaderBackground,
                Models = g.OrderBy(x => x.ModelName, StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();
    }

    /// <summary>
    /// 判断当前请求是否来自页面内的异步操作。
    /// </summary>
    private bool IsAjaxRequest()
    {
        return string.Equals(Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 清理空的路由入口。
    /// </summary>
    private async Task CleanupEmptyRouteEntriesAsync(IEnumerable<string> entryNames, CancellationToken cancellationToken)
    {
        var normalizedNames = entryNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedNames.Count == 0)
        {
            return;
        }

        var remainingEntryNames = await _dbContext.ProxyRouteRules
            .Where(x => normalizedNames.Contains(x.ExternalModelName))
            .Select(x => x.ExternalModelName)
            .Distinct()
            .ToListAsync(cancellationToken);
        var emptyEntryNames = normalizedNames
            .Except(remainingEntryNames, StringComparer.Ordinal)
            .ToList();

        if (emptyEntryNames.Count == 0)
        {
            return;
        }

        var emptyEntries = await _dbContext.ProxyRouteEntries
            .Where(x => emptyEntryNames.Contains(x.EntryName))
            .ToListAsync(cancellationToken);
        if (emptyEntries.Count == 0)
        {
            return;
        }

        _dbContext.ProxyRouteEntries.RemoveRange(emptyEntries);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// 模型厂商比较器。
/// </summary>
internal sealed class ModelVendorDefinitionComparer : IEqualityComparer<ModelVendorDefinition>
{
    /// <summary>
    /// 单例比较器实例。
    /// </summary>
    public static ModelVendorDefinitionComparer Instance { get; } = new();

    /// <summary>
    /// 比较两个厂商定义是否相同。
    /// </summary>
    public bool Equals(ModelVendorDefinition? x, ModelVendorDefinition? y)
    {
        return string.Equals(x?.VendorName, y?.VendorName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 返回厂商定义的哈希值。
    /// </summary>
    public int GetHashCode(ModelVendorDefinition obj)
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.VendorName ?? string.Empty);
    }
}

/// <summary>
/// 新建模型页面模型。
/// </summary>
public class CreateModelModel : PageModel
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache? _metadataCache;

    /// <summary>
    /// 包含元数据缓存的构造函数。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public CreateModelModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 不含元数据缓存的构造函数。
    /// </summary>
    public CreateModelModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 新建模型提交命令。
    /// </summary>
    [BindProperty]
    public CreateModelLibraryItemCommand Command { get; set; } = new();

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public void OnGet() { }

    /// <summary>
    /// 处理页面提交请求。
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();

        _dbContext.ModelLibraryItems.Add(new ModelLibraryItem
        {
            ModelName = Command.ModelName,
            DisplayName = Command.DisplayName,
            IsEnabled = Command.IsEnabled
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache?.InvalidateModelMetadata();
        _metadataCache?.InvalidateRouteTargets();
        return RedirectToPage("./Index");
    }
}
