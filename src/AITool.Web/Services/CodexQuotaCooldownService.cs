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
    /// <remarks>当前尚未在代理转发路径接线（无调用点）；接线前写库已按列限定收敛，防热路径并发回滚。</remarks>
    public async Task<bool> TryApplyCooldownFromErrorAsync(int httpStatus, string? responseBody, Guid linkedSiteId, CancellationToken ct)
    {
        if (!IsUsageLimitError(httpStatus, responseBody, out var coolingUntil) || coolingUntil == null)
        {
            return false;
        }

        // 用 CopyNew 独立连接，不碰单例 SqlSugarScope
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        var account = (await client.Queryable<Domain.Codex.CodexAccount>()
            .Where(a => a.LinkedSiteId == linkedSiteId)
            .ToListAsync(ct)).FirstOrDefault();
        if (account == null)
        {
            return false;
        }

        account.IsQuotaCooling = true;
        account.QuotaCoolingUntil = coolingUntil;
        // 列限定写回：防整行写回滚后台刷新刚写入的 token 等列。
        await client.Updateable(account)
            .UpdateColumns(a => new { a.IsQuotaCooling, a.QuotaCoolingUntil })
            .ExecuteCommandAsync(ct);

        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(linkedSiteId);
        if (site != null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await client.Updateable(site).UpdateColumns(s => s.IsEnabled).ExecuteCommandAsync(ct);
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateCodexAccounts();
        }

        _logger.LogWarning("Codex account {Id} cooling until {Until}", account.Id, coolingUntil);
        return true;
    }

    /// <inheritdoc />
    public async Task ResetAsync(Guid codexAccountId, CancellationToken ct)
    {
        // 查询用 CopyNew 独立连接
        using var queryClient = _dbContext.Client.CopyNew();
        queryClient.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var account = (await queryClient.Queryable<Domain.Codex.CodexAccount>()
            .Where(a => a.Id == codexAccountId)
            .ToListAsync(ct)).FirstOrDefault()
            ?? throw new InvalidOperationException($"Codex account {codexAccountId} not found");

        // 1. 刷新 token（HTTP，锁外）；列限定写回——期间后台刷新/供给可能并发写其他列，整行写会互相回滚。
        // 刷新请求一旦返回，旧 refresh_token 已被上游消费：落盘段改用独立超时令牌与调用方取消隔离
        //（管理员中途关页不得丢已轮换的新 token，对齐 CodexCredentialRefreshService 的关键段范式）。
        // 写序：账号 token 五列先写、site.ApiKey 后写——中途失败的偏态必须落在"账号新/site 旧"侧
        //（首个 401 触发凭证刷新即秒级自愈），反之 site 新/账号旧 RT 已死则永久无法刷新。
        if (!string.IsNullOrEmpty(account.RefreshToken))
        {
            // 段 A：上游刷新（HTTP，随调用方取消）。
            CodexTokenSet? tokens = null;
            try
            {
                tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, ct);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Quota reset canceled before persistence for account {Id}", codexAccountId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token refresh during quota reset failed for account {Id}", codexAccountId);
            }

            // 段 B：落盘（独立 30s 超时令牌，与调用方取消隔离）。
            // 空响应不回写：避免清空有效凭证导致账号永久无法刷新（与 CodexTokenRefreshService 的防护一致）。
            if (tokens is { AccessToken: { Length: > 0 } })
            {
                using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var pct = persistCts.Token;
                try
                {
                    account.AccessToken = tokens.AccessToken;
                    // 上游可能不回传新 refresh_token（此时保留旧值，清空将导致永久无法刷新）。
                    if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
                    {
                        account.RefreshToken = tokens.RefreshToken;
                    }
                    if (!string.IsNullOrEmpty(tokens.IdToken)) account.IdToken = tokens.IdToken;
                    account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
                    account.LastRefreshAt = DateTimeOffset.UtcNow;

                    await queryClient.Updateable(account)
                        .UpdateColumns(a => new { a.AccessToken, a.RefreshToken, a.IdToken, a.TokenExpiresAt, a.LastRefreshAt })
                        .ExecuteCommandAsync(pct);

                    var site = await queryClient.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
                    if (site != null)
                    {
                        site.ApiKey = tokens.AccessToken;
                        await queryClient.Updateable(site).UpdateColumns(s => s.ApiKey).ExecuteCommandAsync(pct);
                    }
                }
                catch (OperationCanceledException ex) when (persistCts.IsCancellationRequested)
                {
                    // 仅落盘超时进入此分支（HTTP 阶段已在段 A 处理）。
                    _logger.LogWarning(ex, "Token persistence timeout during quota reset for account {Id}", codexAccountId);
                }
                catch (Exception ex)
                {
                    // 账号五列若已写成功则偏态在自愈侧；site 写失败（如 DB busy）不阻断冷却清理。
                    _logger.LogWarning(ex, "Token persistence during quota reset partially failed for account {Id}", codexAccountId);
                }
            }
        }

        using (var persistCts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                var pct2 = persistCts2.Token;

                // 2. 清冷却（列限定写回）。不越权改 IsEnabled——重置额度只负责冷却，
                //    手动禁用/阈值自动禁用/总开关禁用不因重置被拉回（与冷却恢复路径语义一致）。
                account.IsQuotaCooling = false;
                account.QuotaCoolingUntil = null;
                await queryClient.Updateable(account)
                    .UpdateColumns(a => new { a.IsQuotaCooling, a.QuotaCoolingUntil })
                    .ExecuteCommandAsync(pct2);

                // 3. 恢复 Site：写前重读实时启用状态，被禁用的账号不拉回路由。
                var currentEnabled = (await queryClient.Queryable<Domain.Codex.CodexAccount>()
                    .Where(a => a.Id == codexAccountId)
                    .Select(a => a.IsEnabled)
                    .ToListAsync(pct2)).FirstOrDefault();
                if (currentEnabled)
                {
                    var linkedSite = await queryClient.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
                    if (linkedSite != null && !linkedSite.IsEnabled)
                    {
                        linkedSite.IsEnabled = true;
                        await queryClient.Updateable(linkedSite).UpdateColumns(s => s.IsEnabled).ExecuteCommandAsync(pct2);
                    }
                }
            }
            catch (OperationCanceledException ex) when (persistCts2.IsCancellationRequested)
            {
                // 关键段范式：落盘超时降级为受控日志（已完成步骤已落库），不让控制器 500。
                _logger.LogWarning(ex, "Quota reset persistence timeout for account {Id}", codexAccountId);
            }
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
