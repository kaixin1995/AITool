using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

/// <summary>
/// Codex 额度主动查询实现（位于 Web 层，因依赖 ProxyRequestMetadataCache）。
/// <para>
/// 上游额度端点待实测确认（见 ICodexQuotaService 注释）。当前实现：
/// 1) 尝试请求候选端点；2) 解析尽量宽松；3) 失败降级（Success=false，不影响账号）。
/// 端点确认后，只需补全 TryParseQuota 的解析逻辑。
/// </para>
/// </summary>
public sealed class CodexQuotaService : ICodexQuotaService
{
    // 候选端点（new-api 风格；实测后调整为真实端点）
    private const string UsageUrl = "https://chatgpt.com/backend-api/codex/usage";
    private const string UserAgent = "codex_cli_rs/0.133.0 (Mac OS 26.3.1; arm64) iTerm.app/3.6.9";

    /// <summary>结果缓存 TTL（防抖）。</summary>
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IMemoryCache _resultCache;
    private readonly ILogger<CodexQuotaService> _logger;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public CodexQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        ILogger<CodexQuotaService> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _resultCache = resultCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CodexQuotaInfo> QueryAsync(CodexAccount account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cacheKey = "codex-quota-" + account.Id.ToString("N");

        // 防抖：非强制刷新走缓存
        if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out CodexQuotaInfo? cached) && cached != null)
        {
            return cached;
        }

        // single-flight
        var gate = _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 二次检查缓存（等待期间可能已被并发填充）
            if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                return cached;
            }

            var info = await QueryUpstreamAsync(account, cancellationToken);

            // 持久化（列更新，避免覆盖并发的 token 刷新）
            try
            {
                account.LastQuotaRawJson = info.RawJson;
                account.LastQuotaCheckedAt = DateTimeOffset.UtcNow;
                await _dbContext.UpdateAsync(account, cancellationToken);

                // 自动禁用判定
                if (info.Success
                    && info.RemainingQuota.HasValue
                    && account.AutoDisableThreshold.HasValue
                    && info.RemainingQuota.Value < account.AutoDisableThreshold.Value
                    && account.IsEnabled)
                {
                    await DisableAccountAsync(account, cancellationToken,
                        $"剩余额度 {info.RemainingQuota} 低于阈值 {account.AutoDisableThreshold}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persist codex quota result failed for account {Id}", account.Id);
            }

            // 写缓存（无论成功失败都缓存 30s，避免失败风暴）
            _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return info;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CodexQuotaInfo> QueryUpstreamAsync(CodexAccount account, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.AccessToken))
        {
            return new CodexQuotaInfo { Success = false, Error = "账号无 access_token" };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            request.Headers.TryAddWithoutValidation("Originator", "codex_cli_rs");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (!string.IsNullOrEmpty(account.AccountId))
            {
                request.Headers.TryAddWithoutValidation("Chatgpt-Account-Id", account.AccountId);
            }

            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            var info = new CodexQuotaInfo { RawJson = body, CheckedAt = DateTimeOffset.UtcNow };
            if (!response.IsSuccessStatusCode)
            {
                info.Success = false;
                info.Error = $"上游返回 {(int)response.StatusCode}";
                return info;
            }

            TryParseQuota(body, info);
            info.Success = true;
            return info;
        }
        catch (Exception ex)
        {
            return new CodexQuotaInfo { Success = false, Error = ex.Message, CheckedAt = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>
    /// 宽松解析上游额度响应。端点结构确认后在此补充具体字段提取。
    /// 当前实现尝试常见字段名（remaining/used/total/quota/resets_at），找不到则留 null。
    /// </summary>
    private static void TryParseQuota(string body, CodexQuotaInfo info)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            info.RemainingQuota = TryGetDecimal(root, "remaining", "remaining_quota", "credits_remaining");
            info.UsedQuota = TryGetDecimal(root, "used", "used_quota", "credits_used");
            info.TotalQuota = TryGetDecimal(root, "total", "total_quota", "credits_total", "limit");
            info.QuotaUnit = TryGetString(root, "unit", "quota_unit");

            // resets_at：unix 秒或 ISO
            var resetStr = TryGetString(root, "resets_at", "reset_at", "resets_at_iso");
            if (!string.IsNullOrEmpty(resetStr))
            {
                if (long.TryParse(resetStr, out var unix)) info.ResetAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                else if (DateTimeOffset.TryParse(resetStr, out var dto)) info.ResetAt = dto;
            }
        }
        catch
        {
            // 解析失败不影响 Success，仅额度字段留空
        }
    }

    private static decimal? TryGetDecimal(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
                if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), out var ds)) return ds;
            }
        }
        return null;
    }

    private static string? TryGetString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private async Task DisableAccountAsync(CodexAccount account, CancellationToken ct, string reason)
    {
        account.IsEnabled = false;
        await _dbContext.UpdateAsync(account, ct);

        var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
        if (site != null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await _dbContext.UpdateAsync(site, ct);
        }

        _metadataCache.InvalidateRouteTargets();
        _logger.LogWarning("Codex account {Id} auto-disabled: {Reason}", account.Id, reason);
    }
}
