using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Sites;

// 站点管理列表页模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 站点列表数据
    public List<Site> Sites { get; set; } = [];

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

    // 删除站点
    public async Task<IActionResult> OnPostDeleteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        try
        {
            var site = await _dbContext.Sites.FindAsync([siteId], cancellationToken);
            if (site is null) return RedirectToPage();
            _dbContext.Sites.Remove(site);
            await _dbContext.SaveChangesAsync(cancellationToken);
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
        return RedirectToPage("./Index");
    }
}
