using AITool.Application.CoreRuntime;
using AITool.Domain.Sites;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// AllInOne 单进程形态的 <see cref="ICoreRuntimeConfigProvider"/> 实现：
/// 快照直接来自/写回 SQLite（无跨宿主下发）。
/// 仅满足 <see cref="CoreCredentialRefreshEngine"/> 的使用面（AccountCredentials + Sites.ApiKey），
/// 不承载完整路由快照语义（AllInOne 的元数据缓存走 DB 模式）。
/// </summary>
public sealed class DbBackedRuntimeConfigProvider : ICoreRuntimeConfigProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    // 缓存改为惰性解析：快照模式的 ProxyRequestMetadataCache 依赖 ICoreRuntimeConfigProvider，
    // 构造期直取会形成 DI 环（测试宿主接入本提供器时崩溃）；失效只在回写路径用，按需取即可。
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<DbBackedRuntimeConfigProvider> _logger;

    public DbBackedRuntimeConfigProvider(
        IServiceScopeFactory scopeFactory,
        IServiceProvider rootProvider,
        ILogger<DbBackedRuntimeConfigProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _rootProvider = rootProvider;
        _logger = logger;
    }

    public CoreRuntimeConfigSnapshot? GetCurrent()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            EnsureSchema(db);

            // 全量构建：测试/AllInOne 形态下缓存走快照分支，密钥/路由/映射/设置都从这里读。
            var settings = db.SystemRuntimeSettings.ToList().FirstOrDefault();
            var snapshot = CoreRuntimeConfigSnapshotBuilder.Build(
                db.Sites.ToList(),
                db.ModelLibraryItems.ToList(),
                db.SiteModelMappings.ToList(),
                db.ProxyRouteEntries.ToList(),
                db.ProxyRouteRules.ToList(),
                db.ProxyAccessKeys.ToList(),
                settings ?? new Domain.Operations.SystemRuntimeSettings(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow,
                compatibilityProfiles: null,
                siteKeys: db.SiteKeys.ToList(),
                codexAccounts: db.CodexAccounts.ToList(),
                googleAccounts: db.GoogleAccounts.ToList(),
                kimiAccounts: db.KimiAccounts.ToList(),
                proxyProfiles: db.ProxyProfiles.ToList(),
                activeHeaderProfiles: null,
                accountNetworkPolicies: db.AccountNetworkPolicies.ToList());
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DbBacked 配置快照读取失败");
            return null;
        }
    }

    public void SetCurrent(CoreRuntimeConfigSnapshot snapshot)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var credential in snapshot.AccountCredentials)
            {
                switch (credential.Provider)
                {
                    case "Codex" when db.CodexAccounts.Any(a => a.Id == credential.AccountId && a.LinkedSiteId == credential.LinkedSiteId):
                        db.Client.Updateable<AITool.Domain.Codex.CodexAccount>(a => a.Id == credential.AccountId)
                            .SetColumns(a => a.RefreshToken == credential.RefreshToken)
                            .ExecuteCommand();
                        break;
                    case "Google" when db.GoogleAccounts.Any(a => a.Id == credential.AccountId && a.LinkedSiteId == credential.LinkedSiteId):
                        db.Client.Updateable<AITool.Domain.Google.GoogleAccount>(a => a.Id == credential.AccountId)
                            .SetColumns(a => a.RefreshToken == credential.RefreshToken)
                            .ExecuteCommand();
                        break;
                    case "Kimi" when db.KimiAccounts.Any(a => a.Id == credential.AccountId && a.LinkedSiteId == credential.LinkedSiteId):
                        db.Client.Updateable<AITool.Domain.Kimi.KimiAccount>(a => a.Id == credential.AccountId)
                            .SetColumns(a => a.RefreshToken == credential.RefreshToken)
                            .ExecuteCommand();
                        break;
                }
            }

            foreach (var site in snapshot.Sites)
            {
                if (db.Sites.Any(s => s.Id == site.Id && s.ApiKey != site.ApiKey))
                {
                    db.Client.Updateable<Site>(s => s.Id == site.Id)
                        .SetColumns(s => new Site { ApiKey = site.ApiKey })
                        .ExecuteCommand();
                }
            }

            // 失效凭证与路由缓存，让新凭证立即对元数据缓存生效（惰性解析，避免构造期 DI 环）。
            var metadataCache = _rootProvider.GetService<ProxyRequestMetadataCache>();
            metadataCache?.InvalidateAccessKeys();
            metadataCache?.InvalidateCodexAccounts();
            metadataCache?.InvalidateGoogleAccounts();
            metadataCache?.InvalidateKimiAccounts();
            metadataCache?.InvalidateRouteTargets();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DbBacked 配置快照回写失败（刷新后的凭证将等待下轮同步）");
        }
    }

    /// <summary>
    /// DB 支撑形态的自愈：宿主启动初期（CodeFirst 建表前）读库会报 no such table——
    /// 此时补一次 Schema 初始化再继续，避免把空快照固化进元数据缓存（AllInOne 首启竞态同理）。
    /// </summary>
    private static void EnsureSchema(AppDbContext db)
    {
        try
        {
            db.Client.Queryable<Site>().Take(1).ToList();
        }
        catch (Exception)
        {
            try
            {
                Persistence.SqlSugarSetup.InitializeDatabase(db.Client);
            }
            catch
            {
                // 初始化失败交由外层 catch 记日志返回 null。
            }
        }
    }

    public bool IsReady => true;

    public Task<bool> TryLoadFromFileAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}