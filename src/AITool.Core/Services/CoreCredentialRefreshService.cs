using AITool.Application.Codex;
using AITool.Application.CoreRuntime;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Proxy;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;

namespace AITool.Core.Services;

// —— Core 侧托管凭证即时刷新（无数据库版本，split 双宿主）——
// master 单体在 Web 进程内刷新并写库；Core 无库，这里做三件事：
// ① 纯 HTTP 刷新（复用 Infrastructure 的 OAuth 客户端）+ 静态 KeyedAsyncLock 单飞；
// ② 新 token 立即回写本地运行时配置快照（Site.ApiKey + AccountCredentials.RefreshToken）并失效路由缓存，
//    使后续请求不再吃 401；
// ③ 发布 credential-refreshed / credential-disabled 事件，Admin 侧摄取后持久化账号表与隐藏站点并全量同步。

/// <summary>
/// 共享刷新引擎：三厂商的刷新/禁用链路在此实现，三个同名门面类供代理控制器注入。
/// </summary>
public sealed class CoreCredentialRefreshEngine
{
    /// <summary>
    /// 按 Provider+SiteId 单飞：同一站点的并发 401 只触发一次真实上游刷新。
    /// static 保证跨 scoped 控制器实例生效（与 master 侧实现口径一致）。
    /// </summary>
    private static readonly KeyedAsyncLock RefreshLocks = new();

    private readonly ICoreRuntimeConfigProvider _configProvider;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly CoreAdminEventBus _eventBus;
    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly ICodexOAuthClient _codexOAuth;
    private readonly IGoogleOAuthClient _googleOAuth;
    private readonly IKimiOAuthClient _kimiOAuth;

    public CoreCredentialRefreshEngine(
        ICoreRuntimeConfigProvider configProvider,
        ProxyRequestMetadataCache metadataCache,
        CoreAdminEventBus eventBus,
        CoreEventSequenceProvider sequenceProvider,
        ICodexOAuthClient codexOAuth,
        IGoogleOAuthClient googleOAuth,
        IKimiOAuthClient kimiOAuth)
    {
        _configProvider = configProvider;
        _metadataCache = metadataCache;
        _eventBus = eventBus;
        _sequenceProvider = sequenceProvider;
        _codexOAuth = codexOAuth;
        _googleOAuth = googleOAuth;
        _kimiOAuth = kimiOAuth;
    }

    /// <summary>
    /// 刷新托管凭证。返回新 access token；无法刷新（无凭证/上游失败/他人已刷新）返回 null。
    /// </summary>
    public async Task<string?> RefreshAsync(string provider, Guid siteId, string? staleToken, CancellationToken cancellationToken)
    {
        var snapshot = _configProvider.GetCurrent();
        var credential = snapshot?.AccountCredentials?.FirstOrDefault(
            x => x.LinkedSiteId == siteId && string.Equals(x.Provider, provider, StringComparison.OrdinalIgnoreCase));
        if (credential is null || string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            return null;
        }

        var lockKey = $"{provider}:{siteId:N}";
        using (await RefreshLocks.WaitAsync(lockKey, cancellationToken))
        {
            // 双检：单飞等待期间他人可能已刷新（快照已更新），直接复用。
            snapshot = _configProvider.GetCurrent();
            credential = snapshot?.AccountCredentials?.FirstOrDefault(
                x => x.LinkedSiteId == siteId && string.Equals(x.Provider, provider, StringComparison.OrdinalIgnoreCase));
            if (credential is null || string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                return null;
            }

            var site = snapshot?.Sites?.FirstOrDefault(s => s.Id == siteId);
            if (site is null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(staleToken)
                && !string.Equals(site.ApiKey, staleToken, StringComparison.Ordinal))
            {
                // 传入的过期 token 与快照不一致：说明已有人刷新过，直接返回当前 token。
                return site.ApiKey;
            }

            string newAccessToken;
            string? newRefreshToken = null;
            try
            {
                switch (provider)
                {
                    case "Codex":
                        var codexTokens = await _codexOAuth.RefreshTokenAsync(credential.RefreshToken, cancellationToken);
                        newAccessToken = codexTokens.AccessToken;
                        newRefreshToken = codexTokens.RefreshToken;
                        break;
                    case "Google":
                        var googleTokens = await _googleOAuth.RefreshTokenAsync(
                            credential.AccountKind ?? "Antigravity", credential.RefreshToken, cancellationToken);
                        newAccessToken = googleTokens.AccessToken;
                        // Google 会轮换 refresh_token；上游未返回时沿用旧值。
                        newRefreshToken = googleTokens.RefreshToken ?? credential.RefreshToken;
                        break;
                    default:
                        var kimiTokens = await _kimiOAuth.RefreshTokenAsync(credential.RefreshToken, credential.DeviceId, cancellationToken);
                        newAccessToken = kimiTokens.AccessToken;
                        newRefreshToken = kimiTokens.RefreshToken;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 刷新失败：返回 null 让本次转发按 401 失败并回退下一路由，不影响其他账号。
                return null;
            }

            if (string.IsNullOrWhiteSpace(newAccessToken))
            {
                return null;
            }

            // ② 立即回写本地快照（内存操作，无 IO），并失效路由缓存让后续请求拿到新 token。
            site.ApiKey = newAccessToken;
            credential.RefreshToken = newRefreshToken ?? credential.RefreshToken;
            _configProvider.SetCurrent(snapshot!);
            _metadataCache.InvalidateRouteTargets();

            // ③ 发布事件给 Admin 持久化（fire-and-forget：spool 机制保证不丢）。
            var payload = new CoreCredentialRefreshedEvent
            {
                Provider = provider,
                AccountId = credential.AccountId,
                LinkedSiteId = siteId,
                NewAccessToken = newAccessToken,
                NewRefreshToken = newRefreshToken ?? string.Empty,
                RefreshedAt = DateTimeOffset.UtcNow
            };
            await _eventBus.PublishAsync(
                CoreAdminEventEnvelopeBuilder.CreateCredentialRefreshedEnvelope(_sequenceProvider.Next(), payload),
                CancellationToken.None);

            return newAccessToken;
        }
    }

    /// <summary>
    /// 禁用托管凭证（上游 403 等不可恢复错误）。Core 无库，仅发事件由 Admin 禁用账号与站点。
    /// </summary>
    public async Task DisableAsync(string provider, Guid siteId, string reason, CancellationToken cancellationToken)
    {
        var snapshot = _configProvider.GetCurrent();
        var credential = snapshot?.AccountCredentials?.FirstOrDefault(
            x => x.LinkedSiteId == siteId && string.Equals(x.Provider, provider, StringComparison.OrdinalIgnoreCase));
        if (credential is null)
        {
            return;
        }

        var payload = new CoreCredentialDisabledEvent
        {
            Provider = provider,
            AccountId = credential.AccountId,
            LinkedSiteId = siteId,
            Reason = reason,
            DisabledAt = DateTimeOffset.UtcNow
        };
        await _eventBus.PublishAsync(
            CoreAdminEventEnvelopeBuilder.CreateCredentialDisabledEnvelope(_sequenceProvider.Next(), payload),
            cancellationToken);
    }
}

/// <summary>
/// Codex 托管凭证 Core 侧刷新门面（与 master 单体的 Admin 服务同名同签名，代理控制器无感切换）。
/// </summary>
public sealed class CodexCredentialRefreshService(CoreCredentialRefreshEngine engine)
{
    public Task<string?> RefreshAsync(Guid siteId, string? staleToken, CancellationToken cancellationToken)
        => engine.RefreshAsync("Codex", siteId, staleToken, cancellationToken);
}

/// <summary>
/// Google（Antigravity）托管凭证 Core 侧刷新门面。
/// </summary>
public sealed class GoogleCredentialRefreshService(CoreCredentialRefreshEngine engine)
{
    public Task<string?> RefreshAsync(Guid siteId, string? staleToken, CancellationToken cancellationToken)
        => engine.RefreshAsync("Google", siteId, staleToken, cancellationToken);

    public Task DisableAsync(Guid siteId, string reason, CancellationToken cancellationToken)
        => engine.DisableAsync("Google", siteId, reason, cancellationToken);
}

/// <summary>
/// Kimi 托管凭证 Core 侧刷新门面。
/// </summary>
public sealed class KimiCredentialRefreshService(CoreCredentialRefreshEngine engine)
{
    public Task<string?> RefreshAsync(Guid siteId, string? staleToken, CancellationToken cancellationToken)
        => engine.RefreshAsync(KimiConstants.ManagedSource, siteId, staleToken, cancellationToken);
}
