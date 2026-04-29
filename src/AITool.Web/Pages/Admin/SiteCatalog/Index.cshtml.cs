using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.SiteCatalog;

public sealed class SiteCatalogItemViewModel
{
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsMapped { get; set; }
    public bool IsEnabled { get; set; }
    public string LastStatus { get; set; } = string.Empty;
}

// 站点模型发现页，仅展示各站点已发现的远程模型与当前映射状态
public sealed class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public List<SiteCatalogItemViewModel> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Items = await (
            from mapping in _dbContext.SiteModelMappings
            join site in _dbContext.Sites on mapping.SiteId equals site.Id
            join model in _dbContext.ModelLibraryItems on mapping.ModelLibraryItemId equals model.Id into modelGroup
            from model in modelGroup.DefaultIfEmpty()
            orderby site.Name, mapping.RemoteModelName
            select new SiteCatalogItemViewModel
            {
                SiteId = site.Id,
                SiteName = site.Name,
                ProtocolType = site.ProtocolType,
                RemoteModelName = mapping.RemoteModelName,
                DisplayName = model != null ? model.DisplayName : null,
                IsMapped = model != null,
                IsEnabled = mapping.IsEnabled,
                LastStatus = mapping.LastStatus
            })
            .ToListAsync(cancellationToken);
    }
}
