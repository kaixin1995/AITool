using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.UsageLogs;

/// <summary>
/// 调用日志页面的站点筛选项，用于初始化筛选下拉框。
/// </summary>
public sealed class UsageLogSiteFilterItem
{
    /// <summary>
    /// 站点主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 调用日志页面的访问密钥筛选项，用于初始化筛选下拉框。
/// </summary>
public sealed class UsageLogAccessKeyFilterItem
{
    /// <summary>
    /// 访问密钥主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 访问密钥名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 调用日志页面模型，负责提供初始筛选数据。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 数据库上下文，用于读取站点列表。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化调用日志页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 页面可选的站点筛选项。
    /// </summary>
    public List<UsageLogSiteFilterItem> SiteFilters { get; set; } = [];

    /// <summary>
    /// 页面可选的访问密钥筛选项。
    /// </summary>
    public List<UsageLogAccessKeyFilterItem> AccessKeyFilters { get; set; } = [];

    /// <summary>
    /// 加载站点和访问密钥筛选项，并按名称排序。
    /// </summary>
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

        AccessKeyFilters = await _dbContext.ProxyAccessKeys
            .OrderBy(k => k.KeyName)
            .Select(k => new UsageLogAccessKeyFilterItem
            {
                Id = k.Id,
                Name = k.KeyName
            })
            .ToListAsync(cancellationToken);
    }
}
