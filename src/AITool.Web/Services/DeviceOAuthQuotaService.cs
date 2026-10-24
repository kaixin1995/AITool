using AITool.Application.Accounts;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

/// <summary>
/// 通用轮询式 OAuth 账号额度服务（IAccountQuotaProvider，ProviderKey="device-oauth"）：
/// 按 VendorKey 分派到厂商定义查询；凭证失效自动刷新一次再查；结果落库缓存。
/// 多厂商共用一个提供程序，巡检/UI/特性开关零改动接入。
/// </summary>
public sealed class DeviceOAuthQuotaService : IAccountQuotaProvider
{
    public const string ProviderKey = "device-oauth";
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly KeyedAsyncLock Locks = new();

    private readonly AppDbContext _dbContext;
    private readonly DeviceOAuthVendorRegistry _registry;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IMemoryCache _resultCache;
    private readonly DeviceOAuthCredentialRefreshService _credentialRefresh;
    private readonly ILogger<DeviceOAuthQuotaService> _logger;

    public DeviceOAuthQuotaService(
        AppDbContext dbContext,
        DeviceOAuthVendorRegistry registry,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        DeviceOAuthCredentialRefreshService credentialRefresh,
        ILogger<DeviceOAuthQuotaService> logger)
    {
        _dbContext = dbContext;
        _registry = registry;
        _metadataCache = metadataCache;
        _resultCache = resultCache;
        _credentialRefresh = credentialRefresh;
        _logger = logger;
    }

    string IAccountQuotaProvider.ProviderKey => ProviderKey;

    public async Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.DeviceOAuthAccounts
            .Where(a => !a.IsDeleted && !a.DisabledByFeatureToggle)
            .OrderBy(a => a.LastQuotaCheckedAt)
            .ToListAsync(cancellationToken);
        return accounts.Select(ToTarget).ToList();
    }

    public AccountQuotaSnapshot? ParseCachedQuota(string rawJson)
    {
        // 快照按厂商格式存储，逐厂商尝试解析。
        foreach (var vendor in _registry.All)
        {
            var parsed = vendor.ParseCachedQuota(rawJson);
            if (parsed is not null)
            {
                return ToSnapshot(parsed);
            }
        }

        return null;
    }

    public async Task<AccountQuotaSnapshot> QueryAsync(AccountQuotaTarget account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cacheKey = "device-oauth-quota-" + account.AccountId.ToString("N");
        if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out AccountQuotaSnapshot? cached) && cached != null)
        {
            return cached;
        }

        using (await Locks.WaitAsync(account.AccountId.ToString("N"), cancellationToken))
        {
            if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                return cached;
            }

            var snapshot = await QueryCoreAsync(account, allowTokenRefresh: true, cancellationToken);

            if (snapshot.Success && !string.IsNullOrEmpty(snapshot.RawJson))
            {
                try
                {
                    using var client = _dbContext.Client.CopyNew();
                    client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
                    await client.Updateable<Domain.OAuth.DeviceOAuthAccount>()
                        .SetColumns(a => new DeviceOAuthAccount
                        {
                            LastQuotaRawJson = snapshot.RawJson,
                            LastQuotaCheckedAt = DateTimeOffset.UtcNow,
                        })
                        .Where(a => a.Id == account.AccountId)
                        .ExecuteCommandAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Persist device-oauth quota result failed for {Id}", account.AccountId);
                }
            }

            _resultCache.Set(cacheKey, snapshot, ResultCacheTtl);
            return snapshot;
        }
    }

    private async Task<AccountQuotaSnapshot> QueryCoreAsync(AccountQuotaTarget account, bool allowTokenRefresh, CancellationToken ct)
    {
        var current = (await _dbContext.DeviceOAuthAccounts
            .Where(a => a.Id == account.AccountId && !a.IsDeleted)
            .ToListAsync(ct))
            .FirstOrDefault();
        if (current is null)
        {
            return new AccountQuotaSnapshot { Success = false, Error = "账号不存在", CheckedAt = DateTimeOffset.UtcNow };
        }

        if (current.RequiresReauth)
        {
            return new AccountQuotaSnapshot
            {
                Success = false,
                Error = "refresh_token 已失效，请重新登录",
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var vendor = _registry.Find(current.VendorKey);
        if (vendor is null)
        {
            return new AccountQuotaSnapshot { Success = false, Error = $"未知厂商 {current.VendorKey}", CheckedAt = DateTimeOffset.UtcNow };
        }

        if (string.IsNullOrWhiteSpace(current.AccessToken))
        {
            return new AccountQuotaSnapshot { Success = false, Error = "账号无 access_token", CheckedAt = DateTimeOffset.UtcNow };
        }

        var token = new DeviceOAuthTokenSet
        {
            AccessToken = current.AccessToken!,
            RefreshToken = current.RefreshToken,
            ExpiresAt = current.TokenExpiresAt,
        };

        var result = await vendor.QueryQuotaAsync(token, current.ExtraJson, ct);
        if (result.CredentialInvalid && allowTokenRefresh && !string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            var refreshed = await _credentialRefresh.RefreshAsync(current.LinkedSiteId, current.AccessToken!, ct);
            if (!string.IsNullOrWhiteSpace(refreshed))
            {
                token = token with { AccessToken = refreshed };
                result = await vendor.QueryQuotaAsync(token, current.ExtraJson, ct);
            }
        }

        return ToSnapshot(result);
    }

    public async Task SetEnabledAsync(AccountQuotaTarget account, bool enabled, string reason, CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var current = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(account.AccountId);
        if (current is null || current.IsDeleted) return;

        if (enabled)
        {
            current.IsEnabled = true;
            if (reason is "quota-recovered") current.ManuallyDisabled = false;
            if (reason is "feature-toggle-on") current.DisabledByFeatureToggle = false;
        }
        else
        {
            current.IsEnabled = false;
            if (reason is "feature-toggle-off") current.DisabledByFeatureToggle = account.IsEnabled;
        }

        await client.Updateable(current)
            .UpdateColumns(x => new { x.IsEnabled, x.ManuallyDisabled, x.DisabledByFeatureToggle })
            .ExecuteCommandAsync(cancellationToken);

        if (current.LinkedSiteId != Guid.Empty)
        {
            var site = await client.Queryable<Site>().InSingleAsync(current.LinkedSiteId);
            if (site is not null && site.IsEnabled != enabled)
            {
                site.IsEnabled = enabled;
                await client.Updateable(site).UpdateColumns(s => new { s.IsEnabled }).ExecuteCommandAsync(cancellationToken);
            }
        }

        _metadataCache.InvalidateRouteTargets();
    }

    public async Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var accounts = await client.Queryable<DeviceOAuthAccount>().Where(a => !a.IsDeleted).ToListAsync(cancellationToken);
        foreach (var account in accounts)
        {
            // 对齐 Kimi 语义：站点启停只跟随「账号状态分支」，绝不能在重开总开关时
            // 把被额度巡检禁用（非开关原因）的账号站点一并启用。
            if (!enabled)
            {
                account.DisabledByFeatureToggle = account.IsEnabled;
                account.IsEnabled = false;
                await client.Updateable(account)
                    .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
                    .ExecuteCommandAsync(cancellationToken);
                await SetSiteEnabledAsync(client, account.LinkedSiteId, false, cancellationToken);
            }
            else if (account.DisabledByFeatureToggle && !account.ManuallyDisabled)
            {
                account.IsEnabled = true;
                account.DisabledByFeatureToggle = false;
                await client.Updateable(account)
                    .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
                    .ExecuteCommandAsync(cancellationToken);
                await SetSiteEnabledAsync(client, account.LinkedSiteId, true, cancellationToken);
            }
        }

        _metadataCache.InvalidateRouteTargets();
    }


    /// <summary>空站点守卫的站点启停（LinkedSiteId=Empty 的账号直接跳过）。</summary>
    private static async Task SetSiteEnabledAsync(
        SqlSugar.ISqlSugarClient client,
        Guid linkedSiteId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (linkedSiteId == Guid.Empty)
        {
            return;
        }

        var site = await client.Queryable<Site>().InSingleAsync(linkedSiteId);
        if (site is not null && site.IsEnabled != enabled)
        {
            site.IsEnabled = enabled;
            await client.Updateable(site).UpdateColumns(s => new { s.IsEnabled }).ExecuteCommandAsync(cancellationToken);
        }
    }

    /// <summary>手动刷新入口。</summary>
    public async Task<AccountQuotaSnapshot> ForceRefreshAsync(Guid accountId, CancellationToken ct)
    {
        var target = (await GetAccountsAsync(ct)).FirstOrDefault(a => a.AccountId == accountId);
        if (target is null)
        {
            return new AccountQuotaSnapshot { Success = false, Error = "账号不存在", CheckedAt = DateTimeOffset.UtcNow };
        }

        return await QueryAsync(target, forceRefresh: true, ct);
    }

    private static AccountQuotaTarget ToTarget(DeviceOAuthAccount account) => new()
    {
        ProviderKey = ProviderKey,
        AccountId = account.Id,
        DisplayName = account.DisplayName,
        LinkedSiteId = account.LinkedSiteId,
        IsEnabled = account.IsEnabled,
        IsQuotaCooling = false,
        DisabledByFeatureToggle = account.DisabledByFeatureToggle,
        ManuallyDisabled = account.ManuallyDisabled,
        DisabledByUpstream = false,
        TokenExpiresAt = account.TokenExpiresAt,
        LastQuotaCheckedAt = account.LastQuotaCheckedAt,
        LastQuotaRawJson = account.LastQuotaRawJson,
    };

    private static AccountQuotaSnapshot ToSnapshot(DeviceOAuthQuotaResult result) => new()
    {
        Success = result.Success,
        Error = result.Error,
        PlanType = result.PlanType,
        RawJson = result.RawJson,
        CheckedAt = DateTimeOffset.UtcNow,
        Windows = result.Windows,
    };
}
