using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.ClientSimulator;

public sealed class ClientSimulatorModelItemViewModel
{
    public string ModelName { get; set; } = string.Empty;
    public int RouteCount { get; set; }
    public bool SupportsOpenAi { get; set; }
    public bool SupportsAnthropic { get; set; }
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
    public string DefaultOpenAiModel { get; private set; } = string.Empty;
    public string DefaultAnthropicModel { get; private set; } = string.Empty;
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

        // 按协议汇总模型能力，避免模拟器把仅支持 OpenAI 的模型误发到 Anthropic 链路。
        var routeModels = await (
                from rule in _dbContext.ProxyRouteRules
                join site in _dbContext.Sites on rule.SiteId equals site.Id
                where rule.IsEnabled && site.IsEnabled && enabledSiteIds.Contains(rule.SiteId)
                group site by rule.ExternalModelName into g
                select new ClientSimulatorModelItemViewModel
                {
                    ModelName = g.Key,
                    RouteCount = g.Count(),
                    SupportsOpenAi = g.Any(x => x.ProtocolType == "OpenAI"),
                    SupportsAnthropic = g.Any(x => x.ProtocolType == "Anthropic")
                })
            .OrderBy(m => m.ModelName)
            .ToListAsync(cancellationToken);

        Models = routeModels;
        DefaultOpenAiModel = routeModels.FirstOrDefault(x => x.SupportsOpenAi)?.ModelName ?? string.Empty;
        DefaultAnthropicModel = routeModels.FirstOrDefault(x => x.SupportsAnthropic)?.ModelName ?? string.Empty;
    }
}
