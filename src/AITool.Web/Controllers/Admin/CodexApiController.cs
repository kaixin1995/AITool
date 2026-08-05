using System.Collections.Concurrent;
using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Domain.Codex;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// Codex 账号管理 API。集中暴露 OAuth 登录、凭证导入、账号列表、额度查询/重置、启用禁用、删除、编辑等。
/// 路由前缀 /api/admin/codex，自动受 /api/admin/* 鉴权保护。
/// 受 Codex 功能总开关保护：关闭时全部返回 404。
/// </summary>
[ApiController]
[Route("api/admin/codex")]
[ServiceFilter(typeof(CodexFeatureToggleAttribute))]
public sealed class CodexApiController : ControllerBase
{
    // —— OAuth 会话暂存（state → verifier，TTL 10min）——
    private sealed record OAuthSession(string State, string Verifier, DateTimeOffset ExpiresAt);
    private static readonly ConcurrentDictionary<string, OAuthSession> Sessions = new();

    private readonly AppDbContext _dbContext;
    private readonly ICodexOAuthClient _oauth;
    private readonly CodexAccountProvisioner _provisioner;
    private readonly ICodexModelFetcher _modelFetcher;
    private readonly ICodexQuotaService _quotaService;
    private readonly ICodexQuotaCooldownService _cooldownService;
    private readonly ICodexResetCreditsService _resetCreditsService;
    private readonly CodexInspectionService _inspectionService;
    private readonly ILogger<CodexApiController> _logger;
    private readonly ProxyRequestMetadataCache _metadataCache;

    public CodexApiController(
        AppDbContext dbContext,
        ICodexOAuthClient oauth,
        CodexAccountProvisioner provisioner,
        ICodexModelFetcher modelFetcher,
        ICodexQuotaService quotaService,
        ICodexQuotaCooldownService cooldownService,
        ICodexResetCreditsService resetCreditsService,
        CodexInspectionService inspectionService,
        ILogger<CodexApiController> logger,
        ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _provisioner = provisioner;
        _modelFetcher = modelFetcher;
        _quotaService = quotaService;
        _cooldownService = cooldownService;
        _resetCreditsService = resetCreditsService;
        _inspectionService = inspectionService;
        _logger = logger;
        _metadataCache = metadataCache;
    }

    /// <summary>启动 OAuth 登录，返回授权 URL 与 state。</summary>
    [HttpPost("start-oauth")]
    public IActionResult StartOAuth([FromBody] StartOAuthRequest? req)
    {
        CleanupExpiredSessions();
        var (state, verifier) = _oauth.CreateOAuthSession();
        var url = _oauth.BuildAuthorizeUrl(state, verifier);
        Sessions[state] = new OAuthSession(state, verifier, DateTimeOffset.UtcNow.AddMinutes(10));
        return Ok(new { url, state });
    }

    /// <summary>完成 OAuth 登录：用户粘贴回调 URL，校验 state 后交换 token 并建账号。</summary>
    [HttpPost("complete-oauth")]
    public async Task<IActionResult> CompleteOAuth([FromBody] CompleteOAuthRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.CallbackUrl))
        {
            return BadRequest(new { message = "回调 URL 不能为空" });
        }

        string code;
        string state;
        try
        {
            var uri = new Uri(req.CallbackUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            code = query["code"] ?? string.Empty;
            state = query["state"] ?? string.Empty;
        }
        catch
        {
            return BadRequest(new { message = "回调 URL 格式无法解析" });
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return BadRequest(new { message = "回调 URL 缺少 code 或 state" });
        }

        if (!Sessions.TryRemove(state, out var session))
        {
            return BadRequest(new { message = "state 无效或已过期（10 分钟），请重新开始登录" });
        }
        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return BadRequest(new { message = "state 已过期，请重新开始登录" });
        }

        CodexTokenSet tokens;
        try
        {
            tokens = await _oauth.ExchangeCodeAsync(code, session.Verifier, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex OAuth code exchange failed");
            return BadRequest(new { message = "授权码交换失败：" + ex.Message });
        }

        var claims = CodexJwtParser.Parse(tokens.IdToken);
        var input = new CodexProvisionInput
        {
            DisplayName = !string.IsNullOrWhiteSpace(req.DisplayName) ? req.DisplayName : (claims?.Email ?? "Codex 账号"),
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            IdToken = tokens.IdToken,
            AccountId = claims?.AccountId,
            Email = claims?.Email,
            PlanType = claims?.PlanType,
            TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600),
        };
        var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
        return Ok(ToSummary(account));
    }

    /// <summary>导入凭证文件（multipart 多文件 或 raw body 单文件）。</summary>
    [HttpPost("import-credential")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ImportCredential([FromQuery] string? name, CancellationToken ct)
    {
        var parseResults = new List<CodexCredentialParseResult>();

        if (Request.HasFormContentType && Request.Form.Files.Count > 0)
        {
            foreach (var file in Request.Form.Files)
            {
                using var sr = new StreamReader(file.OpenReadStream());
                var json = await sr.ReadToEndAsync(ct);
                parseResults.Add(CodexCredentialParser.Parse(json, file.FileName));
            }
        }
        else
        {
            using var sr = new StreamReader(Request.Body);
            var json = await sr.ReadToEndAsync(ct);
            parseResults.Add(CodexCredentialParser.Parse(json, name));
        }

        var summaries = new List<object>();
        foreach (var r in parseResults.Where(r => r.Success))
        {
            var input = new CodexProvisionInput
            {
                DisplayName = r.DisplayName ?? "Codex 账号",
                AccessToken = r.AccessToken ?? string.Empty,
                RefreshToken = r.RefreshToken ?? string.Empty,
                IdToken = r.IdToken ?? string.Empty,
                AccountId = r.AccountId,
                Email = r.Email,
                PlanType = r.PlanType,
                TokenExpiresAt = r.TokenExpiresAt,
            };
            var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
            summaries.Add(ToSummary(account));
        }

        var failures = parseResults.Where(r => !r.Success).Select(r => new { r.FileName, r.Error }).ToList();
        if (failures.Count > 0)
        {
            return StatusCode(207, new { successes = summaries, failures });
        }
        return Ok(new { successes = summaries });
    }

    /// <summary>列出全部 Codex 账号（含状态/额度缓存字段）。</summary>
    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts(CancellationToken ct)
    {
        var accounts = await _dbContext.CodexAccounts
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        return Ok(accounts.Select(ToSummary).ToList());
    }

    /// <summary>查询指定账号额度（forceRefresh 穿透 30s 缓存）。</summary>
    [HttpPost("accounts/{id}/refresh-quota")]
    public async Task<IActionResult> RefreshQuota(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var info = await _quotaService.QueryAsync(account, forceRefresh: true, ct);
        return Ok(info);
    }

    /// <summary>重置额度（清冷却 + 刷新 token + 恢复）。前端需二次确认。</summary>
    [HttpPost("accounts/{id}/reset-quota")]
    public async Task<IActionResult> ResetQuota(Guid id, CancellationToken ct)
    {
        try
        {
            await _cooldownService.ResetAsync(id, ct);
            return Ok(new { message = "已重置" });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "账号不存在" });
        }
    }

    /// <summary>切换启用/禁用。</summary>
    [HttpPost("accounts/{id}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        account.IsEnabled = !account.IsEnabled;
        // 手动禁用打标记，避免巡检「额度恢复」时误自动启用；
        // 手动启用则清除标记，恢复巡检的自动恢复能力。
        account.ManuallyDisabled = !account.IsEnabled;
        await _dbContext.UpdateAsync(account, ct);

        var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
        if (site != null)
        {
            site.IsEnabled = account.IsEnabled;
            await _dbContext.UpdateAsync(site, ct);
        }
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateCodexAccounts();
        return Ok(ToSummary(account));
    }

    /// <summary>删除账号（级联删隐藏 Site + 映射 + 路由规则）。</summary>
    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _provisioner.DeprovisionAsync(id, ct);
            return Ok(new { message = "已删除" });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "账号不存在" });
        }
    }

    /// <summary>编辑账号（当前仅支持修改名称）。</summary>
    [HttpPut("accounts/{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAccountRequest req, CancellationToken ct)
    {
        try
        {
            await _provisioner.UpdateAsync(id, req.DisplayName, ct);
            var account = await GetAccountAsync(id, ct);
            return Ok(ToSummary(account!));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "账号不存在" });
        }
    }

    /// <summary>手动刷新 token。</summary>
    [HttpPost("accounts/{id}/refresh-token")]
    public async Task<IActionResult> RefreshToken(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });
        if (string.IsNullOrEmpty(account.RefreshToken))
        {
            return BadRequest(new { message = "账号无 refresh_token" });
        }

        try
        {
            var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, ct);
            account.AccessToken = tokens.AccessToken;
            account.RefreshToken = tokens.RefreshToken;
            if (!string.IsNullOrEmpty(tokens.IdToken)) account.IdToken = tokens.IdToken;
            account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            await _dbContext.UpdateAsync(account, ct);

            var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.ApiKey = tokens.AccessToken;
                await _dbContext.UpdateAsync(site, ct);
            }
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateCodexAccounts();
            return Ok(ToSummary(account));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "刷新失败：" + ex.Message });
        }
    }

    /// <summary>拉取该账号的上游模型列表（预览，不立即导入）。</summary>
    [HttpGet("accounts/{id}/fetch-models")]
    public async Task<IActionResult> FetchModels(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });
        if (string.IsNullOrEmpty(account.AccessToken))
        {
            return BadRequest(new { message = "账号无 access_token" });
        }

        try
        {
            var remoteModels = (await _modelFetcher.FetchAsync(account.AccessToken, account.AccountId ?? string.Empty, ct)).ToList();
            var existingMappings = await _dbContext.SiteModelMappings
                .Where(m => m.SiteId == account.LinkedSiteId)
                .ToListAsync(ct);

            var remoteNames = remoteModels.Select(m => m.Slug).ToList();
            var modelItems = await _dbContext.ModelLibraryItems
                .Where(m => remoteNames.Contains(m.ModelName))
                .ToListAsync(ct);

            // 使用 Dictionary 优化 Join，避免 O(n²) 复杂度
            var mappingDict = existingMappings.ToDictionary(m => m.RemoteModelName);
            var modelItemDict = modelItems.ToDictionary(m => m.ModelName);

            var result = new List<object>();
            foreach (var remote in remoteModels)
            {
                mappingDict.TryGetValue(remote.Slug, out var mapping);
                modelItemDict.TryGetValue(remote.Slug, out var modelItem);
                var hasValidImport = mapping != null && modelItem != null && mapping.ModelLibraryItemId == modelItem.Id;

                result.Add(new
                {
                    remoteModelName = remote.Slug,
                    displayName = remote.DisplayName,
                    existingMappingId = hasValidImport ? mapping!.Id : (Guid?)null,
                    isEnabled = hasValidImport && mapping!.IsEnabled,
                    existingDisplayName = modelItem?.DisplayName
                });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fetch Codex models failed for account {AccountId}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>导入选中的 Codex 模型（用户已在前端选择）。</summary>
    [HttpPost("accounts/{id}/import-selected-models")]
    public async Task<IActionResult> ImportSelectedModels(Guid id, [FromBody] ImportCodexModelsRequest request, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var selected = request.Selections.Where(s => s.Selected).ToList();
        if (selected.Count == 0) return BadRequest(new { message = "请至少选择一个模型" });

        var modelsToImport = selected.Select(s => (s.RemoteModelName, string.IsNullOrWhiteSpace(s.DisplayName) ? s.RemoteModelName : s.DisplayName)).ToList();
        await _provisioner.UpsertRemoteModelsAsync(account.LinkedSiteId, modelsToImport, ct);

        return Ok(new { importedCount = modelsToImport.Count });
    }

    public sealed class ImportCodexModelsRequest
    {
        public List<CodexModelSelection> Selections { get; set; } = [];
    }

    public sealed class CodexModelSelection
    {
        public string RemoteModelName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool Selected { get; set; }
    }

    // —— 巡检 ——

    /// <summary>触发一轮巡检。force=true 强制真实刷新全部账号；false 允许命中缓存。</summary>
    [HttpPost("inspection/run")]
    [ServiceFilter(typeof(CodexInspectionToggleAttribute))]
    public async Task<IActionResult> RunInspection([FromQuery] bool force, CancellationToken ct)
    {
        var result = await _inspectionService.RunManualAsync(force, ct);
        return Ok(result);
    }

    /// <summary>巡检状态（是否运行中、下次调度时间、上次完成时间）。</summary>
    [HttpGet("inspection/status")]
    [ServiceFilter(typeof(CodexInspectionToggleAttribute))]
    public IActionResult InspectionStatus()
    {
        return Ok(_inspectionService.GetStatus());
    }

    /// <summary>上次巡检结果（每账号动作/原因/百分比）。</summary>
    [HttpGet("inspection/last-run")]
    [ServiceFilter(typeof(CodexInspectionToggleAttribute))]
    public IActionResult InspectionLastRun()
    {
        return Ok(_inspectionService.GetLastRun());
    }

    /// <summary>巡检操作日志（最新在前）。</summary>
    [HttpGet("inspection/logs")]
    [ServiceFilter(typeof(CodexInspectionToggleAttribute))]
    public IActionResult InspectionLogs()
    {
        return Ok(_inspectionService.GetLogs());
    }

    // —— Reset Credits ——

    /// <summary>查询账号的手动重置 credits（剩余次数 + 每张过期时间）。</summary>
    [HttpGet("accounts/{id}/reset-credits")]
    public async Task<IActionResult> GetResetCredits(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var info = await _resetCreditsService.QueryResetCreditsAsync(account, ct);
        return Ok(info);
    }

    /// <summary>消耗一张 reset credit，执行真实额度重置。</summary>
    [HttpPost("accounts/{id}/consume-reset-credit")]
    public async Task<IActionResult> ConsumeResetCredit(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var redeemRequestId = Guid.NewGuid().ToString();
        var (success, error) = await _resetCreditsService.ConsumeResetCreditAsync(account, redeemRequestId, ct);
        if (!success) return BadRequest(new { message = error });

        // 消耗成功后重新刷新额度（让前端能看到重置后的新额度）
        await _quotaService.QueryAsync(account, forceRefresh: true, ct);

        return Ok(new { message = "手动重置额度成功" });
    }

    // —— 导出凭证 ——

    /// <summary>导出选中账号的凭证（access_token / refresh_token / id_token 等）。</summary>
    [HttpPost("accounts/export-credentials")]
    public async Task<IActionResult> ExportCredentials([FromBody] ExportCredentialsRequest req, CancellationToken ct)
    {
        if (req?.AccountIds == null || req.AccountIds.Count == 0)
        {
            return BadRequest(new { message = "请至少选择一个账号" });
        }

        var accounts = await _dbContext.CodexAccounts
            .Where(a => req.AccountIds.Contains(a.Id))
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            return NotFound(new { message = "未找到匹配的账号" });
        }

        var credentials = accounts.Select(a => new
        {
            account_id = a.AccountId,
            email = a.Email,
            display_name = a.DisplayName,
            plan_type = a.PlanType,
            access_token = a.AccessToken,
            refresh_token = a.RefreshToken,
            id_token = a.IdToken,
            token_expires_at = a.TokenExpiresAt?.ToString("o"),
            created_at = a.CreatedAt.ToString("o"),
        }).ToList();

        return Ok(new { credentials });
    }

    // —— 私有 ——

    private async Task<CodexAccount?> GetAccountAsync(Guid id, CancellationToken ct)
    {
        return (await _dbContext.CodexAccounts.Where(a => a.Id == id).ToListAsync(ct)).FirstOrDefault();
    }

    private static object ToSummary(CodexAccount a)
    {
        // 从最近一次额度查询的原始响应解析窗口（供前端画进度条，无需每次单独刷新）
        List<object>? windows = null;
        double? fiveHour = null;
        double? weekly = null;
        int? resetCreditsAvailableCount = null;
        if (!string.IsNullOrEmpty(a.LastQuotaRawJson))
        {
            try
            {
                var (planType, parsedWindows) = CodexUsageParser.Parse(a.LastQuotaRawJson);
                windows = parsedWindows.Select(w => (object)new
                {
                    id = w.Id, label = w.Label,
                    usedPercent = w.UsedPercent, resetLabel = w.ResetLabel,
                }).ToList();
                fiveHour = parsedWindows.FirstOrDefault(w => w.Id == "five-hour")?.UsedPercent;
                weekly = parsedWindows.FirstOrDefault(w => w.Id == "weekly")?.UsedPercent;

                // 解析 rate_limit_reset_credits.available_count（如果存在）
                var json = System.Text.Json.JsonDocument.Parse(a.LastQuotaRawJson);
                if (json.RootElement.TryGetProperty("rate_limit_reset_credits", out var rlrcEl) ||
                    json.RootElement.TryGetProperty("rateLimitResetCredits", out rlrcEl))
                {
                    if (rlrcEl.TryGetProperty("available_count", out var countEl) ||
                        rlrcEl.TryGetProperty("availableCount", out countEl))
                    {
                        if (countEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            resetCreditsAvailableCount = countEl.GetInt32();
                        }
                        else if (countEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            if (int.TryParse(countEl.GetString(), out var c)) resetCreditsAvailableCount = c;
                        }
                    }
                }
            }
            catch { }
        }

        return new
        {
            id = a.Id,
            displayName = a.DisplayName,
            email = a.Email,
            accountId = a.AccountId,
            planType = a.PlanType,
            isEnabled = a.IsEnabled,
            isQuotaCooling = a.IsQuotaCooling,
            quotaCoolingUntil = a.QuotaCoolingUntil,
            autoDisableThreshold = a.AutoDisableThreshold,
            windows,
            fiveHourUsedPercent = fiveHour,
            weeklyUsedPercent = weekly,
            resetCreditsAvailableCount,
            lastQuotaCheckedAt = a.LastQuotaCheckedAt,
            tokenExpiresAt = a.TokenExpiresAt,
            createdAt = a.CreatedAt,
        };
    }

    private static void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Sessions)
        {
            if (kv.Value.ExpiresAt < now) Sessions.TryRemove(kv.Key, out _);
        }
    }
}

// —— 请求 DTO ——

public sealed class StartOAuthRequest
{
    public string? DisplayName { get; set; }
}

public sealed class CompleteOAuthRequest
{
    /// <summary>用户粘贴的回调 URL（含 code/state 查询参数）。</summary>
    public string? CallbackUrl { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class UpdateAccountRequest
{
    public string? DisplayName { get; set; }
}

public sealed class ExportCredentialsRequest
{
    public List<Guid> AccountIds { get; set; } = [];
}
