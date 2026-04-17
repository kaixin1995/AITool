using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Routes;

// 路由规则视图模型
public class RouteRuleViewModel
{
    public Guid RuleId { get; set; }
    public string ExternalModelName { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool IsEnabled { get; set; }
}

// 路由规则管理页面模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 路由规则列表
    public List<RouteRuleViewModel> Rules { get; set; } = [];

    // 可选站点列表，用于创建表单
    public List<Site> AvailableSites { get; set; } = [];

    // 操作结果提示
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载路由规则列表和可选站点
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        AvailableSites = await _dbContext.Sites.ToListAsync(cancellationToken);

        var siteDict = AvailableSites.ToDictionary(s => s.Id);

        Rules = await _dbContext.ProxyRouteRules
            .OrderBy(r => r.ExternalModelName)
            .ThenBy(r => r.Priority)
            .Select(r => new RouteRuleViewModel
            {
                RuleId = r.Id,
                ExternalModelName = r.ExternalModelName,
                SiteModelName = r.SiteModelName,
                Priority = r.Priority,
                IsEnabled = r.IsEnabled
            })
            .ToListAsync(cancellationToken);

        // 填充站点名称
        foreach (var rule in Rules)
        {
            // 手动查找站点名称，避免导航属性
            var ruleEntity = await _dbContext.ProxyRouteRules.FindAsync([rule.RuleId], cancellationToken);
            if (ruleEntity is not null && siteDict.TryGetValue(ruleEntity.SiteId, out var site))
            {
                rule.SiteName = site.Name;
            }
        }
    }

    // 创建新的路由规则
    public async Task<IActionResult> OnPostCreateAsync(
        string externalModelName, Guid siteId, string siteModelName, int priority,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalModelName) || siteId == Guid.Empty || string.IsNullOrWhiteSpace(siteModelName))
        {
            StatusMessage = "外部模型名、目标站点和站点模型名不能为空";
            StatusSuccess = false;
            await OnGetAsync(cancellationToken);
            return Page();
        }

        var rule = new ProxyRouteRule
        {
            ExternalModelName = externalModelName,
            SiteId = siteId,
            SiteModelName = siteModelName,
            Priority = priority,
            IsEnabled = true
        };
        _dbContext.ProxyRouteRules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        StatusMessage = $"路由规则 \"{externalModelName}\" 创建成功";
        StatusSuccess = true;
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 切换路由规则启用/禁用状态
    public async Task<IActionResult> OnPostToggleAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.ProxyRouteRules.FindAsync([ruleId], cancellationToken);
        if (rule is null) return RedirectToPage();

        rule.IsEnabled = !rule.IsEnabled;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 删除路由规则
    public async Task<IActionResult> OnPostDeleteAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.ProxyRouteRules.FindAsync([ruleId], cancellationToken);
        if (rule is null) return RedirectToPage();

        _dbContext.ProxyRouteRules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        StatusMessage = "路由规则已删除";
        StatusSuccess = true;
        await OnGetAsync(cancellationToken);
        return Page();
    }
}
