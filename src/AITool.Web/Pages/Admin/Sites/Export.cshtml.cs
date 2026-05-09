using System.Text.Json;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Sites;

/// <summary>
/// 站点导出页模型
/// </summary>
public class ExportModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public ExportModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /* 站点列表（用于表格渲染） */
    public List<SiteExportItem> Sites { get; set; } = new();

    /* 序列化后的 JSON（用于前端预览和下载） */
    public string JsonData { get; set; } = "[]";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var sites = _dbContext.Sites.ToList();

        Sites = sites.Select(s => new SiteExportItem
        {
            Id = s.Id,
            Name = s.Name,
            BaseUrl = s.BaseUrl,
            ApiKey = s.ApiKey,
            SupportsOpenAi = s.SupportsOpenAi,
            SupportsAnthropic = s.SupportsAnthropic
        }).ToList();

        /* 导出用的匿名对象，仅包含必要字段 */
        var exportData = sites.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            baseUrl = s.BaseUrl,
            apiKey = s.ApiKey,
            supportsOpenAi = s.SupportsOpenAi,
            supportsAnthropic = s.SupportsAnthropic
        });

        JsonData = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }
}

/// <summary>
/// 站点导出展示项
/// </summary>
public class SiteExportItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool SupportsOpenAi { get; set; } = true;
    public bool SupportsAnthropic { get; set; }
}
