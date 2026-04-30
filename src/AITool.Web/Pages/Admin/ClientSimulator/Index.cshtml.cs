using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.ClientSimulator;

public sealed class ClientSimulatorModelItemViewModel
{
    public string ModelName { get; set; } = string.Empty;
    public int RouteCount { get; set; }
}

public sealed class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public string DefaultBaseUrl { get; private set; } = string.Empty;
    public string DefaultAccessKey { get; private set; } = string.Empty;
    public List<ClientSimulatorModelItemViewModel> Models { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        DefaultBaseUrl = $"{Request.Scheme}://{Request.Host}";

        DefaultAccessKey = await _dbContext.ProxyAccessKeys
            .Where(k => k.IsEnabled && !string.IsNullOrWhiteSpace(k.PlainKey))
            .OrderBy(k => k.KeyName)
            .Select(k => k.PlainKey)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        // 从路由规则中获取对外模型名，与 /v1/models 端点保持一致
        var enabledSiteIds = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var routeModels = await _dbContext.ProxyRouteRules
            .Where(r => r.IsEnabled && enabledSiteIds.Contains(r.SiteId))
            .GroupBy(r => r.ExternalModelName)
            .Select(g => new ClientSimulatorModelItemViewModel
            {
                ModelName = g.Key,
                RouteCount = g.Count()
            })
            .OrderBy(m => m.ModelName)
            .ToListAsync(cancellationToken);

        Models = routeModels;
    }
}
