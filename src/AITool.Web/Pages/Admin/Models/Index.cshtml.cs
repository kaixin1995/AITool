using AITool.Application.Models;
using AITool.Domain.Models;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Models;

// 模型库视图模型，包含关联站点数
public class ModelWithSiteCount
{
    public Guid Id { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    // 关联的站点数量
    public int SiteCount { get; set; }
}

// 模型库列表页模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    [ActivatorUtilitiesConstructor]
    public IndexModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 模型库列表数据
    public List<ModelWithSiteCount> Models { get; set; } = [];

    // 状态消息
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载模型列表，包含每个模型关联的站点数量
    public async Task OnGetAsync(CancellationToken cancellationToken)
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

        Models = await _dbContext.ModelLibraryItems
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

        foreach (var model in Models)
        {
            model.SiteCount = siteCounts.GetValueOrDefault(model.Id);
        }
    }

    // 切换模型启用/禁用状态
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
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 删除模型时，同时清理该模型下全部站点映射与相关路由规则，避免留下失效关联。
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

            // 先按站点范围取回候选规则，再在内存里按站点+模型精确匹配，避免 EF 无法翻译本地集合 Any。
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
        await OnGetAsync(cancellationToken);
        return Page();
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

// 模型库创建页模型
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

    // 显示创建表单
    public void OnGet() { }

    // 提交模型创建
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();

        // 将命令转为实体并保存
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
