using AITool.Application.CoreRuntime;
using AITool.Domain.Sites;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITool.AllInOne;

/// <summary>
/// AllInOne 单进程形态的 <see cref="ICoreRuntimeConfigProvider"/> 实现：
/// 快照直接来自/写回 SQLite（无跨宿主下发）。
/// 仅满足 <see cref="CoreCredentialRefreshEngine"/> 的使用面（AccountCredentials + Sites.ApiKey），
/// 不承载完整路由快照语义（AllInOne 的元数据缓存走 DB 模式）。
/// </summary>
public sealed class DbBackedRuntimeConfigProvider : ICoreRuntimeConfigProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<DbBackedRuntimeConfigProvider> _logger;

    public DbBackedRuntimeConfigProvider(
        IServiceScopeFactory scopeFactory,
        ProxyRequestMetadataCache metadataCache,
        ILogger<DbBackedRuntimeConfigProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    public CoreRuntimeConfigSnapshot? GetCurrent()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var snapshot = new CoreRuntimeConfigSnapshot
            {
                ConfigVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                GeneratedAt = DateTimeOffset.UtcNow,
                Sites = db.Sites.ToList().Select(s => new CoreRuntimeSite
                {
                    Id = s.Id,
                    Name = s.Name,
                    BaseUrl = s.BaseUrl,
                    ApiKey = s.ApiKey,
                    IsEnabled = s.IsEnabled
                }).ToList()
            };

            foreach (var account in db.CodexAccounts.ToList())
            {
                snapshot.AccountCredentials.Add(new CoreRuntimeAccountCredential
                {
                    Provider = "Codex",
                    AccountId = account.Id,
                    LinkedSiteId = account.LinkedSiteId,
                    RefreshToken = account.RefreshToken,
                    AccountKind = "Codex",
                    IsEnabled = account.IsEnabled
                });
            }

            foreach (var account in db.GoogleAccounts.ToList())
            {
                snapshot.AccountCredentials.Add(new CoreRuntimeAccountCredential
                {
                    Provider = "Google",
                    AccountId = account.Id,
                    LinkedSiteId = account.LinkedSiteId,
                    RefreshToken = account.RefreshToken,
                    AccountKind = account.AccountKind,
                    IsEnabled = account.IsEnabled
                });
            }

            foreach (var account in db.KimiAccounts.ToList())
            {
                snapshot.AccountCredentials.Add(new CoreRuntimeAccountCredential
                {
                    Provider = "Kimi",
                    AccountId = account.Id,
                    LinkedSiteId = account.LinkedSiteId,
                    RefreshToken = account.RefreshToken,
                    AccountKind = "Kimi",
                    IsEnabled = account.IsEnabled
                });
            }

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

            // 失效凭证与路由缓存，让新凭证立即对元数据缓存生效。
            _metadataCache.InvalidateAccessKeys();
            _metadataCache.InvalidateCodexAccounts();
            _metadataCache.InvalidateGoogleAccounts();
            _metadataCache.InvalidateKimiAccounts();
            _metadataCache.InvalidateRouteTargets();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DbBacked 配置快照回写失败（刷新后的凭证将等待下轮同步）");
        }
    }

    public bool IsReady => true;

    public Task<bool> TryLoadFromFileAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}