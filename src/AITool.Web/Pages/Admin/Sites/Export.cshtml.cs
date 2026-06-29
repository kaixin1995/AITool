using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Sites;

/// <summary>
/// 站点导出页面模型，负责准备表格展示数据和导出 JSON。
/// </summary>
public class ExportModel : PageModel
{
    /// <summary>
    /// 数据库上下文，用于读取站点信息。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化站点导出页面模型。
    /// </summary>
    public ExportModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 页面表格展示用的站点列表。
    /// </summary>
    public List<SiteExportItem> Sites { get; set; } = new();

    /// <summary>
    /// 导出给前端预览和下载的 JSON 文本。
    /// </summary>
    public string JsonData { get; set; } = "[]";

    /// <summary>
    /// 读取站点数据，并构造导出所需的 JSON 内容。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites.ToListAsync(cancellationToken);

        Sites = sites.Select(s => new SiteExportItem
        {
            Id = s.Id,
            Name = s.Name,
            BaseUrl = s.BaseUrl,
            EndpointPathMode = SiteEndpointPathResolver.NormalizeMode(s.EndpointPathMode),
            ApiKey = s.ApiKey,
            SupportsOpenAi = s.SupportsOpenAi,
            SupportsAnthropic = s.SupportsAnthropic
        }).ToList();

        // 导出内容只保留站点恢复所需字段，避免掺入页面展示状态。
        var exportData = sites.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            baseUrl = s.BaseUrl,
            endpointPathMode = SiteEndpointPathResolver.NormalizeMode(s.EndpointPathMode),
            apiKey = s.ApiKey,
            supportsOpenAi = s.SupportsOpenAi,
            supportsAnthropic = s.SupportsAnthropic
        });

        JsonData = JsonSerializer.Serialize(exportData, JsonSerializerPresets.Compact);
    }
}

/// <summary>
/// 站点导出页面中的单条展示项。
/// </summary>
public class SiteExportItem
{
    /// <summary>
    /// 站点主键。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 站点基础地址。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 接口路径模式。
    /// </summary>
    public string EndpointPathMode { get; set; } = SiteEndpointPathResolver.StandardRoot;

    /// <summary>
    /// 站点 API Key。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 是否支持 OpenAI 协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; } = true;

    /// <summary>
    /// 是否支持 Anthropic 协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }
}
