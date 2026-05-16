using System.Text.Json;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Sites;

/// <summary>
/// 导入站点项。
/// </summary>
public class ImportSiteItem
{
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// 基础地址。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// 接口密钥。
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

/// <summary>
/// 站点导入页面模型。
/// </summary>
public class ImportModel : PageModel
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache? _metadataCache;

    /// <summary>
    /// 站点导入页面模型。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public ImportModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 站点导入页面模型。
    /// </summary>
    public ImportModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 状态提示。
    /// </summary>
    public string? StatusMessage { get; set; }
    /// <summary>
    /// 操作是否成功。
    /// </summary>
    public bool StatusSuccess { get; set; }

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public void OnGet() { }

    /// <summary>
    /// 处理页面提交请求。
    /// </summary>
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

    /// <summary>
    /// 根据站点能力推导协议类型。
    /// </summary>
    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        return supportsAnthropic && !supportsOpenAi ? "Anthropic" : "OpenAI";
    }
}
