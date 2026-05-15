using AITool.Application.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.ClientSimulator;

public sealed class ClientSimulatorModelItemViewModel
{
    public string ModelName { get; set; } = string.Empty;
    public int RouteCount { get; set; }
    public bool SupportsOpenAi { get; set; }
    public bool SupportsAnthropic { get; set; }
    public bool CanUseOpenAi { get; set; }
    public bool CanUseAnthropic { get; set; }
}

public sealed class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;

    public IndexModel(AppDbContext dbContext, ISystemRuntimeSettingsService runtimeSettingsService)
    {
        _dbContext = dbContext;
        _runtimeSettingsService = runtimeSettingsService;
    }

    public string DefaultBaseUrl { get; private set; } = string.Empty;
    public string DefaultAccessKey { get; private set; } = string.Empty;
    public string DefaultOpenAiModel { get; private set; } = string.Empty;
    public string DefaultAnthropicModel { get; private set; } = string.Empty;
    public List<ClientSimulatorModelItemViewModel> Models { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        return Redirect("/Admin/Developer/Invocations#developerSimulatorPane");
    }
}
