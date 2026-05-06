using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.UsageLogs;

// 页面初始化所需的站点筛选项
public sealed class UsageLogSiteFilterItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// 调用日志页面模型，提供站点筛选初始化数据
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public List<UsageLogSiteFilterItem> SiteFilters { get; set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        SiteFilters = await _dbContext.Sites
            .OrderBy(s => s.Name)
            .Select(s => new UsageLogSiteFilterItem
            {
                Id = s.Id,
                Name = s.Name
            })
            .ToListAsync(cancellationToken);
    }
}
