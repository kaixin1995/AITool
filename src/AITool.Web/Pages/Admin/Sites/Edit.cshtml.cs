using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Sites;

/// <summary>
/// 站点编辑页面模型。
/// </summary>
public class EditModel : PageModel
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
    /// 站点编辑页面模型。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public EditModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 站点编辑页面模型。
    /// </summary>
    public EditModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Name。
    /// </summary>
    [BindProperty]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 基础地址。
    /// </summary>
    [BindProperty]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 接口密钥。
    /// </summary>
    [BindProperty]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 是否支持 OpenAI 协议。
    /// </summary>
    [BindProperty]
    public bool SupportsOpenAi { get; set; } = true;

    /// <summary>
    /// 是否支持 Anthropic 协议。
    /// </summary>
    [BindProperty]
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 是否启用。
    /// </summary>
    [BindProperty]
    public bool IsEnabled { get; set; }

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
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.FindAsync([id], cancellationToken);
        if (site is null) return RedirectToPage("./Index");

        Name = site.Name;
        BaseUrl = site.BaseUrl;
        ApiKey = null;
        SupportsOpenAi = site.SupportsOpenAi;
        SupportsAnthropic = site.SupportsAnthropic;
        IsEnabled = site.IsEnabled;

        return Page();
    }

    /// <summary>
    /// 处理页面提交请求。
    /// </summary>
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!SupportsOpenAi && !SupportsAnthropic)
        {
            ModelState.AddModelError(nameof(SupportsOpenAi), "至少选择一种支持协议");
        }

        if (!ModelState.IsValid) return Page();

        try
        {
            var site = await _dbContext.Sites.FindAsync([id], cancellationToken);
            if (site is null) return RedirectToPage("./Index");

            site.Name = Name;
            site.BaseUrl = BaseUrl;
            // 编辑时留空表示继续使用原有密钥。
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                site.ApiKey = ApiKey;
            }
            site.SupportsOpenAi = SupportsOpenAi;
            site.SupportsAnthropic = SupportsAnthropic;
            site.ProtocolType = ResolveSiteProtocolType(SupportsOpenAi, SupportsAnthropic);
            site.IsEnabled = IsEnabled;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _metadataCache?.InvalidateRouteTargets();
            StatusMessage = "站点已更新";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
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
