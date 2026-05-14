using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Pages.Admin.Models;

// 模型编辑页关联站点视图模型
public class ModelSiteMappingViewModel
{
    public Guid MappingId { get; set; }
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

// 模型编辑页模型，加载现有模型数据并提供更新功能
public class EditModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    [ActivatorUtilitiesConstructor]
    public EditModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    public EditModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty]
    public string ModelName { get; set; } = string.Empty;

    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public bool IsEnabled { get; set; }

    // 状态消息
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 当前模型标识，供页面内的关联操作复用
    public Guid CurrentModelId { get; set; }

    // 当前模型关联的站点列表
    public List<ModelSiteMappingViewModel> SiteMappings { get; set; } = [];

    // 加载模型数据填充表单
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadPageDataAsync(id, cancellationToken);
        if (!loaded)
        {
            return RedirectToPage("./Index");
        }

        return Page();
    }

    // 提交模型更新
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            CurrentModelId = id;
            await LoadSiteMappingsAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            var model = await _dbContext.ModelLibraryItems.FindAsync([id], cancellationToken);
            if (model is null) return RedirectToPage("./Index");

            model.ModelName = ModelName;
            model.DisplayName = DisplayName;            model.IsEnabled = IsEnabled;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _metadataCache?.InvalidateModelMetadata();
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = "模型已更新";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }

        await LoadSiteMappingsAsync(id, cancellationToken);
        return Page();
    }

    // 删除模型关联站点时，同时清理对应的路由规则与空路由入口。
    public async Task<IActionResult> OnPostDeleteMappingAsync(Guid id, Guid mappingId, CancellationToken cancellationToken)
    {
        try
        {
            var mapping = await _dbContext.SiteModelMappings
                .FirstOrDefaultAsync(x => x.Id == mappingId && x.ModelLibraryItemId == id, cancellationToken);
            if (mapping is null)
            {
                return RedirectToPage("./Index");
            }

            var model = await _dbContext.ModelLibraryItems.FindAsync([id], cancellationToken);
            if (model is null)
            {
                return RedirectToPage("./Index");
            }

            var affectedRules = await _dbContext.ProxyRouteRules
                .Where(x => x.SiteId == mapping.SiteId && x.SiteModelName == mapping.RemoteModelName)
                .ToListAsync(cancellationToken);
            var affectedEntryNames = affectedRules
                .Select(x => x.ExternalModelName)
                .Append(model.ModelName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            _dbContext.SiteModelMappings.Remove(mapping);
            if (affectedRules.Count > 0)
            {
                _dbContext.ProxyRouteRules.RemoveRange(affectedRules);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await CleanupEmptyRouteEntriesAsync(affectedEntryNames, cancellationToken);

            _metadataCache?.InvalidateModelMetadata();
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = $"站点关联已删除，并清理了 {affectedRules.Count} 条相关路由规则";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }

        var loaded = await LoadPageDataAsync(id, cancellationToken);
        if (!loaded)
        {
            return RedirectToPage("./Index");
        }

        return Page();
    }

    // 统一加载模型表单和关联站点，避免各个处理器重复拼装页面数据。
    private async Task<bool> LoadPageDataAsync(Guid id, CancellationToken cancellationToken)
    {
        var model = await _dbContext.ModelLibraryItems.FindAsync([id], cancellationToken);
        if (model is null)
        {
            return false;
        }

        CurrentModelId = id;
        ModelName = model.ModelName;
        DisplayName = model.DisplayName;        IsEnabled = model.IsEnabled;
        await LoadSiteMappingsAsync(id, cancellationToken);
        return true;
    }

    // 关联站点列表仅按当前模型读取，供编辑页单独管理。
    private async Task LoadSiteMappingsAsync(Guid modelId, CancellationToken cancellationToken)
    {
        SiteMappings = await (
                from mapping in _dbContext.SiteModelMappings
                join site in _dbContext.Sites on mapping.SiteId equals site.Id
                where mapping.ModelLibraryItemId == modelId
                orderby site.Name, mapping.RemoteModelName
                select new ModelSiteMappingViewModel
                {
                    MappingId = mapping.Id,
                    SiteId = site.Id,
                    SiteName = site.Name,
                    RemoteModelName = mapping.RemoteModelName,
                    IsEnabled = mapping.IsEnabled
                })
            .ToListAsync(cancellationToken);
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
