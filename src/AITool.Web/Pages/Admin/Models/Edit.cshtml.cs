using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Pages.Admin.Models;

/// <summary>
/// 模型关联站点信息。
/// </summary>
public class ModelSiteMappingViewModel
{
    /// <summary>
    /// 关联标识。
    /// </summary>
    public Guid MappingId { get; set; }
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 远程模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
    /// <summary>
    /// 该站点模型的最大并发数，0 表示不限制。
    /// </summary>
    public int MaxConcurrency { get; set; }
}

/// <summary>
/// 模型编辑页面模型。
/// </summary>
public class EditModel : PageModel
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
    /// 模型编辑页面模型。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public EditModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 模型编辑页面模型。
    /// </summary>
    public EditModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 模型名称。
    /// </summary>
    [BindProperty]
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称。
    /// </summary>
    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用。
    /// </summary>
    [BindProperty]
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 状态提示。
    /// </summary>
    public string? StatusMessage { get; set; }
    /// <summary>
    /// 操作是否成功。
    /// </summary>
    public bool StatusSuccess { get; set; }

    /// <summary>
    /// 当前模型标识。
    /// </summary>
    public Guid CurrentModelId { get; set; }

    /// <summary>
    /// 站点关联列表。
    /// </summary>
    public List<ModelSiteMappingViewModel> SiteMappings { get; set; } = [];

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadPageDataAsync(id, cancellationToken);
        if (!loaded)
        {
            return RedirectToPage("./Index");
        }

        return Page();
    }

    /// <summary>
    /// 处理页面提交请求。
    /// </summary>
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

    /// <summary>
    /// 删除模型与站点的关联。
    /// </summary>
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

    /// <summary>
    /// 加载页面所需数据。
    /// </summary>
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

    /// <summary>
    /// 加载模型关联的站点列表。
    /// </summary>
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
                    IsEnabled = mapping.IsEnabled,
                    MaxConcurrency = mapping.MaxConcurrency
                })
            .ToListAsync(cancellationToken);
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
