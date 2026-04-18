using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.AccessKeys;

// 访问密钥视图模型
public class AccessKeyViewModel
{
    public Guid KeyId { get; set; }
    public string KeyName { get; set; } = string.Empty;
    public string MaskedValue { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

// 访问密钥管理页面模型，仅负责初始加载
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 密钥列表
    public List<AccessKeyViewModel> Keys { get; set; } = [];

    // 加载密钥列表
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Keys = await _dbContext.ProxyAccessKeys
            .OrderBy(k => k.KeyName)
            .Select(k => new AccessKeyViewModel
            {
                KeyId = k.Id,
                KeyName = k.KeyName,
                MaskedValue = k.MaskedValue,
                IsEnabled = k.IsEnabled
            })
            .ToListAsync(cancellationToken);
    }
}
