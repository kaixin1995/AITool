using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

[ApiController]
[Route("api/admin/chat")]
public sealed class ChatApiController : ControllerBase
{
    private readonly AdminQueryMetadataService _adminQueryMetadataService;

    public ChatApiController(AdminQueryMetadataService adminQueryMetadataService)
    {
        _adminQueryMetadataService = adminQueryMetadataService;
    }

    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        var models = await _adminQueryMetadataService.GetChatModelsAsync(cancellationToken);
        return Ok(models.Select(x => new
        {
            modelId = x.ModelId,
            displayName = x.DisplayName,
            availableSiteCount = x.AvailableSiteCount
        }));
    }

    [HttpGet("targets")]
    public async Task<IActionResult> GetTargets(CancellationToken cancellationToken)
    {
        var targets = await _adminQueryMetadataService.GetChatTargetsAsync(cancellationToken);
        return Ok(targets.Select(x => new
        {
            mappingId = x.MappingId,
            modelId = x.ModelId,
            modelDisplayName = x.ModelDisplayName,
            siteId = x.SiteId,
            siteName = x.SiteName,
            protocolType = x.ProtocolType,
            baseUrl = x.BaseUrl,
            endpointPathMode = x.EndpointPathMode,
            siteModelName = x.SiteModelName
        }));
    }
}
