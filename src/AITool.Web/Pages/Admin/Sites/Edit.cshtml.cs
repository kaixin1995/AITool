using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Sites;

// 站点编辑页模型，加载现有站点数据并提供更新功能
public class EditModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    [ActivatorUtilitiesConstructor]
    public EditModel(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    public EditModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string BaseUrl { get; set; } = string.Empty;

    [BindProperty]
    public string? ApiKey { get; set; }

    [BindProperty]
    public bool SupportsOpenAi { get; set; } = true;

    [BindProperty]
    public bool SupportsAnthropic { get; set; }

    [BindProperty]
    public bool IsEnabled { get; set; }

    // 状态消息
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载站点数据填充表单
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

    // 提交站点更新
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

    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        return supportsAnthropic && !supportsOpenAi ? "Anthropic" : "OpenAI";
    }
}
