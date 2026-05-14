using System.Text.Json;
using AITool.Application.Models;
using AITool.Domain.Models;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Pages.Admin.Models;

public class ModelWithSiteCount
{
    public Guid Id { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int SiteCount { get; set; }
}

public class ModelVendorGroupViewModel
{
    public string VendorName { get; set; } = string.Empty;
    public string IconSvgBody { get; set; } = string.Empty;
    public string HeaderBackground { get; set; } = string.Empty;
    public List<ModelWithSiteCount> Models { get; set; } = [];
}

public class IndexModel : PageModel
{
    private static readonly JsonSerializerOptions EditorJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;
    private readonly ModelVendorCatalogService? _vendorCatalogService;

    [ActivatorUtilitiesConstructor]
    public IndexModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache, ModelVendorCatalogService vendorCatalogService)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _vendorCatalogService = vendorCatalogService;
    }

    public IndexModel(AppDbContext dbContext, ModelVendorCatalogService vendorCatalogService)
    {
        _dbContext = dbContext;
        _vendorCatalogService = vendorCatalogService;
    }

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public List<ModelVendorGroupViewModel> VendorGroups { get; set; } = [];

    public string VendorCatalogEditorJson { get; set; } = string.Empty;

    [BindProperty]
    public string VendorCatalogJson { get; set; } = string.Empty;

    [BindProperty]
    public string ActiveTab { get; set; } = "models";

    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPageDataAsync(cancellationToken);
    }

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

    public async Task<IActionResult> OnPostToggleAsync(Guid modelId, CancellationToken cancellationToken)
    {
        try
        {
            var model = await _dbContext.ModelLibraryItems.FindAsync([modelId], cancellationToken);
            if (model is null) return RedirectToPage();
            model.IsEnabled = !model.IsEnabled;
            await _dbContext.SaveChangesAsync(cancellationToken);
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

    public async Task<IActionResult> OnPostDeleteAsync(Guid modelId, CancellationToken cancellationToken)
    {
        try
        {
            var model = await _dbContext.ModelLibraryItems.FindAsync([modelId], cancellationToken);
            if (model is null) return RedirectToPage();

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
            }
            _dbContext.ModelLibraryItems.Remove(model);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await CleanupEmptyRouteEntriesAsync(affectedEntryNames, cancellationToken);

            _metadataCache?.InvalidateModelMetadata();
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = $"模型已删除，并清理了 {mappings.Count} 条站点关联、{affectedRules.Count} 条相关路由规则、{affectedMonitors.Count} 条健康监控，并解绑了 {affectedDetectionTasks.Count} 个检测任务";
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

    private async Task LoadPageDataAsync(CancellationToken cancellationToken)
    {
        var catalog = await GetVendorCatalogAsync(cancellationToken);
        await LoadPageDataAsync(catalog, cancellationToken);
    }

    private async Task LoadPageDataAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken)
    {
        VendorCatalogEditorJson = JsonSerializer.Serialize(catalog, EditorJsonOptions);
        VendorCatalogJson = VendorCatalogEditorJson;
        await LoadModelGroupsAsync(catalog, cancellationToken);
    }

    private async Task<ModelVendorCatalog> GetVendorCatalogAsync(CancellationToken cancellationToken)
    {
        return _vendorCatalogService is null
            ? new ModelVendorCatalog()
            : await _vendorCatalogService.GetOrCreateAsync(cancellationToken);
    }

    // 模型分组展示统一走可维护的厂商规则，避免页面内再写死映射逻辑。
    private async Task LoadModelGroupsAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken)
    {
        var enabledSiteIds = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var siteCounts = await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled && enabledSiteIds.Contains(m.SiteId))
            .GroupBy(m => m.ModelLibraryItemId)
            .Select(g => new { ModelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ModelId, x => x.Count, cancellationToken);

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

// 比较器只按厂商名归组，避免同名厂商因对象实例不同被拆成多个分组。
internal sealed class ModelVendorDefinitionComparer : IEqualityComparer<ModelVendorDefinition>
{
    public static ModelVendorDefinitionComparer Instance { get; } = new();

    public bool Equals(ModelVendorDefinition? x, ModelVendorDefinition? y)
    {
        return string.Equals(x?.VendorName, y?.VendorName, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(ModelVendorDefinition obj)
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.VendorName ?? string.Empty);
    }
}

public class CreateModelModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    [ActivatorUtilitiesConstructor]
    public CreateModelModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    public CreateModelModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty]
    public CreateModelLibraryItemCommand Command { get; set; } = new();

    public void OnGet() { }

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
