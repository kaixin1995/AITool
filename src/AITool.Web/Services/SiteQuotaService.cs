using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// 站点套餐额度编排服务：聚合全部 <see cref="ISiteQuotaProvider"/>，为站点页「额度查询」
/// Tab 提供总览（纯缓存）与按站刷新（实时查询并落库）。
/// <para>
/// 纯手动模式：只在用户进入页面或点击刷新按钮时查询，无后台巡检、无定时轮询；
/// 查询结果写入 <see cref="SiteKey"/> 的 LastQuota* 列，重启后仍可展示上次值。
/// </para>
/// </summary>
public sealed class SiteQuotaService
{
    /// <summary>按站点串行化并发刷新，避免同站并发打上游。</summary>
    private static readonly KeyedAsyncLock Locks = new();

    private readonly AppDbContext _dbContext;
    private readonly IEnumerable<ISiteQuotaProvider> _providers;
    private readonly ILogger<SiteQuotaService> _logger;

    public SiteQuotaService(
        AppDbContext dbContext,
        IEnumerable<ISiteQuotaProvider> providers,
        ILogger<SiteQuotaService> logger)
    {
        _dbContext = dbContext;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// 额度总览：列出全部支持额度查询的站点及其密钥（密钥值脱敏），数据来自上次查询的
    /// 落库缓存，不触发任何上游请求。
    /// </summary>
    public async Task<SiteQuotaOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        var keysBySite = await LoadKeysBySiteAsync(sites.Select(s => s.Id).ToList(), cancellationToken);

        var result = new List<SiteQuotaSiteInfo>();
        foreach (var site in sites)
        {
            var provider = MatchProvider(site.BaseUrl);
            if (provider is null)
            {
                continue;
            }

            keysBySite.TryGetValue(site.Id, out var keys);
            result.Add(ToSiteInfo(site, provider, keys ?? []));
        }

        return new SiteQuotaOverview(result);
    }

    /// <summary>
    /// 刷新单个站点的全部密钥额度：并发查询上游、逐 Key 落库缓存，返回刷新后的站点信息。
    /// 站点不存在或不支持额度查询时返回 null。
    /// </summary>
    public async Task<SiteQuotaSiteInfo?> RefreshSiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(siteId);
        if (site is null)
        {
            return null;
        }

        var provider = MatchProvider(site.BaseUrl);
        if (provider is null)
        {
            return null;
        }

        // 串行化同站点的并发刷新（自动进入 + 手动点击赛跑）：避免同站并发打上游。
        // 排队方等待期间不消耗结果——手动模式下用户再次触发即期望真实重查。
        using (await Locks.WaitAsync(siteId.ToString("N"), cancellationToken))
        {
            var keys = (await _dbContext.SiteKeys
                    .Where(k => k.SiteId == siteId)
                    .OrderBy(k => k.Priority)
                    .ThenBy(k => k.CreatedAt)
                    .ThenBy(k => k.Id)
                    .ToListAsync(cancellationToken))
                .ToList();

            var results = await Task.WhenAll(keys.Select(k =>
                provider.QueryAsync(site.BaseUrl, k.KeyValue, cancellationToken)));

            for (var i = 0; i < keys.Count; i++)
            {
                await PersistKeyResultAsync(keys[i], results[i], cancellationToken);
            }

            return ToSiteInfo(site, provider, keys);
        }
    }

    private ISiteQuotaProvider? MatchProvider(string baseUrl)
        => _providers.FirstOrDefault(p => p.MatchesBaseUrl(baseUrl));

    private async Task<Dictionary<Guid, List<SiteKey>>> LoadKeysBySiteAsync(
        IReadOnlyList<Guid> siteIds,
        CancellationToken cancellationToken)
    {
        if (siteIds.Count == 0)
        {
            return [];
        }

        var allKeys = await _dbContext.SiteKeys
            .Where(k => siteIds.Contains(k.SiteId))
            .ToListAsync(cancellationToken);

        return allKeys
            .GroupBy(k => k.SiteId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(k => k.Priority).ThenBy(k => k.CreatedAt).ThenBy(k => k.Id).ToList());
    }

    /// <summary>把查询结果落库：成功更新原始报文并清空错误；失败保留上次成功值（供置灰展示）。</summary>
    private async Task PersistKeyResultAsync(SiteKey key, SiteQuotaQueryResult result, CancellationToken cancellationToken)
    {
        key.LastQuotaCheckedAt = DateTimeOffset.UtcNow;
        key.LastQuotaStatus = result.Success
            ? "ok"
            : result.CredentialInvalid ? "invalid_credential" : "error";
        key.LastQuotaError = result.Success ? null : result.Error;

        if (result.Success)
        {
            key.LastQuotaRawJson = result.RawJson;
        }

        try
        {
            using var writeClient = _dbContext.Client.CopyNew();
            writeClient.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            await writeClient.Updateable(key)
                .UpdateColumns(x => new
                {
                    x.LastQuotaRawJson,
                    x.LastQuotaCheckedAt,
                    x.LastQuotaStatus,
                    x.LastQuotaError,
                })
                .ExecuteCommandAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persist site key quota result failed for key {KeyId}", key.Id);
        }
    }

    private SiteQuotaSiteInfo ToSiteInfo(Site site, ISiteQuotaProvider provider, IReadOnlyList<SiteKey> keys)
        => new(
            site.Id,
            site.Name,
            site.BaseUrl,
            provider.ProviderKey,
            provider.ProviderLabel,
            keys.Select(k => ToKeyInfo(k, provider)).ToList());

    private SiteQuotaKeyInfo ToKeyInfo(SiteKey key, ISiteQuotaProvider provider)
    {
        // 状态：从未查询（无 CheckedAt/Status）→ never；落库值异常时按 error 兜底。
        var status = key.LastQuotaCheckedAt is null || string.IsNullOrEmpty(key.LastQuotaStatus)
            ? "never"
            : key.LastQuotaStatus is "ok" or "invalid_credential" or "error"
                ? key.LastQuotaStatus
                : "error";

        // 失败时保留上次成功值（置灰展示）；成功时必然有可解析的原始报文。
        var parsed = key.LastQuotaRawJson is null ? null : provider.ParseCached(key.LastQuotaRawJson);

        return new SiteQuotaKeyInfo(
            key.Id,
            MaskApiKey(key.KeyValue),
            key.Remark,
            key.Priority,
            key.IsEnabled,
            status,
            parsed?.Level,
            key.LastQuotaError,
            key.LastQuotaCheckedAt,
            parsed?.Windows ?? []);
    }

    /// <summary>密钥脱敏：前 4 位 + *** + 后 4 位（与 SitesApiController.MaskApiKey 口径一致）。</summary>
    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return string.Empty;
        }
        if (apiKey.Length <= 8)
        {
            return "***";
        }
        return string.Concat(apiKey.AsSpan(0, 4), "***", apiKey.AsSpan(apiKey.Length - 4));
    }
}
