using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Sites;

/// <summary>
/// 站点管理页面模型。
/// </summary>
public class IndexModel : PageModel
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
    /// 站点管理页面模型。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public IndexModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 站点管理页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 站点列表。
    /// </summary>
    public List<Site> Sites { get; set; } = [];

    /// <summary>
    /// 选中的站点标识列表。
    /// </summary>
    [BindProperty]
    public List<Guid> SelectedSiteIds { get; set; } = [];

    /// <summary>
    /// 状态提示。
    /// </summary>
    public string? StatusMessage { get; set; }
    /// <summary>
    /// 操作是否成功。
    /// </summary>
    public bool StatusSuccess { get; set; }

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Sites = await _dbContext.Sites
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 切换启用状态。
    /// </summary>
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

    /// <summary>
    /// 批量删除站点。
    /// </summary>
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

    /// <summary>
    /// 处理删除请求。
    /// </summary>
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

/// <summary>
/// 站点新建页面模型。
/// </summary>
public class CreateModel : PageModel
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
    /// 站点新建页面模型。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public CreateModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 站点新建页面模型。
    /// </summary>
    public CreateModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// new。
    /// </summary>
    [BindProperty]
    public CreateSiteCommand Command { get; set; } = new();

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public void OnGet() { }

    /// <summary>
    /// 处理页面提交请求。
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!Command.SupportsOpenAi && !Command.SupportsAnthropic)
        {
            ModelState.AddModelError("Command.SupportsOpenAi", "至少选择一种支持协议");
        }

        if (!ModelState.IsValid) return Page();

        // 站点不再由页面选择默认协议，这里仅保留兼容字段的推导值。
        _dbContext.Sites.Add(new Site
        {
            Name = Command.Name,
            BaseUrl = Command.BaseUrl,
            ApiKey = Command.ApiKey,
            ProtocolType = ResolveSiteProtocolType(Command.SupportsOpenAi, Command.SupportsAnthropic),
            SupportsOpenAi = Command.SupportsOpenAi,
            SupportsAnthropic = Command.SupportsAnthropic,
            IsEnabled = Command.IsEnabled
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        return RedirectToPage("./Index");
    }

    /// <summary>
    /// 根据站点能力推导协议类型。
    /// </summary>
    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        return supportsAnthropic && !supportsOpenAi ? "Anthropic" : "OpenAI";
    }
}
