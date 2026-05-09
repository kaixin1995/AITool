using System.Text.Json;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Sites;

// 站点导入数据传输模型
public class ImportSiteItem
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool SupportsOpenAi { get; set; } = true;
    public bool SupportsAnthropic { get; set; }
}

// 站点批量导入页模型
public class ImportModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    [ActivatorUtilitiesConstructor]
    public ImportModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    public ImportModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 操作结果提示
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    public void OnGet() { }

    // 批量导入站点
    public async Task<IActionResult> OnPostAsync(string jsonData, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                StatusMessage = "未收到有效数据";
                StatusSuccess = false;
                return Page();
            }

            var items = JsonSerializer.Deserialize<List<ImportSiteItem>>(jsonData, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (items is null || items.Count == 0)
            {
                StatusMessage = "解析结果为空，请检查数据格式";
                StatusSuccess = false;
                return Page();
            }

            var created = 0;
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.BaseUrl) || string.IsNullOrWhiteSpace(item.ApiKey))
                    continue;

                _dbContext.Sites.Add(new Site
                {
                    Name = item.Name,
                    BaseUrl = item.BaseUrl,
                    ApiKey = item.ApiKey,
                    ProtocolType = ResolveSiteProtocolType(item.SupportsOpenAi, item.SupportsAnthropic),
                    SupportsOpenAi = item.SupportsOpenAi,
                    SupportsAnthropic = item.SupportsAnthropic,
                    IsEnabled = true
                });
                created++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = $"成功导入 {created} 个站点";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
            StatusSuccess = false;
        }
        return Page();
    }

    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        return supportsAnthropic && !supportsOpenAi ? "Anthropic" : "OpenAI";
    }
}
