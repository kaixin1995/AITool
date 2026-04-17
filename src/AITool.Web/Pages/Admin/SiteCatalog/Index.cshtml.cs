using AITool.Application.SiteCatalog;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.SiteCatalog;

// 站点模型映射视图模型
public class SiteCatalogViewModel
{
    public Guid SiteId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int MappingCount { get; set; }
}

// 站点模型拉取页面模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ISiteCatalogClient _catalogClient;

    public IndexModel(AppDbContext dbContext, ISiteCatalogClient catalogClient)
    {
        _dbContext = dbContext;
        _catalogClient = catalogClient;
    }

    // 站点列表及映射统计
    public List<SiteCatalogViewModel> Sites { get; set; } = [];

    // 拉取结果，用于显示成功提示
    public PullSiteModelsResult? PullResult { get; set; }

    // 加载站点列表与映射计数
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var mappingCounts = await _dbContext.SiteModelMappings
            .GroupBy(m => m.SiteId)
            .Select(g => new { SiteId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SiteId, x => x.Count, cancellationToken);

        Sites = await _dbContext.Sites
            .OrderBy(s => s.Name)
            .Select(s => new SiteCatalogViewModel
            {
                SiteId = s.Id,
                Name = s.Name,
                ProtocolType = s.ProtocolType,
                IsEnabled = s.IsEnabled
            })
            .ToListAsync(cancellationToken);

        foreach (var site in Sites)
        {
            site.MappingCount = mappingCounts.GetValueOrDefault(site.SiteId);
        }
    }

    // 执行站点模型拉取
    public async Task<IActionResult> OnPostPullAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.FindAsync([siteId], cancellationToken);
        if (site is null) return RedirectToPage();

        var remoteModels = await _catalogClient.GetModelsAsync(site, cancellationToken);

        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == siteId)
            .ToListAsync(cancellationToken);

        var importedCount = 0;
        foreach (var remoteName in remoteModels)
        {
            if (existingMappings.Any(m => m.RemoteModelName == remoteName)) continue;

            var modelItem = await _dbContext.ModelLibraryItems
                .FirstOrDefaultAsync(m => m.ModelName == remoteName, cancellationToken);

            if (modelItem is null)
            {
                modelItem = new Domain.Models.ModelLibraryItem
                {
                    ModelName = remoteName,
                    DisplayName = remoteName
                };
                _dbContext.ModelLibraryItems.Add(modelItem);
            }

            _dbContext.SiteModelMappings.Add(new SiteModelMapping
            {
                SiteId = siteId,
                ModelLibraryItemId = modelItem.Id,
                RemoteModelName = remoteName,
                LastStatus = "imported"
            });

            importedCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        PullResult = new PullSiteModelsResult { ImportedCount = importedCount };

        await OnGetAsync(cancellationToken);
        return Page();
    }
}
