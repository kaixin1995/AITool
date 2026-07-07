using System.Text.Json;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// Codex 额度被动冷却与重置实现。位于 Web 层（依赖 ProxyRequestMetadataCache + AppDbContext）。
/// 错误判定逻辑移植自 CPA（reference-projects/CLIProxyAPI/internal/runtime/executor/codex_executor.go）：
/// 仅 usage_limit_reached 计入冷却；普通 rate_limit_exceeded 是瞬时限流，不计入。
/// </summary>
public sealed class CodexQuotaCooldownService : ICodexQuotaCooldownService
{
    /// <summary>无明确恢复时间时的默认冷却时长。</summary>
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ICodexOAuthClient _oauth;
    private readonly ILogger<CodexQuotaCooldownService> _logger;

    public CodexQuotaCooldownService(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        ICodexOAuthClient oauth,
        ILogger<CodexQuotaCooldownService> logger)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _oauth = oauth;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryApplyCooldownFromErrorAsync(int httpStatus, string? responseBody, Guid linkedSiteId, CancellationToken ct)
    {
        if (!IsUsageLimitError(httpStatus, responseBody, out var coolingUntil) || coolingUntil == null)
        {
            return false;
        }

        var account = (await _dbContext.CodexAccounts
            .Where(a => a.LinkedSiteId == linkedSiteId)
            .ToListAsync(ct)).FirstOrDefault();
        if (account == null)
        {
            // 非 Codex Site，不处理
            return false;
        }

        account.IsQuotaCooling = true;
        account.QuotaCoolingUntil = coolingUntil;
        await _dbContext.UpdateAsync(account, ct);

        var site = await _dbContext.Sites.InSingleAsync(linkedSiteId);
        if (site != null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await _dbContext.UpdateAsync(site, ct);
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateCodexAccounts();
        }

        _logger.LogWarning("Codex account {Id} cooling until {Until}", account.Id, coolingUntil);
        return true;
    }

    /// <inheritdoc />
    public async Task ResetAsync(Guid codexAccountId, CancellationToken ct)
    {
        var account = (await _dbContext.CodexAccounts
            .Where(a => a.Id == codexAccountId)
            .ToListAsync(ct)).FirstOrDefault()
            ?? throw new InvalidOperationException($"Codex account {codexAccountId} not found");

        // 1. 刷新 token（确保用新 token 重试，避免旧 token 仍触发限制）
        if (!string.IsNullOrEmpty(account.RefreshToken))
        {
            try
            {
                var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, ct);
                account.AccessToken = tokens.AccessToken;
                account.RefreshToken = tokens.RefreshToken; // 兼容轮换
                if (!string.IsNullOrEmpty(tokens.IdToken)) account.IdToken = tokens.IdToken;
                account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
                account.LastRefreshAt = DateTimeOffset.UtcNow;

                var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
                if (site != null)
                {
                    site.ApiKey = tokens.AccessToken;
                    await _dbContext.UpdateAsync(site, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token refresh during quota reset failed for account {Id}", codexAccountId);
                // 刷新失败不阻断重置（仍清冷却恢复，转发时若 token 失效再处理）
            }
        }

        // 2. 清冷却 + 恢复启用
        account.IsQuotaCooling = false;
        account.QuotaCoolingUntil = null;
        account.IsEnabled = true;
        await _dbContext.UpdateAsync(account, ct);

        // 3. 恢复 Site
        var linkedSite = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
        if (linkedSite != null && !linkedSite.IsEnabled)
        {
            linkedSite.IsEnabled = true;
            await _dbContext.UpdateAsync(linkedSite, ct);
        }

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateCodexAccounts();
        _logger.LogInformation("Codex account {Id} quota reset", codexAccountId);
    }

    /// <summary>
    /// 判定是否为 Codex usage limit 错误（照搬 CPA isCodexUsageLimitError + parseCodexRetryAfter）。
    /// 仅匹配 error.type == "usage_limit_reached"（普通 rate_limit_exceeded 不计入冷却）。
    /// </summary>
    private static bool IsUsageLimitError(int httpStatus, string? body, out DateTimeOffset? coolingUntil)
    {
        coolingUntil = null;
        if (httpStatus != 429 && httpStatus != 402) return false;

        try
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var err)) return false;
            if (!err.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return false;
            if (!string.Equals(typeEl.GetString(), "usage_limit_reached", StringComparison.OrdinalIgnoreCase)) return false;

            // 解析 resets_at（unix）或 resets_in_seconds
            if (err.TryGetProperty("resets_at", out var atEl) && atEl.ValueKind == JsonValueKind.Number && atEl.TryGetInt64(out var unix))
            {
                coolingUntil = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            else if (err.TryGetProperty("resets_in_seconds", out var secsEl) && secsEl.TryGetInt64(out var secs))
            {
                coolingUntil = DateTimeOffset.UtcNow.AddSeconds(secs);
            }
            else
            {
                coolingUntil = DateTimeOffset.UtcNow.Add(DefaultCooldown);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
