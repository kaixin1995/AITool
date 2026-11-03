using AITool.Application.Networking;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using AITool.Application.Accounts;
using AITool.Application.Xai;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Xai;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

/// <summary>
/// xAI (Grok / SuperGrok) 账号额度主动查询实现（IAccountQuotaProvider，ProviderKey="xai"）。
/// <para>
/// 数据源为 grok.com 的 gRPC-web 账单端点（GetGrokCreditsConfig）：POST 空 gRPC-web 帧后
/// 用 <see cref="GrokQuotaParser"/> 启发式解析出已用百分比与重置时间，产出单窗口
/// （按重置距离命名每周/月度/Credits 额度）。任一窗口达到全局阈值时自动禁用账号与
/// 关联站点（与 Codex/Google/Kimi 巡检口径一致）。缓存的 protobuf 以 Base64 存于
/// LastQuotaRawJson。
/// </para>
/// </summary>
public sealed class XaiQuotaService : IAccountQuotaProvider
{
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly HttpClient _httpClient;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IMemoryCache _resultCache;
    private readonly IXaiOAuthClient _oauthClient;
    private readonly ILogger<XaiQuotaService> _logger;
    private readonly IQuotaRecheckScheduleStore? _scheduleStore;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。</summary>
    private static readonly KeyedAsyncLock Locks = new();

    public XaiQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        IXaiOAuthClient oauthClient,
        ILogger<XaiQuotaService> logger,
        IQuotaRecheckScheduleStore? scheduleStore = null,
        NetworkBoundOAuthClientFactory? networkFactory = null)
    {
        _networkFactory = networkFactory;
        _httpClient = httpClient;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _resultCache = resultCache;
        _oauthClient = oauthClient;
        _logger = logger;
        _scheduleStore = scheduleStore;
    }

    public string ProviderKey => "xai";

    public async Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.XaiAccounts
            .Where(a => !a.IsDeleted && !a.DisabledByFeatureToggle)
            .OrderBy(a => a.LastQuotaCheckedAt)
            .ToListAsync(cancellationToken);

        return accounts.Select(ToQuotaTarget).ToList();
    }

    public async Task<IReadOnlyList<AccountQuotaTarget>> FindByLinkedSiteIdsAsync(
        IReadOnlyList<Guid> siteIds,
        CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.XaiAccounts
            .Where(a => !a.IsDeleted && siteIds.Contains(a.LinkedSiteId))
            .ToListAsync(cancellationToken);
        return accounts.Select(ToQuotaTarget).ToList();
    }

    public AccountQuotaSnapshot? ParseCachedQuota(string rawJson)
    {
        var snapshot = TryParseCached(rawJson);
        if (snapshot is null)
        {
            return null;
        }

        return new AccountQuotaSnapshot
        {
            Success = true,
            RawJson = rawJson,
            Windows = [ToQuotaWindow(snapshot)],
        };
    }

    public async Task<AccountQuotaSnapshot> QueryAsync(AccountQuotaTarget account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var current = (await _dbContext.XaiAccounts
            .Where(a => a.Id == account.AccountId && !a.IsDeleted)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();

        if (current is null)
        {
            return new AccountQuotaSnapshot
            {
                Success = false,
                Error = "账号不存在",
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var info = await QueryAsync(current, forceRefresh, cancellationToken);
        return ToQuotaSnapshot(info);
    }

    public async Task SetEnabledAsync(AccountQuotaTarget account, bool enabled, string reason, CancellationToken cancellationToken)
    {
        if ((enabled && string.Equals(reason, "quota-recovered", StringComparison.OrdinalIgnoreCase)) ||
            (!enabled && string.Equals(reason, "quota-threshold", StringComparison.OrdinalIgnoreCase)))
        {
            await QuotaAccountStateWriter.ChangeAsync(_dbContext, ProviderKey, account.AccountId, enabled,
                _scheduleStore, _logger, cancellationToken);
            _metadataCache.InvalidateRouteTargets();
            return;
        }

        if (enabled)
        {
            await QuotaAccountStateWriter.EnableAsync(_dbContext, ProviderKey, account.AccountId,
                string.Equals(reason, "feature-toggle-on", StringComparison.OrdinalIgnoreCase), false, cancellationToken);
            _metadataCache.InvalidateRouteTargets();
            return;
        }

        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var current = (await client.Queryable<XaiAccount>()
            .Where(a => a.Id == account.AccountId)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();
        if (current is null || current.IsDeleted) return;

        if (enabled)
        {
            if (current.RequiresReauth) return;
            current.IsEnabled = true;
            if (string.Equals(reason, "feature-toggle-on", StringComparison.OrdinalIgnoreCase))
            {
                current.DisabledByFeatureToggle = false;
            }
        }
        else
        {
            current.IsEnabled = false;
            if (string.Equals(reason, "feature-toggle-off", StringComparison.OrdinalIgnoreCase))
            {
                current.DisabledByFeatureToggle = account.IsEnabled;
            }
        }

        await client.Updateable(current)
            .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
            .ExecuteCommandAsync(cancellationToken);
        await SetLinkedSiteEnabledAsync(client, current.LinkedSiteId, enabled, cancellationToken);
        _metadataCache.InvalidateRouteTargets();
    }

    public async Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var accounts = await client.Queryable<XaiAccount>()
            .Where(a => !a.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            if (enabled)
            {
                await QuotaAccountStateWriter.EnableAsync(_dbContext, ProviderKey, account.Id,
                    true, true, cancellationToken);
                await client.Updateable<XaiAccount>()
                    .SetColumns(x => new XaiAccount { DisabledByFeatureToggle = false })
                    .Where(x => x.Id == account.Id && x.RequiresReauth && x.DisabledByFeatureToggle)
                    .ExecuteCommandAsync(cancellationToken);
                continue;
            }

            if (!enabled)
            {
                account.DisabledByFeatureToggle = account.IsEnabled;
                account.IsEnabled = false;
                await client.Updateable(account)
                    .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
                    .ExecuteCommandAsync(cancellationToken);
                await SetLinkedSiteEnabledAsync(client, account.LinkedSiteId, false, cancellationToken);
            }
        }

        _metadataCache.InvalidateRouteTargets();
    }

    /// <summary>手动「刷新额度」入口：强制实时查询。</summary>
    public async Task<AccountQuotaSnapshot> ForceRefreshAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var target = (await GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.AccountId == accountId);
        if (target is null)
        {
            return new AccountQuotaSnapshot { Success = false, Error = "账号不存在", CheckedAt = DateTimeOffset.UtcNow };
        }

        return await QueryAsync(target, forceRefresh: true, cancellationToken);
    }

    private async Task<QuotaQueryResult> QueryAsync(XaiAccount account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cacheKey = "xai-quota-" + account.Id.ToString("N");
        if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out QuotaQueryResult? cached) && cached != null)
        {
            return cached;
        }

        using (await Locks.WaitAsync(account.Id.ToString("N"), cancellationToken))
        {
            if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                return cached;
            }

            var info = await QueryUpstreamAsync(account, cancellationToken);

            if (info.Success)
            {
                try
                {
                    using var writeClient = _dbContext.Client.CopyNew();
                    writeClient.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
                    account.LastQuotaRawJson = info.RawJson;
                    account.LastQuotaCheckedAt = DateTimeOffset.UtcNow;
                    await writeClient.Updateable(account)
                        .UpdateColumns(x => new { x.LastQuotaRawJson, x.LastQuotaCheckedAt })
                        .ExecuteCommandAsync(cancellationToken);

                    var runtime = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
                    if (account.IsEnabled)
                    {
                        var maxPercent = GetMaxUsedPercent(info);
                        var threshold = (double)runtime.OAuthAutoDisableThresholdPercent;
                        if (maxPercent.HasValue && maxPercent.Value >= threshold)
                        {
                            await DisableAccountAsync(account, cancellationToken,
                                $"额度使用 {maxPercent.Value:F1}% 达到全局阈值 {threshold}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Persist xai quota result failed for account {Id}", account.Id);
                }
            }

            // Authentication failures must not survive a successful re-login in the quota cache.
            if (!info.CredentialInvalid && !info.UpstreamUnauthorized) _resultCache.Set(cacheKey, info, ResultCacheTtl);
            else _resultCache.Remove(cacheKey);
            return info;
        }
    }

    private async Task<QuotaQueryResult> QueryUpstreamAsync(XaiAccount account, CancellationToken ct)
    {
        if (account.RequiresReauth)
        {
            // refresh_token 已被上游拒绝：不打上游（旧 access_token 必然 401），
            // 直接确定性报错引导重新登录，巡检不消耗上游配额。
            return new QuotaQueryResult
            {
                Success = false,
                Error = "登录已失效，请重新登录",
                CredentialInvalid = true,
            };
        }

        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return new QuotaQueryResult { Success = false, Error = "账号无 access_token" };
        }

        try
        {
            // 空 gRPC-web 帧：1 字节 flags + 4 字节大端长度 0。
            using var request = new HttpRequestMessage(HttpMethod.Post, XaiConstants.GrokBillingEndpoint)
            {
                Content = new ByteArrayContent([0, 0, 0, 0, 0])
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken.Trim());
            request.Headers.TryAddWithoutValidation("Origin", "https://grok.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://grok.com/?_s=usage");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
            request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
            request.Headers.TryAddWithoutValidation("x-user-agent", "connect-es/2.1.1");
            request.Headers.TryAddWithoutValidation("User-Agent", "AITool");

            using var network = _networkFactory?.CreateForAccount(new AccountIdentity("xai", account.Id), "quota");
            if (network is not null) network.HttpClient.Timeout = _httpClient.Timeout;
            using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_httpClient.Timeout != Timeout.InfiniteTimeSpan) requestBudget.CancelAfter(_httpClient.Timeout);
            using var response = await (network?.HttpClient ?? _httpClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestBudget.Token);
            var status = response.StatusCode;

            if (status == System.Net.HttpStatusCode.Unauthorized)
            {
                // Only the credential actually sent can be invalidated. A re-login or token
                // refresh may have published a newer credential while this request was in flight.
                var invalidated = await InvalidateCredentialAsync(account, ct);
                return new QuotaQueryResult
                {
                    Success = false,
                    Error = invalidated ? "登录已失效（上游 HTTP 401），请重新登录" : "上游 HTTP 401，账号凭证已更新，请重试",
                    CredentialInvalid = invalidated,
                    UpstreamUnauthorized = true,
                };
            }

            if (status == System.Net.HttpStatusCode.Forbidden)
            {
                return new QuotaQueryResult { Success = false, Error = "上游拒绝访问（HTTP 403）" };
            }

            // HTTP 408 与 grpc-status 4 同为服务端超时，按瞬时失败传播（上层重试并保留上次成功值）。
            if (status == System.Net.HttpStatusCode.RequestTimeout)
            {
                throw new InvalidOperationException($"Transient HTTP failure (HTTP {(int)status})");
            }

            // gRPC 错误可能在 HTTP 头里携带（trailers-only 响应），先于响应体检查。
            if (TryGetGrpcStatus(response.Headers, out var headerStatus, out var headerMessage) && headerStatus != 0)
            {
                return GrpcStatusFailure(headerStatus, headerMessage);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(requestBudget.Token);
                return new QuotaQueryResult { Success = false, Error = $"API 错误 (HTTP {(int)status})：{Truncate(body)}" };
            }

            var raw = await response.Content.ReadAsByteArrayAsync(requestBudget.Token);

            // 帧内 trailer 携带 grpc-status 时优先判定。
            var trailers = GrokQuotaParser.ParseGrpcWebTrailers(raw);
            if (trailers.TryGetValue("grpc-status", out var trailerStatus)
                && long.TryParse(trailerStatus, out var trailerCode)
                && trailerCode != 0)
            {
                var trailerMessage = trailers.TryGetValue("grpc-message", out var msg) ? msg : string.Empty;
                return GrpcStatusFailure(trailerCode, trailerMessage);
            }

            var snapshot = GrokQuotaParser.Parse(raw, DateTimeOffset.UtcNow);
            var base64 = Convert.ToBase64String(raw);

            return new QuotaQueryResult
            {
                Success = true,
                RawJson = base64,
                Windows = [CreateWindow(snapshot)],
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new QuotaQueryResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<bool> InvalidateCredentialAsync(XaiAccount account, CancellationToken ct)
    {
        var accountId = account.Id;
        var accessToken = account.AccessToken;
        var refreshToken = account.RefreshToken;
        var lastRefreshAt = account.LastRefreshAt;
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        try
        {
            var update = client.Updateable<XaiAccount>()
                .SetColumns(a => new XaiAccount { RequiresReauth = true, IsEnabled = false })
                .Where(a => a.Id == accountId && !a.IsDeleted && a.AccessToken == accessToken);
            update = refreshToken is null
                ? update.Where(a => a.RefreshToken == null)
                : update.Where(a => a.RefreshToken == refreshToken);
            update = lastRefreshAt is null
                ? update.Where(a => a.LastRefreshAt == null)
                : update.Where(a => a.LastRefreshAt == lastRefreshAt);
            var changed = await update.ExecuteCommandAsync(ct);
            if (changed == 0)
            {
                await client.Ado.CommitTranAsync();
                return false;
            }

            var current = await client.Queryable<XaiAccount>().InSingleAsync(accountId);
            if (current.LinkedSiteId != Guid.Empty)
            {
                await client.Updateable<Site>().SetColumns(s => new Site { IsEnabled = false })
                    .Where(s => s.Id == current.LinkedSiteId)
                    .ExecuteCommandAsync(ct);
            }
            await client.Ado.CommitTranAsync();
        }
        catch
        {
            await client.Ado.RollbackTranAsync();
            throw;
        }

        _metadataCache.InvalidateRouteTargets();
        return true;
    }

    /// <summary>gRPC 应用层错误：internal(13)/unavailable(14) 等基础设施错误按瞬时传播，其余确定性失败。</summary>
    private static QuotaQueryResult GrpcStatusFailure(long code, string message)
        => code is 13 or 14
            ? throw new InvalidOperationException($"gRPC 瞬时错误 {code}: {message}")
            : new QuotaQueryResult { Success = false, Error = $"gRPC 错误 {code}: {message}" };

    private static bool TryGetGrpcStatus(HttpResponseHeaders headers, out long status, out string message)
    {
        status = 0;
        message = string.Empty;
        if (headers.TryGetValues("grpc-status", out var statusValues)
            && long.TryParse(statusValues.FirstOrDefault(), out var parsed))
        {
            status = parsed;
        }
        if (headers.TryGetValues("grpc-message", out var messageValues))
        {
            message = GrokQuotaParser.PercentDecode(messageValues.FirstOrDefault() ?? string.Empty);
        }
        return headers.Contains("grpc-status");
    }

    private static AccountQuotaWindow CreateWindow(GrokQuotaParser.BillingSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var label = GrokQuotaParser.DescribeWindow(snapshot.ResetsAtSeconds, now.ToUnixTimeSeconds());
        var resetAt = snapshot.ResetsAtSeconds is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : (DateTimeOffset?)null;

        return new AccountQuotaWindow
        {
            Id = "grok_credits",
            Label = label,
            UsedPercent = snapshot.UsedPercent,
            ResetLabel = resetAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "N/A",
            ResetAtUtc = resetAt,
        };
    }

    private static AccountQuotaWindow ToQuotaWindow(GrokQuotaParser.BillingSnapshot snapshot)
        => CreateWindow(snapshot);

    private static double? GetMaxUsedPercent(QuotaQueryResult info)
        => info.Windows.Count == 0 ? null : info.Windows.Max(w => w.UsedPercent);

    /// <summary>解析缓存的 Base64 protobuf；失败（无缓存/损坏）返回 null。</summary>
    private static GrokQuotaParser.BillingSnapshot? TryParseCached(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return GrokQuotaParser.Parse(Convert.FromBase64String(rawJson), DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Truncate(string text, int maxLength = 400)
        => string.IsNullOrEmpty(text) || text.Length <= maxLength ? text : text[..maxLength] + "…";

    private sealed record QuotaQueryResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public bool CredentialInvalid { get; init; }
        public bool UpstreamUnauthorized { get; init; }
        public string RawJson { get; init; } = string.Empty;
        public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
        public IReadOnlyList<AccountQuotaWindow> Windows { get; init; } = [];
    }

    private static AccountQuotaTarget ToQuotaTarget(XaiAccount account) => new()
    {
        ProviderKey = "xai",
        AccountId = account.Id,
        DisplayName = account.DisplayName,
        LinkedSiteId = account.LinkedSiteId,
        IsEnabled = account.IsEnabled,
        IsQuotaCooling = false,
        DisabledByFeatureToggle = account.DisabledByFeatureToggle,
        ManuallyDisabled = account.ManuallyDisabled,
        DisabledByUpstream = account.RequiresReauth,
        TokenExpiresAt = account.TokenExpiresAt,
        LastQuotaCheckedAt = account.LastQuotaCheckedAt,
        LastQuotaRawJson = account.LastQuotaRawJson,
    };

    private static AccountQuotaSnapshot ToQuotaSnapshot(QuotaQueryResult info) => new()
    {
        Success = info.Success,
        Error = info.Error,
        UpstreamUnauthorized = info.UpstreamUnauthorized,
        PlanType = "SuperGrok",
        RawJson = info.RawJson,
        CheckedAt = info.CheckedAt,
        Windows = info.Windows,
    };

    private static async Task SetLinkedSiteEnabledAsync(
        SqlSugar.ISqlSugarClient client,
        Guid linkedSiteId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var site = await client.Queryable<Site>().InSingleAsync(linkedSiteId);
        if (site is null || site.IsEnabled == enabled) return;

        site.IsEnabled = enabled;
        await client.Updateable(site).UpdateColumns(x => new { x.IsEnabled }).ExecuteCommandAsync(cancellationToken);
    }

    private async Task DisableAccountAsync(XaiAccount account, CancellationToken ct, string reason)
    {
        await SetEnabledAsync(ToQuotaTarget(account), false, "quota-threshold", ct);
        _logger.LogWarning("Xai account {Id} auto-disabled: {Reason}", account.Id, reason);
    }
}
