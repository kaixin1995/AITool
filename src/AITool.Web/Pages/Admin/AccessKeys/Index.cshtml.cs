using System.Text.Json;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

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

    /// <summary>
    /// 允许访问的路由入口名称列表。空列表=允许全部路由。
    /// </summary>
    public List<string> AllowedRouteNames { get; set; } = [];

    /// <summary>
    /// 允许路由的展示文本（逗号分隔，空=全部）。
    /// </summary>
    public string AllowedRouteNamesText => AllowedRouteNames.Count == 0
        ? "全部"
        : string.Join(", ", AllowedRouteNames);
}

/// <summary>
/// 路由入口选项，用于前端多选下拉框。
/// </summary>
public class RouteEntryOption
{
    /// <summary>
    /// 路由入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
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
    /// 可选的路由入口列表，用于创建/编辑密钥时的多选下拉框。
    /// </summary>
    public List<RouteEntryOption> RouteEntries { get; set; } = [];

    /// <summary>
    /// 读取密钥列表和路由入口列表并按名称排序。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var rawKeys = await _dbContext.ProxyAccessKeys
            .OrderBy(k => k.KeyName)
            .ToListAsync(cancellationToken);

        Keys = rawKeys.Select(k => new AccessKeyViewModel
        {
            KeyId = k.Id,
            KeyName = k.KeyName,
            PlainKey = k.PlainKey,
            IsEnabled = k.IsEnabled,
            AllowedRouteNames = DeserializeRouteNames(k.AllowedRouteNames)
        }).ToList();

        RouteEntries = await _dbContext.ProxyRouteEntries
            .OrderBy(e => e.EntryName)
            .Select(e => new RouteEntryOption { EntryName = e.EntryName })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 将存储的 JSON 字符串反序列化为路由名称列表。
    /// </summary>
    private static List<string> DeserializeRouteNames(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(stored) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
