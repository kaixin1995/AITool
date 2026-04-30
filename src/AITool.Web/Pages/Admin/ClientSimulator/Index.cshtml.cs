using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.ClientSimulator;

public sealed class ClientSimulatorModelItemViewModel
{
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int AvailableSiteCount { get; set; }
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

        var enabledMappings = await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled)
            .ToListAsync(cancellationToken);

        var modelIds = enabledMappings
            .Select(m => m.ModelLibraryItemId)
            .Distinct()
            .ToList();

        var models = await _dbContext.ModelLibraryItems
            .Where(m => m.IsEnabled && modelIds.Contains(m.Id))
            .OrderBy(m => m.DisplayName)
            .Select(m => new
            {
                m.Id,
                m.ModelName,
                m.DisplayName
            })
            .ToListAsync(cancellationToken);

        var siteCounts = enabledMappings
            .GroupBy(m => m.ModelLibraryItemId)
            .ToDictionary(g => g.Key, g => g.Count());

        Models = models
            .Select(m => new ClientSimulatorModelItemViewModel
            {
                ModelName = m.ModelName,
                DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.ModelName : m.DisplayName,
                AvailableSiteCount = siteCounts.GetValueOrDefault(m.Id)
            })
            .ToList();
    }
}
