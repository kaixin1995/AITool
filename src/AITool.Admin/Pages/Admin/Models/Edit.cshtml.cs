using AITool.Admin.Services;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Admin.Pages.Admin.Models;

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
public sealed class ManualSiteMappingInput
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 站点模型名。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

public class EditModel : PageModel
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 后台缓存失效服务。
    /// </summary>
    private readonly AdminCacheInvalidationService _cacheInvalidation;

    /// <summary>
    /// 包含缓存失效服务的构造函数。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public EditModel(AppDbContext dbContext, AdminCacheInvalidationService cacheInvalidation)
    {
        _dbContext = dbContext;
        _cacheInvalidation = cacheInvalidation;
    }

    /// <summary>
    /// 不含缓存失效服务的构造函数。
    /// </summary>
    public EditModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
        _cacheInvalidation = null!;
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
    /// 强制覆盖的思考等级。留空=不干预（透传客户端原始值），非空=强制覆盖。
    /// </summary>
    [BindProperty]
    public string OverrideReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 关联的兼容规则集 Id（可空）。为空表示不应用任何规则集。
    /// </summary>
    [BindProperty]
    public Guid? CompatibilityProfileId { get; set; }

    /// <summary>
    /// 可选的兼容规则集列表（仅启用的），供下拉框选择。
    /// </summary>
    public List<ProfileOption> AvailableProfiles { get; set; } = new();

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
    /// 手动新增关联站点表单。
    /// </summary>
    [BindProperty]
    public ManualSiteMappingInput NewMapping { get; set; } = new();

    /// <summary>
    /// 可选站点列表。
    /// </summary>
    public List<SelectListItem> AvailableSites { get; set; } = [];

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
            var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
            if (model is null) return RedirectToPage("./Index");

            model.ModelName = ModelName;
            model.DisplayName = DisplayName;
            model.IsEnabled = IsEnabled;
            model.OverrideReasoningEffort = (OverrideReasoningEffort ?? string.Empty).Trim();
            model.CompatibilityProfileId = CompatibilityProfileId;

            await _dbContext.UpdateAsync(model, cancellationToken);
            await _cacheInvalidation.InvalidateModelMetadataAsync(cancellationToken);
            await _cacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);
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
    /// 手动新增模型与站点的关联。
    /// </summary>
    public async Task<IActionResult> OnPostAddMappingAsync(Guid id, CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(ModelName));
        ModelState.Remove(nameof(DisplayName));
        ModelState.Remove(nameof(IsEnabled));

        if (NewMapping.SiteId == Guid.Empty)
        {
            StatusMessage = "请选择站点";
            StatusSuccess = false;
            await LoadPageDataAsync(id, cancellationToken);
            return Page();
        }

        if (string.IsNullOrWhiteSpace(NewMapping.RemoteModelName))
        {
            StatusMessage = "请填写站点模型名";
            StatusSuccess = false;
            await LoadPageDataAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
            if (model is null)
            {
                return RedirectToPage("./Index");
            }

            var site = await _dbContext.Sites
                .FirstAsync(x => x.Id == NewMapping.SiteId && x.IsEnabled, cancellationToken);
            if (site is null)
            {
                StatusMessage = "所选站点不存在或已禁用";
                StatusSuccess = false;
                await LoadPageDataAsync(id, cancellationToken);
                return Page();
            }

            var remoteModelName = NewMapping.RemoteModelName.Trim();
            var existingMapping = await _dbContext.SiteModelMappings
                .FirstAsync(x => x.SiteId == NewMapping.SiteId && x.RemoteModelName == remoteModelName, cancellationToken);
            if (existingMapping is not null)
            {
                existingMapping.ModelLibraryItemId = id;
                existingMapping.IsEnabled = NewMapping.IsEnabled;
                existingMapping.LastStatus = "manual";
                await _dbContext.UpdateAsync(existingMapping, cancellationToken);
            }
            else
            {
                await _dbContext.InsertAsync(new AITool.Domain.SiteCatalog.SiteModelMapping
                {
                    SiteId = NewMapping.SiteId,
                    ModelLibraryItemId = id,
                    RemoteModelName = remoteModelName,
                    LastStatus = "manual",
                    IsEnabled = NewMapping.IsEnabled
                }, cancellationToken);
            }

            await _cacheInvalidation.InvalidateModelMetadataAsync(cancellationToken);
            await _cacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);
            StatusMessage = "关联站点已添加";
            StatusSuccess = true;
            NewMapping = new ManualSiteMappingInput();
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }

        await LoadPageDataAsync(id, cancellationToken);
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
                .FirstAsync(x => x.Id == mappingId && x.ModelLibraryItemId == id, cancellationToken);
            if (mapping is null)
            {
                return RedirectToPage("./Index");
            }

            var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
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

            await _dbContext.DeleteAsync(mapping, cancellationToken);
            if (affectedRules.Count > 0)
            {
                await _dbContext.DeleteRangeAsync(affectedRules, cancellationToken);
            }

            await CleanupEmptyRouteEntriesAsync(affectedEntryNames, cancellationToken);

            await _cacheInvalidation.InvalidateModelMetadataAsync(cancellationToken);
            await _cacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);
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
        var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
        if (model is null)
        {
            return false;
        }

        CurrentModelId = id;
        ModelName = model.ModelName;
        DisplayName = model.DisplayName;
        IsEnabled = model.IsEnabled;
        OverrideReasoningEffort = model.OverrideReasoningEffort;
        CompatibilityProfileId = model.CompatibilityProfileId;
        await LoadAvailableProfilesAsync(cancellationToken);
        await LoadSiteMappingsAsync(id, cancellationToken);
        await LoadAvailableSitesAsync(id, cancellationToken);
        if (string.IsNullOrWhiteSpace(NewMapping.RemoteModelName))
        {
            NewMapping.RemoteModelName = model.ModelName;
        }
        return true;
    }

    /// <summary>
    /// 加载启用的兼容规则集列表，供模型编辑页下拉框选择。
    /// </summary>
    private async Task LoadAvailableProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await _dbContext.CompatibilityProfiles
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
        AvailableProfiles = profiles.Select(p => new ProfileOption(p.Id, p.Name)).ToList();
    }

    /// <summary>
    /// 加载模型关联的站点列表。
    /// </summary>
    private async Task LoadSiteMappingsAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var mappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
        var sites = await _dbContext.Sites.ToListAsync(cancellationToken);
        SiteMappings = (
                from mapping in mappings
                join site in sites on mapping.SiteId equals site.Id
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
            .ToList();
    }

    /// <summary>
    /// 加载可选站点列表。
    /// </summary>
    private async Task LoadAvailableSitesAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var mappedSiteIds = await _dbContext.SiteModelMappings
            .Where(x => x.ModelLibraryItemId == modelId)
            .Select(x => x.SiteId)
            .Distinct()
            .ToListAsync(cancellationToken);

        AvailableSites = await _dbContext.Sites
            .Where(x => x.IsEnabled && !mappedSiteIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .Select(x => new SelectListItem
            {
                Value = x.Id.ToString(),
                Text = x.Name
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

        await _dbContext.DeleteRangeAsync(emptyEntries, cancellationToken);
    }
}

/// <summary>
/// 兼容规则集下拉选项。
/// </summary>
public sealed record ProfileOption(Guid Id, string Name);
