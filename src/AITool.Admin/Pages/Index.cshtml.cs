using AITool.Application.CoreRuntime;
using AITool.Admin.Services;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Admin.Pages;

/// <summary>
/// 首页仪表盘页面模型，用于展示系统的概览统计信息。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 数据库上下文，用于读取首页统计数据。
    /// </summary>
    private readonly AppDbContext _dbContext;
    private readonly CoreAdminClient _coreClient;
    private readonly CoreSyncStatusStore _syncStatusStore;

    /// <summary>
    /// 初始化首页页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext, CoreAdminClient coreClient, CoreSyncStatusStore syncStatusStore)
    {
        _dbContext = dbContext;
        _coreClient = coreClient;
        _syncStatusStore = syncStatusStore;
    }

    /// <summary>
    /// 当前启用的站点数量。
    /// </summary>
    public int EnabledSiteCount { get; set; }

    /// <summary>
    /// 已录入的模型总数。
    /// </summary>
    public int ModelCount { get; set; }

    /// <summary>
    /// 当前配置的路由规则数量。
    /// </summary>
    public int RouteRuleCount { get; set; }

    /// <summary>
    /// 处于启用状态的访问密钥数量。
    /// </summary>
    public int EnabledKeyCount { get; set; }

    /// <summary>
    /// 当前启用的检测任务数量。
    /// </summary>
    public int EnabledTaskCount { get; set; }

    public string CoreBaseUrl { get; private set; } = string.Empty;
    public bool CoreConnected { get; private set; }
    public bool CoreReady { get; private set; }
    public string CoreStatusText { get; private set; } = "未连接";
    public string CoreSyncStatusText { get; private set; } = "未同步";
    public string CoreSyncDetailText { get; private set; } = string.Empty;

    /// <summary>
    /// 加载首页概览统计数据。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        EnabledSiteCount = await _dbContext.Sites.CountAsync(s => s.IsEnabled, cancellationToken);
        ModelCount = await _dbContext.ModelLibraryItems.CountAsync(cancellationToken);
        RouteRuleCount = await _dbContext.ProxyRouteRules.CountAsync(cancellationToken);
        EnabledKeyCount = await _dbContext.ProxyAccessKeys.CountAsync(k => k.IsEnabled, cancellationToken);
        EnabledTaskCount = await _dbContext.DetectionTasks.CountAsync(t => t.IsEnabled, cancellationToken);

        CoreBaseUrl = GetCoreBaseUrl();
        await LoadCoreStatusAsync(cancellationToken);
        LoadCoreSyncStatus();
    }

    private string GetCoreBaseUrl()
    {
        var baseAddress = _coreClient.BaseAddress;
        if (baseAddress is not null)
        {
            return baseAddress.ToString().TrimEnd('/').Replace("://0.0.0.0:", "://127.0.0.1:");
        }

        return "http://127.0.0.1:5029";
    }

    private async Task LoadCoreStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _coreClient.HandshakeAsync(new CoreAdminHandshakeRequest
            {
                AdminInstanceId = "admin-dashboard",
                AdminStartedAt = DateTimeOffset.UtcNow,
                CurrentConfigVersion = 0,
                CurrentConfigHash = string.Empty,
                LastAckedSequenceId = 0
            }, cancellationToken);

            CoreConnected = true;
            CoreReady = response.Ready;
            CoreStatusText = response.Ready ? "已连接 / Ready" : "已连接 / 未就绪";
        }
        catch
        {
            CoreConnected = false;
            CoreReady = false;
            CoreStatusText = "连接失败";
        }
    }

    private void LoadCoreSyncStatus()
    {
        var snapshot = _syncStatusStore.GetSnapshot();
        CoreSyncStatusText = snapshot.LastStatus;

        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            CoreSyncDetailText = snapshot.LastError;
            return;
        }

        if (snapshot.LastSuccessAt.HasValue)
        {
            CoreSyncDetailText = $"最近成功：{snapshot.LastSuccessAt:yyyy-MM-dd HH:mm:ss}";
            return;
        }

        if (snapshot.LastFailureAt.HasValue)
        {
            CoreSyncDetailText = $"最近失败：{snapshot.LastFailureAt:yyyy-MM-dd HH:mm:ss}";
        }
    }
}
