using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Sites;

// 站点管理列表页模型
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

    // 站点列表数据
    public List<Site> Sites { get; set; } = [];

    // 批量删除选择的站点 ID
    [BindProperty]
    public List<Guid> SelectedSiteIds { get; set; } = [];

    // 状态消息
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载站点列表
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Sites = await _dbContext.Sites
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    // 切换站点启用/禁用状态
    public async Task<IActionResult> OnPostToggleAsync(Guid siteId, CancellationToken cancellationToken)
    {
        try
        {
            var site = await _dbContext.Sites.FindAsync([siteId], cancellationToken);
            if (site is null) return RedirectToPage();
            site.IsEnabled = !site.IsEnabled;
            await _dbContext.SaveChangesAsync(cancellationToken);
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = "站点状态已切换";
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

    // 批量删除站点，并同步清理关联映射
    public async Task<IActionResult> OnPostBulkDeleteAsync(CancellationToken cancellationToken)
    {
        if (SelectedSiteIds.Count == 0)
        {
            StatusMessage = "请先选择要删除的站点";
            StatusSuccess = false;
            await OnGetAsync(cancellationToken);
            return Page();
        }

        try
        {
            var sites = await _dbContext.Sites
                .Where(x => SelectedSiteIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            if (sites.Count == 0) return RedirectToPage();

            var mappings = await _dbContext.SiteModelMappings
                .Where(x => SelectedSiteIds.Contains(x.SiteId))
                .ToListAsync(cancellationToken);

            _dbContext.SiteModelMappings.RemoveRange(mappings);
            _dbContext.Sites.RemoveRange(sites);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = $"已批量删除 {sites.Count} 个站点";
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

    // 删除站点
    public async Task<IActionResult> OnPostDeleteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        try
        {
            var site = await _dbContext.Sites.FindAsync([siteId], cancellationToken);
            if (site is null) return RedirectToPage();
            _dbContext.Sites.Remove(site);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = "站点已删除";
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
}

// 站点创建页模型
public class CreateModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    [ActivatorUtilitiesConstructor]
    public CreateModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    public CreateModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty]
    public CreateSiteCommand Command { get; set; } = new();

    // 显示创建表单
    public void OnGet() { }

    // 提交站点创建
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();

        // 将命令转为实体并保存
        _dbContext.Sites.Add(new Site
        {
            Name = Command.Name,
            BaseUrl = Command.BaseUrl,
            ApiKey = Command.ApiKey,
            ProtocolType = Command.ProtocolType,
            IsEnabled = Command.IsEnabled
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        return RedirectToPage("./Index");
    }
}
