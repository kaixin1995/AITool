using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Admin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages.Admin.Sites;

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
    /// 接口路径模式。
    /// </summary>
    public string EndpointPathMode { get; set; } = SiteEndpointPathResolver.StandardRoot;

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
    /// 后台缓存失效服务。
    /// </summary>
    private readonly AdminCacheInvalidationService _cacheInvalidation;

    /// <summary>
    /// 包含缓存失效服务的构造函数。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public ImportModel(AppDbContext dbContext, AdminCacheInvalidationService cacheInvalidation)
    {
        _dbContext = dbContext;
        _cacheInvalidation = cacheInvalidation;
    }

    /// <summary>
    /// 不含缓存失效服务的构造函数。
    /// </summary>
    public ImportModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
        _cacheInvalidation = null!;
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

            var items = JsonSerializer.Deserialize<List<ImportSiteItem>>(jsonData, JsonSerializerPresets.CaseInsensitive);

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
                    EndpointPathMode = SiteEndpointPathResolver.NormalizeMode(item.EndpointPathMode),
                    ApiKey = item.ApiKey,
                    ProtocolType = ResolveSiteProtocolType(item.SupportsOpenAi, item.SupportsAnthropic),
                    SupportsOpenAi = item.SupportsOpenAi,
                    SupportsAnthropic = item.SupportsAnthropic,
                    IsEnabled = true
                });
                created++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await _cacheInvalidation.InvalidateRouteTargetsAsync();
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
        if (!supportsOpenAi && !supportsAnthropic)
        {
            return "Responses";
        }

        return supportsAnthropic && !supportsOpenAi ? "Anthropic" : "OpenAI";
    }
}
