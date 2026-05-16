using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.AccessKeys;

/// <summary>
/// 访问密钥列表项视图模型，用于页面渲染。
/// </summary>
public class AccessKeyViewModel
{
    /// <summary>
    /// 密钥记录主键。
    /// </summary>
    public Guid KeyId { get; set; }

    /// <summary>
    /// 后台展示用的密钥名称。
    /// </summary>
    public string KeyName { get; set; } = string.Empty;

    /// <summary>
    /// 明文密钥内容。
    /// </summary>
    public string PlainKey { get; set; } = string.Empty;

    /// <summary>
    /// 密钥是否处于启用状态。
    /// </summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// 访问密钥管理页面模型，负责初始列表加载。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 数据库上下文，用于读取密钥列表。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化访问密钥页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 当前页面展示的密钥列表。
    /// </summary>
    public List<AccessKeyViewModel> Keys { get; set; } = [];

    /// <summary>
    /// 读取密钥列表并按名称排序。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Keys = await _dbContext.ProxyAccessKeys
            .OrderBy(k => k.KeyName)
            .Select(k => new AccessKeyViewModel
            {
                KeyId = k.Id,
                KeyName = k.KeyName,
                PlainKey = k.PlainKey,
                IsEnabled = k.IsEnabled
            })
            .ToListAsync(cancellationToken);
    }
}
